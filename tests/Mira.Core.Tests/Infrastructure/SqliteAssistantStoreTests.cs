namespace Mira.Core.Tests.Infrastructure;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Storage;
using Xunit;

public sealed class SqliteAssistantStoreTests
{
    [Fact]
    public async Task SaveRawCaptureAsync_stores_immutable_source_and_returns_path()
    {
        var fixture = await StoreFixture.CreateAsync();

        var sourcePath = await fixture.Store.SaveRawCaptureAsync("raw capture", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.True(sourcePath.StartsWith("0-raw/", StringComparison.Ordinal) || sourcePath.StartsWith("db://raw_captures/", StringComparison.Ordinal));
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE content = @content;";
        command.Parameters.AddWithValue("@content", "raw capture");
        var count = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task SaveSourceCaptureAsync_persists_and_reads_recent_sources()
    {
        var fixture = await StoreFixture.CreateAsync();
        var older = new DateTimeOffset(2026, 7, 1, 4, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(30);

        await fixture.Store.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Older note",
            "Older content",
            older,
            Metadata: new Dictionary<string, string> { ["chat_id"] = "123" }), TestContext.Current.CancellationToken);
        await fixture.Store.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Newer note",
            "Newer content",
            newer,
            Metadata: new Dictionary<string, string> { ["chat_id"] = "456" }), TestContext.Current.CancellationToken);

        var captures = await fixture.Store.GetRecentSourceCapturesAsync(20, null, TestContext.Current.CancellationToken);

        Assert.Collection(
            captures,
            capture =>
            {
                Assert.Equal("Newer note", capture.Title);
                Assert.Equal(SourceKind.TelegramMessage, capture.Kind);
                Assert.Equal("Newer content", capture.ContentText);
                Assert.NotEmpty(capture.ContentHash);
                Assert.Equal(SourceProcessingStatus.Unprocessed, capture.Status);
                Assert.Equal("456", capture.Metadata["chat_id"]);
            },
            capture =>
            {
                Assert.Equal("Older note", capture.Title);
                Assert.Equal("Older content", capture.ContentText);
                Assert.Equal("123", capture.Metadata["chat_id"]);
            });
    }

    [Fact]
    public async Task GetRecentSourceCapturesAsync_filters_by_status()
    {
        var fixture = await StoreFixture.CreateAsync();
        var processed = await fixture.Store.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Processed note",
            "Processed content",
            new DateTimeOffset(2026, 7, 1, 4, 0, 0, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        await fixture.Store.MarkSourceCaptureProcessedAsync(processed.Id, new DateTimeOffset(2026, 7, 1, 4, 5, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);
        var unprocessed = await fixture.Store.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Unprocessed note",
            "Unprocessed content",
            new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero)), TestContext.Current.CancellationToken);

        var unprocessedCaptures = await fixture.Store.GetRecentSourceCapturesAsync(20, SourceProcessingStatus.Unprocessed, TestContext.Current.CancellationToken);
        var processedCaptures = await fixture.Store.GetRecentSourceCapturesAsync(20, SourceProcessingStatus.Processed, TestContext.Current.CancellationToken);

        Assert.Equal(unprocessed.Id, Assert.Single(unprocessedCaptures).Id);
        var processedCapture = Assert.Single(processedCaptures);
        Assert.Equal(processed.Id, processedCapture.Id);
        Assert.Equal(SourceProcessingStatus.Processed, processedCapture.Status);
        Assert.NotNull(processedCapture.ProcessedAtUtc);
    }

    [Fact]
    public async Task UpsertAsync_with_sourceCaptureId_creates_memory_source_link()
    {
        var fixture = await StoreFixture.CreateAsync();
        var source = await fixture.Store.SaveSourceCaptureAsync(new SourceCaptureCreate(
            SourceKind.TelegramMessage,
            "Maxim keyboard source",
            "Maxim likes keyboards",
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var sourcePath = await fixture.Store.SaveRawCaptureAsync("Maxim likes keyboards", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var memory = await fixture.Store.UpsertAsync(new MemoryUpsert(
            MemoryCategory.Person,
            "Maxim keyboard preferences",
            "Maxim likes mechanical keyboards",
            "Maxim",
            ["keyboard"],
            0.9,
            null,
            sourcePath), source.Id, TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM memory_source_links
WHERE memory_id = $memory_id AND source_capture_id = $source_id AND relationship = 'derived_from';
""";
        command.Parameters.AddWithValue("$memory_id", memory.Id.ToString());
        command.Parameters.AddWithValue("$source_id", source.Id.ToString());
        var count = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task UpsertAndSearchAsync_returns_saved_memory_by_subject()
    {
        var fixture = await StoreFixture.CreateAsync();
        var sourcePath = await fixture.Store.SaveRawCaptureAsync("Maxim likes keyboards", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await fixture.Store.UpsertAsync(new MemoryUpsert(
            MemoryCategory.Person,
            "Maxim keyboard preferences",
            "Maxim likes mechanical keyboards and artisan keycaps",
            "Maxim",
            ["keyboard"],
            0.9,
            null,
            sourcePath), cancellationToken: TestContext.Current.CancellationToken);

        var results = await fixture.Store.SearchAsync(new MemorySearchQuery("Maxim keyboard", [], 5), TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(MemoryCategory.Person, result.Category);
        Assert.Equal("Maxim keyboard preferences", result.Title);
        Assert.Contains("mechanical keyboards", result.Content, StringComparison.Ordinal);
        Assert.Equal(sourcePath, result.SourcePath);
    }

    [Fact]
    public async Task GetRecentConversationAsync_filters_limits_and_orders_dialogue()
    {
        var fixture = await StoreFixture.CreateAsync();
        var cutoff = new DateTimeOffset(2026, 7, 1, 10, 5, 0, TimeSpan.Zero);

        await fixture.Store.SaveConversationMessageAsync(new ConversationMessage(42, 100, ConversationDirection.Incoming, "oldest included", cutoff.AddMinutes(-7)), TestContext.Current.CancellationToken);
        await fixture.Store.SaveConversationMessageAsync(new ConversationMessage(42, 101, ConversationDirection.Outgoing, "middle included", cutoff.AddMinutes(-4)), TestContext.Current.CancellationToken);
        await fixture.Store.SaveConversationMessageAsync(new ConversationMessage(7, 200, ConversationDirection.Incoming, "different chat", cutoff.AddMinutes(-3)), TestContext.Current.CancellationToken);
        await fixture.Store.SaveConversationMessageAsync(new ConversationMessage(42, 102, ConversationDirection.Incoming, "newest included", cutoff.AddMinutes(-1)), TestContext.Current.CancellationToken);
        await fixture.Store.SaveConversationMessageAsync(new ConversationMessage(42, 103, ConversationDirection.Outgoing, "at cutoff excluded", cutoff), TestContext.Current.CancellationToken);
        await fixture.Store.SaveConversationMessageAsync(new ConversationMessage(42, 104, ConversationDirection.Incoming, "after cutoff excluded", cutoff.AddMinutes(1)), TestContext.Current.CancellationToken);

        var messages = await fixture.Store.GetRecentConversationAsync(42, 2, cutoff, TestContext.Current.CancellationToken);

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal(101, message.MessageId);
                Assert.Equal("middle included", message.Content);
            },
            message =>
            {
                Assert.Equal(102, message.MessageId);
                Assert.Equal("newest included", message.Content);
            });
    }

    [Fact]
    public async Task UpsertAndDeleteAsync_refresh_memory_dashboard_mirror()
    {
        var fixture = await StoreFixture.CreateAsync();
        var sourcePath = await fixture.Store.SaveRawCaptureAsync("Maxim likes keyboards", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var item = await fixture.Store.UpsertAsync(new MemoryUpsert(
            MemoryCategory.Person,
            "Maxim keyboard preferences",
            "Maxim likes mechanical keyboards and artisan keycaps",
            "Maxim",
            ["keyboard"],
            0.9,
            null,
            sourcePath), cancellationToken: TestContext.Current.CancellationToken);

        var dashboardDirectory = Path.Combine(fixture.KnowledgeRootPath, "0-dashboard");
        var dashboardPath = Path.Combine(dashboardDirectory, "memory.md");
        var htmlDashboardPath = Path.Combine(dashboardDirectory, "memory.html");
        Assert.True(Directory.Exists(dashboardDirectory));
        Assert.True(File.Exists(dashboardPath));
        var markdown = await File.ReadAllTextAsync(dashboardPath, TestContext.Current.CancellationToken);
        Assert.Contains("# Mira Memory Dashboard", markdown);
        Assert.Contains("item_count: 1", markdown);
        Assert.Contains("## At a glance", markdown);
        Assert.Contains("- Total memories in this dashboard: 1", markdown);
        Assert.Contains("- Person: 1", markdown);
        Assert.Contains("## Recent memories", markdown);
        Assert.Contains("[Maxim keyboard preferences](../2-atoms/Person/", markdown);
        Assert.Contains("Maxim likes mechanical keyboards", markdown);
        Assert.True(File.Exists(htmlDashboardPath));
        var html = await File.ReadAllTextAsync(htmlDashboardPath, TestContext.Current.CancellationToken);
        Assert.Contains("http-equiv=\"refresh\" content=\"5\"", html);
        Assert.Contains("Mira Memory Dashboard", html);
        Assert.Contains("Maxim keyboard preferences", html);
        Assert.Contains("../2-atoms/Person/", html);

        await fixture.Store.DeleteAsync(item.Id, TestContext.Current.CancellationToken);

        var updated = await File.ReadAllTextAsync(dashboardPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Maxim keyboard preferences", updated);
        Assert.Contains("item_count: 0", updated);
        Assert.Contains("No saved memories yet.", updated);
        var updatedHtml = await File.ReadAllTextAsync(htmlDashboardPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Maxim keyboard preferences", updatedHtml);
        Assert.Contains("No saved memories yet", updatedHtml);
    }


    [Fact]
    public async Task GetDueAsync_returns_only_pending_due_reminders()
    {
        var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var due = await fixture.Store.AddAsync(new ReminderCreateRequest("past", null, now.AddMinutes(-5), ReminderRepeatKind.None), TestContext.Current.CancellationToken);
        await fixture.Store.AddAsync(new ReminderCreateRequest("future", null, now.AddMinutes(5), ReminderRepeatKind.None), TestContext.Current.CancellationToken);
        var cancelled = await fixture.Store.AddAsync(new ReminderCreateRequest("cancelled", null, now.AddMinutes(-10), ReminderRepeatKind.None), TestContext.Current.CancellationToken);
        await fixture.Store.CancelAsync(cancelled.Id, TestContext.Current.CancellationToken);

        var reminders = await fixture.Store.GetDueAsync(now, 10, TestContext.Current.CancellationToken);

        var reminder = Assert.Single(reminders);
        Assert.Equal(due.Id, reminder.Id);
    }

    [Fact]
    public async Task RepeatedReminder_MarkSent_keeps_pending_with_next_due()
    {
        var fixture = await StoreFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var reminder = await fixture.Store.AddAsync(new ReminderCreateRequest("daily", null, now.AddMinutes(-1), ReminderRepeatKind.Daily), TestContext.Current.CancellationToken);
        var nextDue = now.AddDays(1);

        await fixture.Store.MarkSentAsync(reminder.Id, now, nextDue, TestContext.Current.CancellationToken);

        var pending = await fixture.Store.GetPendingAsync(10, TestContext.Current.CancellationToken);
        var updated = Assert.Single(pending);
        Assert.Equal(reminder.Id, updated.Id);
        Assert.Equal(ReminderStatus.Pending, updated.Status);
        Assert.Equal(nextDue.ToUniversalTime(), updated.DueAtUtc);
    }

    [Fact]
    public async Task TryRecordRunAsync_allows_period_once()
    {
        var fixture = await StoreFixture.CreateAsync();

        var first = await fixture.Store.TryRecordRunAsync("daily-brief", "20260701", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var second = await fixture.Store.TryRecordRunAsync("daily-brief", "20260701", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task InitializeAsync_creates_personal_os_vault_scaffold_without_overwriting_system_files()
    {
        var root = Directory.CreateTempSubdirectory("mira-vault-tests-");
        var knowledgeRoot = Path.Combine(root.FullName, "knowledge");
        var systemRoot = Path.Combine(knowledgeRoot, "_system");
        Directory.CreateDirectory(systemRoot);
        var profilePath = Path.Combine(systemRoot, "profile.md");
        await File.WriteAllTextAsync(profilePath, "custom profile", TestContext.Current.CancellationToken);
        var settings = new StorageSettings
        {
            DatabasePath = Path.Combine(root.FullName, "mira.db"),
            KnowledgeRootPath = knowledgeRoot,
            EnableMarkdownMirror = true
        };
        var initializer = new SqliteSchemaInitializer(Options.Create(settings));

        await initializer.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "0-raw")));
        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "0-dashboard")));
        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "sources")));
        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "1-desk")));
        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "2-atoms")));
        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "3-threads")));
        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "briefings")));
        Assert.True(Directory.Exists(Path.Combine(knowledgeRoot, "_system", "skills")));
        Assert.Contains("No saved memories yet.", await File.ReadAllTextAsync(Path.Combine(knowledgeRoot, "0-dashboard", "memory.md"), TestContext.Current.CancellationToken));
        Assert.Equal("custom profile", await File.ReadAllTextAsync(profilePath, TestContext.Current.CancellationToken));
        Assert.Contains("Mira House Rules", await File.ReadAllTextAsync(Path.Combine(systemRoot, "house-rules.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeAsync_preserves_existing_dashboard_file()
    {
        var root = Directory.CreateTempSubdirectory("mira-dashboard-tests-");
        var knowledgeRoot = Path.Combine(root.FullName, "knowledge");
        var dashboardRoot = Path.Combine(knowledgeRoot, "0-dashboard");
        Directory.CreateDirectory(dashboardRoot);
        var dashboardPath = Path.Combine(dashboardRoot, "memory.md");
        await File.WriteAllTextAsync(dashboardPath, "custom dashboard", TestContext.Current.CancellationToken);
        var settings = new StorageSettings
        {
            DatabasePath = Path.Combine(root.FullName, "mira.db"),
            KnowledgeRootPath = knowledgeRoot,
            EnableMarkdownMirror = true
        };
        var initializer = new SqliteSchemaInitializer(Options.Create(settings));

        await initializer.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("custom dashboard", await File.ReadAllTextAsync(dashboardPath, TestContext.Current.CancellationToken));
    }


    internal sealed record StoreFixture(SqliteAssistantStore Store, string ConnectionString, string KnowledgeRootPath)
    {
        public static async Task<StoreFixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("mira-tests-");
            var settings = new StorageSettings
            {
                DatabasePath = Path.Combine(root.FullName, "mira.db"),
                KnowledgeRootPath = Path.Combine(root.FullName, "knowledge"),
                EnableMarkdownMirror = true
            };
            var options = Options.Create(settings);
            var writer = new KnowledgeMarkdownWriter(options, NullLogger<KnowledgeMarkdownWriter>.Instance);
            var initializer = new SqliteSchemaInitializer(options);
            await initializer.InitializeAsync();
            var store = new SqliteAssistantStore(options, writer, NullLogger<SqliteAssistantStore>.Instance);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = settings.DatabasePath }.ToString();
            return new StoreFixture(store, connectionString, settings.KnowledgeRootPath);
        }
    }
}

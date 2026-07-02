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
            sourcePath), TestContext.Current.CancellationToken);

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
            sourcePath), TestContext.Current.CancellationToken);

        var dashboardPath = Path.Combine(fixture.KnowledgeRootPath, "0-dashboard", "memory.md");
        Assert.True(File.Exists(dashboardPath));
        var markdown = await File.ReadAllTextAsync(dashboardPath, TestContext.Current.CancellationToken);
        Assert.Contains("# Mira Memory Dashboard", markdown);
        Assert.Contains("- Person: 1", markdown);
        Assert.Contains("[Maxim keyboard preferences](../2-atoms/Person/", markdown);
        Assert.Contains("Maxim likes mechanical keyboards", markdown);

        await fixture.Store.DeleteAsync(item.Id, TestContext.Current.CancellationToken);

        var updated = await File.ReadAllTextAsync(dashboardPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Maxim keyboard preferences", updated);
        Assert.Contains("No saved memories yet.", updated);
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

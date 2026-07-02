namespace Mira.Infrastructure.Storage;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;

public sealed class SqliteAssistantStore : IMemoryStore, IReminderStore, IProactiveRunStore, IAutomationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly StorageSettings _settings;
    private readonly KnowledgeMarkdownWriter _markdownWriter;
    private readonly ILogger<SqliteAssistantStore> _logger;
    private readonly string _connectionString;

    public SqliteAssistantStore(
        IOptions<StorageSettings> options,
        KnowledgeMarkdownWriter markdownWriter,
        ILogger<SqliteAssistantStore> logger)
    {
        _settings = options.Value;
        _markdownWriter = markdownWriter;
        _logger = logger;
        _connectionString = StoragePathResolver.BuildConnectionString(_settings);
    }

    public async Task SaveConversationMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO conversation_messages (chat_id, telegram_message_id, direction, content, created_utc)
VALUES (@chat_id, @telegram_message_id, @direction, @content, @created_utc);
""";
        Add(command, "@chat_id", message.ChatId);
        Add(command, "@telegram_message_id", message.MessageId);
        Add(command, "@direction", message.Direction.ToString());
        Add(command, "@content", message.Content);
        Add(command, "@created_utc", FormatUtc(message.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SaveRawCaptureAsync(string content, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = Guid.NewGuid();
            var sourcePath = BuildRawCapturePath(id, createdAtUtc);
            if (_settings.EnableMarkdownMirror && _markdownWriter.SourcePathExists(sourcePath))
            {
                continue;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
INSERT INTO raw_captures (id, source_path, content, content_hash, created_utc)
VALUES (@id, @source_path, @content, @content_hash, @created_utc);
""";
            Add(command, "@id", id.ToString());
            Add(command, "@source_path", sourcePath);
            Add(command, "@content", content);
            Add(command, "@content_hash", ComputeSha256(content));
            Add(command, "@created_utc", FormatUtc(createdAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var wroteMirror = await _markdownWriter.TryWriteRawCaptureAsync(sourcePath, content, cancellationToken).ConfigureAwait(false);
            if (wroteMirror)
            {
                return sourcePath;
            }

            var dbSourcePath = $"db://raw_captures/{id}";
            await UpdateRawCaptureSourcePathAsync(connection, id, dbSourcePath, cancellationToken).ConfigureAwait(false);
            return dbSourcePath;
        }

        throw new InvalidOperationException("Could not allocate a unique raw capture source path.");
    }

    public async Task<MemoryItem> UpsertAsync(MemoryUpsert request, CancellationToken cancellationToken = default)
    {
        if (request.SourceMessageId is null && string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new ArgumentException("A memory item must reference either a source message or a source path.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var item = new MemoryItem(
            Guid.NewGuid(),
            request.Category,
            request.Title.Trim(),
            request.Content.Trim(),
            string.IsNullOrWhiteSpace(request.Subject) ? null : request.Subject.Trim(),
            CleanTags(request.Tags),
            Math.Clamp(request.Confidence, 0.0, 1.0),
            request.SourceMessageId,
            string.IsNullOrWhiteSpace(request.SourcePath) ? null : request.SourcePath.Trim(),
            now,
            now);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
INSERT INTO memory_items (id, category, title, subject, content, tags_json, confidence, source_message_id, source_path, created_utc, updated_utc)
VALUES (@id, @category, @title, @subject, @content, @tags_json, @confidence, @source_message_id, @source_path, @created_utc, @updated_utc);
""";
                AddMemoryParameters(command, item);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await DeleteFtsRowAsync(connection, transaction, item.Id, cancellationToken).ConfigureAwait(false);
            await InsertFtsRowAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        await _markdownWriter.WriteAtomAsync(item, cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<MemoryItem>> SearchAsync(MemorySearchQuery query, CancellationToken cancellationToken = default)
    {
        var tokens = SanitizeTokens(query.Text);
        if (tokens.Count == 0)
        {
            return await GetRecentByCategoriesAsync(query.Categories, query.Limit, null, cancellationToken).ConfigureAwait(false);
        }

        var limit = NormalizeLimit(query.Limit);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var categoryFilter = AddCategoryFilter(command, query.Categories, "m");
        command.CommandText = $$"""
SELECT m.id, m.category, m.title, m.subject, m.content, m.tags_json, m.confidence, m.source_message_id, m.source_path, m.created_utc, m.updated_utc
FROM memory_item_fts f
JOIN memory_items m ON m.id = f.memory_item_id
WHERE memory_item_fts MATCH @query{{categoryFilter}}
ORDER BY rank, m.updated_utc DESC
LIMIT @limit;
""";
        Add(command, "@query", string.Join(" OR ", tokens.Select(token => token + "*")));
        Add(command, "@limit", limit);

        try
        {
            return await ReadMemoryItemsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Memory FTS search failed; falling back to recent memories.");
            return await GetRecentByCategoriesAsync(query.Categories, query.Limit, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<MemoryItem>> GetRecentAsync(int limit, DateTimeOffset? sinceUtc = null, CancellationToken cancellationToken = default)
    {
        return await GetRecentByCategoriesAsync([], limit, sinceUtc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryItem>> GetByCategoryAsync(MemoryCategory category, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, category, title, subject, content, tags_json, confidence, source_message_id, source_path, created_utc, updated_utc
FROM memory_items
WHERE category = @category
ORDER BY updated_utc DESC
LIMIT @limit;
""";
        Add(command, "@category", category.ToString());
        Add(command, "@limit", NormalizeLimit(limit));
        return await ReadMemoryItemsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryItem>> GetBySubjectAsync(string subject, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, category, title, subject, content, tags_json, confidence, source_message_id, source_path, created_utc, updated_utc
FROM memory_items
WHERE subject = @subject COLLATE NOCASE
ORDER BY updated_utc DESC
LIMIT @limit;
""";
        Add(command, "@subject", subject);
        Add(command, "@limit", NormalizeLimit(limit));
        return await ReadMemoryItemsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryItem>> GetLowConfidenceAsync(double maxConfidence, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, category, title, subject, content, tags_json, confidence, source_message_id, source_path, created_utc, updated_utc
FROM memory_items
WHERE confidence < @max_confidence
ORDER BY confidence ASC, updated_utc DESC
LIMIT @limit;
""";
        Add(command, "@max_confidence", maxConfidence);
        Add(command, "@limit", NormalizeLimit(limit));
        return await ReadMemoryItemsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryItem>> GetStaleAsync(DateTimeOffset olderThanUtc, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, category, title, subject, content, tags_json, confidence, source_message_id, source_path, created_utc, updated_utc
FROM memory_items
WHERE updated_utc < @older_than
ORDER BY updated_utc ASC
LIMIT @limit;
""";
        Add(command, "@older_than", FormatUtc(olderThanUtc));
        Add(command, "@limit", NormalizeLimit(limit));
        return await ReadMemoryItemsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        try
        {
            await DeleteFtsRowAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM memory_items WHERE id = @id;";
            Add(command, "@id", id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<Reminder> AddAsync(ReminderCreateRequest request, CancellationToken cancellationToken = default)
    {
        var reminder = new Reminder(
            Guid.NewGuid(),
            request.Title.Trim(),
            string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            request.DueAtUtc.ToUniversalTime(),
            request.RepeatKind,
            ReminderStatus.Pending,
            DateTimeOffset.UtcNow,
            null,
            null);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO reminders (id, title, notes, due_utc, repeat_kind, status, created_utc, last_sent_utc, completed_utc)
VALUES (@id, @title, @notes, @due_utc, @repeat_kind, @status, @created_utc, @last_sent_utc, @completed_utc);
""";
        AddReminderParameters(command, reminder);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return reminder;
    }

    public async Task<IReadOnlyList<Reminder>> GetPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, title, notes, due_utc, repeat_kind, status, created_utc, last_sent_utc, completed_utc
FROM reminders
WHERE status = @status
ORDER BY due_utc ASC
LIMIT @limit;
""";
        Add(command, "@status", ReminderStatus.Pending.ToString());
        Add(command, "@limit", NormalizeLimit(limit));
        return await ReadRemindersAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, title, notes, due_utc, repeat_kind, status, created_utc, last_sent_utc, completed_utc
FROM reminders
WHERE status = @status AND due_utc <= @now_utc
ORDER BY due_utc ASC
LIMIT @limit;
""";
        Add(command, "@status", ReminderStatus.Pending.ToString());
        Add(command, "@now_utc", FormatUtc(nowUtc));
        Add(command, "@limit", NormalizeLimit(limit));
        return await ReadRemindersAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSentAsync(Guid id, DateTimeOffset sentAtUtc, DateTimeOffset? nextDueAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (nextDueAtUtc is null)
        {
            command.CommandText = """
UPDATE reminders
SET status = @status, last_sent_utc = @last_sent_utc
WHERE id = @id;
""";
            Add(command, "@status", ReminderStatus.Sent.ToString());
        }
        else
        {
            command.CommandText = """
UPDATE reminders
SET status = @status, due_utc = @due_utc, last_sent_utc = @last_sent_utc
WHERE id = @id;
""";
            Add(command, "@status", ReminderStatus.Pending.ToString());
            Add(command, "@due_utc", FormatUtc(nextDueAtUtc.Value));
        }

        Add(command, "@last_sent_utc", FormatUtc(sentAtUtc));
        Add(command, "@id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(Guid id, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default)
    {
        await UpdateReminderStatusAsync(id, ReminderStatus.Completed, completedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await UpdateReminderStatusAsync(id, ReminderStatus.Cancelled, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryRecordRunAsync(string kind, string periodKey, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT OR IGNORE INTO proactive_runs (kind, period_key, created_utc)
VALUES (@kind, @period_key, @created_utc);
""";
        Add(command, "@kind", kind);
        Add(command, "@period_key", periodKey);
        Add(command, "@created_utc", FormatUtc(createdAtUtc));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<PendingAutomation> SavePendingAsync(AutomationRequest request, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var pending = new PendingAutomation(Guid.NewGuid(), request.Name.Trim(), request.Arguments, expiresAtUtc.ToUniversalTime());
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO pending_automations (id, name, arguments_json, expires_utc, created_utc)
VALUES (@id, @name, @arguments_json, @expires_utc, @created_utc);
""";
        Add(command, "@id", pending.Id.ToString());
        Add(command, "@name", pending.Name);
        Add(command, "@arguments_json", JsonSerializer.Serialize(pending.Arguments, JsonOptions));
        Add(command, "@expires_utc", FormatUtc(pending.ExpiresAtUtc));
        Add(command, "@created_utc", FormatUtc(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return pending;
    }

    public async Task<PendingAutomation?> TakePendingAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        try
        {
            PendingAutomation? pending;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
SELECT id, name, arguments_json, expires_utc
FROM pending_automations
WHERE id = @id AND expires_utc >= @now_utc;
""";
                Add(select, "@id", id.ToString());
                Add(select, "@now_utc", FormatUtc(nowUtc));
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                pending = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPendingAutomation(reader) : null;
            }

            if (pending is not null)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM pending_automations WHERE id = @id;";
                Add(delete, "@id", id.ToString());
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
            return pending;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task RecordAutomationRunAsync(
        string name,
        IReadOnlyDictionary<string, string> arguments,
        AutomationRunStatus status,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset? finishedAtUtc,
        int? exitCode,
        string output,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO automation_runs (id, name, arguments_json, status, requested_utc, finished_utc, exit_code, output, error)
VALUES (@id, @name, @arguments_json, @status, @requested_utc, @finished_utc, @exit_code, @output, @error);
""";
        Add(command, "@id", Guid.NewGuid().ToString());
        Add(command, "@name", name);
        Add(command, "@arguments_json", JsonSerializer.Serialize(arguments, JsonOptions));
        Add(command, "@status", status.ToString());
        Add(command, "@requested_utc", FormatUtc(requestedAtUtc));
        Add(command, "@finished_utc", finishedAtUtc is null ? DBNull.Value : FormatUtc(finishedAtUtc.Value));
        Add(command, "@exit_code", exitCode is null ? DBNull.Value : exitCode.Value);
        Add(command, "@output", Truncate(output, 4000));
        Add(command, "@error", Truncate(error, 4000));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MemoryItem>> GetRecentByCategoriesAsync(
        IReadOnlyList<MemoryCategory> categories,
        int limit,
        DateTimeOffset? sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var categoryFilter = AddCategoryFilter(command, categories);
        var sinceFilter = sinceUtc is null ? string.Empty : " AND updated_utc >= @since_utc";
        command.CommandText = $$"""
SELECT id, category, title, subject, content, tags_json, confidence, source_message_id, source_path, created_utc, updated_utc
FROM memory_items
WHERE 1 = 1{{categoryFilter}}{{sinceFilter}}
ORDER BY updated_utc DESC
LIMIT @limit;
""";
        if (sinceUtc is not null)
        {
            Add(command, "@since_utc", FormatUtc(sinceUtc.Value));
        }

        Add(command, "@limit", NormalizeLimit(limit));
        return await ReadMemoryItemsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateReminderStatusAsync(Guid id, ReminderStatus status, DateTimeOffset? completedAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE reminders
SET status = @status, completed_utc = @completed_utc
WHERE id = @id;
""";
        Add(command, "@status", status.ToString());
        Add(command, "@completed_utc", completedAtUtc is null ? DBNull.Value : FormatUtc(completedAtUtc.Value));
        Add(command, "@id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateRawCaptureSourcePathAsync(SqliteConnection connection, Guid id, string sourcePath, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE raw_captures SET source_path = @source_path WHERE id = @id;";
        Add(command, "@source_path", sourcePath);
        Add(command, "@id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteFtsRowAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM memory_item_fts WHERE memory_item_id = @id;";
        Add(command, "@id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertFtsRowAsync(SqliteConnection connection, SqliteTransaction transaction, MemoryItem item, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO memory_item_fts (memory_item_id, title, subject, content, tags)
VALUES (@memory_item_id, @title, @subject, @content, @tags);
""";
        Add(command, "@memory_item_id", item.Id.ToString());
        Add(command, "@title", item.Title);
        Add(command, "@subject", item.Subject ?? string.Empty);
        Add(command, "@content", item.Content);
        Add(command, "@tags", string.Join(' ', item.Tags));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<IReadOnlyList<MemoryItem>> ReadMemoryItemsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var items = new List<MemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadMemoryItem(reader));
        }

        return items;
    }

    private static MemoryItem ReadMemoryItem(SqliteDataReader reader)
    {
        return new MemoryItem(
            Guid.Parse(reader.GetString(0)),
            Enum.Parse<MemoryCategory>(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(4),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            JsonSerializer.Deserialize<string[]>(reader.GetString(5), JsonOptions) ?? [],
            reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            ParseUtc(reader.GetString(9)),
            ParseUtc(reader.GetString(10)));
    }

    private static async Task<IReadOnlyList<Reminder>> ReadRemindersAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var reminders = new List<Reminder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reminders.Add(new Reminder(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseUtc(reader.GetString(3)),
                Enum.Parse<ReminderRepeatKind>(reader.GetString(4)),
                Enum.Parse<ReminderStatus>(reader.GetString(5)),
                ParseUtc(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8))));
        }

        return reminders;
    }

    private static PendingAutomation ReadPendingAutomation(SqliteDataReader reader)
    {
        return new PendingAutomation(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2), JsonOptions) ?? new Dictionary<string, string>(),
            ParseUtc(reader.GetString(3)));
    }

    private static void AddMemoryParameters(SqliteCommand command, MemoryItem item)
    {
        Add(command, "@id", item.Id.ToString());
        Add(command, "@category", item.Category.ToString());
        Add(command, "@title", item.Title);
        Add(command, "@subject", item.Subject is null ? DBNull.Value : item.Subject);
        Add(command, "@content", item.Content);
        Add(command, "@tags_json", JsonSerializer.Serialize(item.Tags, JsonOptions));
        Add(command, "@confidence", item.Confidence);
        Add(command, "@source_message_id", item.SourceMessageId is null ? DBNull.Value : item.SourceMessageId.Value);
        Add(command, "@source_path", item.SourcePath is null ? DBNull.Value : item.SourcePath);
        Add(command, "@created_utc", FormatUtc(item.CreatedAt));
        Add(command, "@updated_utc", FormatUtc(item.UpdatedAt));
    }

    private static void AddReminderParameters(SqliteCommand command, Reminder reminder)
    {
        Add(command, "@id", reminder.Id.ToString());
        Add(command, "@title", reminder.Title);
        Add(command, "@notes", reminder.Notes is null ? DBNull.Value : reminder.Notes);
        Add(command, "@due_utc", FormatUtc(reminder.DueAtUtc));
        Add(command, "@repeat_kind", reminder.RepeatKind.ToString());
        Add(command, "@status", reminder.Status.ToString());
        Add(command, "@created_utc", FormatUtc(reminder.CreatedAtUtc));
        Add(command, "@last_sent_utc", reminder.LastSentAtUtc is null ? DBNull.Value : FormatUtc(reminder.LastSentAtUtc.Value));
        Add(command, "@completed_utc", reminder.CompletedAtUtc is null ? DBNull.Value : FormatUtc(reminder.CompletedAtUtc.Value));
    }

    private static string AddCategoryFilter(SqliteCommand command, IReadOnlyList<MemoryCategory> categories, string? tableAlias = null)
    {
        if (categories.Count == 0)
        {
            return string.Empty;
        }

        var parameterNames = new List<string>(categories.Count);
        for (var index = 0; index < categories.Count; index++)
        {
            var parameterName = $"@category_{index}";
            parameterNames.Add(parameterName);
            Add(command, parameterName, categories[index].ToString());
        }

        var column = string.IsNullOrWhiteSpace(tableAlias) ? "category" : tableAlias + ".category";
        return $" AND {column} IN ({string.Join(", ", parameterNames)})";
    }

    private static List<string> SanitizeTokens(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            FlushToken(builder, tokens);
        }

        FlushToken(builder, tokens);
        return tokens.Distinct(StringComparer.Ordinal).Take(16).ToList();
    }

    private static void FlushToken(StringBuilder builder, List<string> tokens)
    {
        if (builder.Length >= 2)
        {
            tokens.Add(builder.ToString());
        }

        builder.Clear();
    }

    private static IReadOnlyList<string> CleanTags(IReadOnlyList<string> tags)
    {
        return tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    private static string BuildRawCapturePath(Guid id, DateTimeOffset createdAtUtc)
    {
        var utc = createdAtUtc.ToUniversalTime();
        var id8 = id.ToString("N", CultureInfo.InvariantCulture)[..8];
        return FormattableString.Invariant($"0-raw/{utc:yyyy}/{utc:MM}/{utc:yyyyMMdd-HHmmss}-{id8}.md");
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    private static int NormalizeLimit(int limit) => Math.Clamp(limit, 1, 500);

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static void Add(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
    }
}

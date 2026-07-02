namespace Mira.Infrastructure.Storage;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mira.Infrastructure.Configuration;

public sealed class SqliteSchemaInitializer(IOptions<StorageSettings> options)
{
    private readonly StorageSettings _settings = options.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StoragePathResolver.EnsureStorageDirectories(_settings);

        await using var connection = new SqliteConnection(StoragePathResolver.BuildConnectionString(_settings));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS conversation_messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    chat_id INTEGER NOT NULL,
    telegram_message_id INTEGER NOT NULL,
    direction TEXT NOT NULL,
    content TEXT NOT NULL,
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS raw_captures (
    id TEXT PRIMARY KEY,
    source_path TEXT NOT NULL UNIQUE,
    content TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_items (
    id TEXT PRIMARY KEY,
    category TEXT NOT NULL,
    title TEXT NOT NULL,
    subject TEXT NULL,
    content TEXT NOT NULL,
    tags_json TEXT NOT NULL,
    confidence REAL NOT NULL,
    source_message_id INTEGER NULL,
    source_path TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE VIRTUAL TABLE IF NOT EXISTS memory_item_fts
USING fts5(memory_item_id UNINDEXED, title, subject, content, tags);

CREATE TABLE IF NOT EXISTS reminders (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    notes TEXT NULL,
    due_utc TEXT NOT NULL,
    repeat_kind TEXT NOT NULL,
    status TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    last_sent_utc TEXT NULL,
    completed_utc TEXT NULL
);

CREATE TABLE IF NOT EXISTS proactive_runs (
    kind TEXT NOT NULL,
    period_key TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    PRIMARY KEY (kind, period_key)
);

CREATE TABLE IF NOT EXISTS pending_automations (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    arguments_json TEXT NOT NULL,
    expires_utc TEXT NOT NULL,
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS automation_runs (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    arguments_json TEXT NOT NULL,
    status TEXT NOT NULL,
    requested_utc TEXT NOT NULL,
    finished_utc TEXT NULL,
    exit_code INTEGER NULL,
    output TEXT NOT NULL,
    error TEXT NOT NULL
);
""";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

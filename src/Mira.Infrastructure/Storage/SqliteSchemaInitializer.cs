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

CREATE INDEX IF NOT EXISTS idx_conversation_messages_chat_created
ON conversation_messages (chat_id, created_utc DESC, id DESC);

CREATE TABLE IF NOT EXISTS raw_captures (
    id TEXT PRIMARY KEY,
    source_path TEXT NOT NULL UNIQUE,
    content TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS source_captures (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    title TEXT NOT NULL,
    content_text TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    external_id TEXT NULL,
    file_path TEXT NULL,
    metadata_json TEXT NOT NULL,
    status TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    processed_utc TEXT NULL
);
CREATE INDEX IF NOT EXISTS idx_source_captures_created ON source_captures(created_utc DESC);
CREATE INDEX IF NOT EXISTS idx_source_captures_status_created ON source_captures(status, created_utc DESC);
CREATE INDEX IF NOT EXISTS idx_source_captures_hash ON source_captures(content_hash);
CREATE TABLE IF NOT EXISTS memory_source_links (
    memory_id TEXT NOT NULL,
    source_capture_id TEXT NOT NULL,
    relationship TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    PRIMARY KEY (memory_id, source_capture_id, relationship),
    FOREIGN KEY (memory_id) REFERENCES memory_items(id) ON DELETE CASCADE,
    FOREIGN KEY (source_capture_id) REFERENCES source_captures(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_memory_source_links_source ON memory_source_links(source_capture_id);

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

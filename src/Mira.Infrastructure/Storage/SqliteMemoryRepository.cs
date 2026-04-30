using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mira.Core.Entities;
using Mira.Core.Enums;
using Mira.Core.Interfaces;
using Mira.Infrastructure.Configuration;

namespace Mira.Infrastructure.Storage;

internal sealed class SqliteMemoryRepository(IOptions<StorageSettings> options) : IMemoryRepository
{
    private readonly string _connectionString = options.Value.ConnectionString;

    public async Task SaveMemoryEntryAsync(MemoryEntry entry, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_entries (content, category, tags, created_at)
            VALUES ($content, $category, $tags, $createdAt)
            """;
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$category", (int)entry.Category);
        command.Parameters.AddWithValue("$tags", entry.Tags);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchMemoryAsync(string query, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, content, category, tags, created_at
            FROM memory_entries
            WHERE content LIKE $query
            ORDER BY created_at DESC
            LIMIT 20
            """;
        command.Parameters.AddWithValue("$query", $"%{query}%");

        return await ReadMemoryEntriesAsync(command, ct);
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetRecentMemoryAsync(int count, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, content, category, tags, created_at
            FROM memory_entries
            ORDER BY created_at DESC
            LIMIT $count
            """;
        command.Parameters.AddWithValue("$count", count);

        return await ReadMemoryEntriesAsync(command, ct);
    }

    private static async Task<IReadOnlyList<MemoryEntry>> ReadMemoryEntriesAsync(
        SqliteCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var entries = new List<MemoryEntry>();

        while (await reader.ReadAsync(ct))
        {
            entries.Add(new MemoryEntry
            {
                Id = reader.GetInt32(0),
                Content = reader.GetString(1),
                Category = (MemoryCategory)reader.GetInt32(2),
                Tags = reader.GetString(3),
                CreatedAt = DateTime.Parse(reader.GetString(4))
            });
        }

        return entries;
    }
}

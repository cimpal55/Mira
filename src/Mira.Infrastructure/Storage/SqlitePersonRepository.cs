using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mira.Core.Entities;
using Mira.Core.Interfaces;
using Mira.Infrastructure.Configuration;

namespace Mira.Infrastructure.Storage;

internal sealed class SqlitePersonRepository(IOptions<StorageSettings> options) : IPersonRepository
{
    private readonly string _connectionString = options.Value.ConnectionString;

    public async Task<Person?> FindPersonByNameAsync(string name, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, relationship_type, notes, updated_at
            FROM people
            WHERE name = $name COLLATE NOCASE
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new Person
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            RelationshipType = reader.GetString(2),
            Notes = reader.GetString(3),
            UpdatedAt = DateTime.Parse(reader.GetString(4))
        };
    }

    public async Task<IReadOnlyList<Person>> GetAllPeopleAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, relationship_type, notes, updated_at FROM people ORDER BY name";

        await using var reader = await command.ExecuteReaderAsync(ct);
        var people = new List<Person>();

        while (await reader.ReadAsync(ct))
        {
            people.Add(new Person
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                RelationshipType = reader.GetString(2),
                Notes = reader.GetString(3),
                UpdatedAt = DateTime.Parse(reader.GetString(4))
            });
        }

        return people;
    }

    public async Task SavePersonAsync(Person person, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();

        // Upsert: update if name exists, insert otherwise
        command.CommandText = """
            INSERT INTO people (name, relationship_type, notes, updated_at)
            VALUES ($name, $relationshipType, $notes, $updatedAt)
            ON CONFLICT(name) DO UPDATE SET
                relationship_type = $relationshipType,
                notes = $notes,
                updated_at = $updatedAt
            """;
        command.Parameters.AddWithValue("$name", person.Name);
        command.Parameters.AddWithValue("$relationshipType", person.RelationshipType);
        command.Parameters.AddWithValue("$notes", person.Notes);
        command.Parameters.AddWithValue("$updatedAt", person.UpdatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(ct);
    }
}

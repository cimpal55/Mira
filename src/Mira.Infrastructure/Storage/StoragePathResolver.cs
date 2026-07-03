namespace Mira.Infrastructure.Storage;

using Microsoft.Data.Sqlite;
using Mira.Infrastructure.Configuration;

internal static class StoragePathResolver
{
    public static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.GetFullPath(expanded.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string BuildConnectionString(StorageSettings settings)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ExpandPath(settings.DatabasePath)
        };
        return builder.ToString();
    }

    public static void EnsureStorageDirectories(StorageSettings settings)
    {
        var databasePath = ExpandPath(settings.DatabasePath);
        var databaseDirectory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        var root = ExpandPath(settings.KnowledgeRootPath);
        Directory.CreateDirectory(Path.Combine(root, "0-raw"));
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        Directory.CreateDirectory(Path.Combine(root, "1-desk"));
        Directory.CreateDirectory(Path.Combine(root, "2-atoms"));
        Directory.CreateDirectory(Path.Combine(root, "3-threads"));
        Directory.CreateDirectory(Path.Combine(root, "briefings"));
        Directory.CreateDirectory(Path.Combine(root, "_system"));
        Directory.CreateDirectory(Path.Combine(root, "_system", "skills"));
        EnsureSeedFile(
            Path.Combine(root, "_system", "profile.md"),
            """
            # Mira Profile

            Stable prompt memory goes here: identity, preferences, routines, tone, goals, and durable context Mira should always respect.
            """);
        EnsureSeedFile(
            Path.Combine(root, "_system", "house-rules.md"),
            """
            # Mira House Rules

            - Capture raw sources first; never overwrite original inputs.
            - Prefer local/private processing unless the user explicitly chooses a stronger route.
            - Keep medical information organized, but do not make medical decisions or change treatment.
            - Surface uncertainty, contradictions, stale facts, and missing information for human review.
            """);
    }

    private static void EnsureSeedFile(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        File.WriteAllText(path, content);
    }

    public static string CombineKnowledgePath(StorageSettings settings, string relativePath)
    {
        var root = ExpandPath(settings.KnowledgeRootPath);
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([root, .. segments]);
    }
}

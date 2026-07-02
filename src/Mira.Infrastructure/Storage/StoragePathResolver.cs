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
        Directory.CreateDirectory(Path.Combine(root, "2-atoms"));
        Directory.CreateDirectory(Path.Combine(root, "3-threads"));
        Directory.CreateDirectory(Path.Combine(root, "briefings"));
    }

    public static string CombineKnowledgePath(StorageSettings settings, string relativePath)
    {
        var root = ExpandPath(settings.KnowledgeRootPath);
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([root, .. segments]);
    }
}

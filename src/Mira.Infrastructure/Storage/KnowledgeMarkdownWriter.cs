namespace Mira.Infrastructure.Storage;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;

public sealed class KnowledgeMarkdownWriter(
    IOptions<StorageSettings> options,
    ILogger<KnowledgeMarkdownWriter> logger) : IKnowledgeArtifactWriter
{
    private readonly StorageSettings _settings = options.Value;

    public bool SourcePathExists(string sourcePath)
    {
        var path = StoragePathResolver.CombineKnowledgePath(_settings, sourcePath);
        return File.Exists(path);
    }

    public async Task<bool> TryWriteRawCaptureAsync(string sourcePath, string content, CancellationToken cancellationToken = default)
    {
        if (!_settings.EnableMarkdownMirror)
        {
            return true;
        }

        try
        {
            var path = StoragePathResolver.CombineKnowledgePath(_settings, sourcePath);
            if (File.Exists(path))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? StoragePathResolver.ExpandPath(_settings.KnowledgeRootPath));
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write raw capture Markdown mirror.");
            return false;
        }
    }

    public async Task WriteAtomAsync(MemoryItem item, CancellationToken cancellationToken = default)
    {
        if (!_settings.EnableMarkdownMirror)
        {
            return;
        }

        try
        {
            var path = BuildAtomPath(item);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? StoragePathResolver.ExpandPath(_settings.KnowledgeRootPath));
            await File.WriteAllTextAsync(path, BuildAtomMarkdown(item), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write memory atom Markdown mirror for {MemoryId}.", item.Id);
        }
    }

    public async Task WriteBriefingAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        await WriteArtifactAsync(Path.Combine("briefings", SafeFileName(fileName)), content, "briefing", cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteThreadAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        await WriteArtifactAsync(Path.Combine("3-threads", SafeFileName(fileName)), content, "thread", cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteMemoryDashboardAsync(IReadOnlyList<MemoryItem> items, CancellationToken cancellationToken = default)
    {
        if (!_settings.EnableMarkdownMirror)
        {
            return;
        }

        try
        {
            var path = StoragePathResolver.CombineKnowledgePath(_settings, Path.Combine("0-dashboard", "memory.md"));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? StoragePathResolver.ExpandPath(_settings.KnowledgeRootPath));
            await File.WriteAllTextAsync(path, BuildMemoryDashboardMarkdown(items), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write memory dashboard Markdown mirror.");
        }
    }


    private async Task WriteArtifactAsync(string relativePath, string content, string artifactKind, CancellationToken cancellationToken)
    {
        if (!_settings.EnableMarkdownMirror)
        {
            return;
        }

        try
        {
            var path = StoragePathResolver.CombineKnowledgePath(_settings, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? StoragePathResolver.ExpandPath(_settings.KnowledgeRootPath));
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write {ArtifactKind} Markdown mirror.", artifactKind);
        }
    }

    private string BuildAtomPath(MemoryItem item)
    {
        return StoragePathResolver.CombineKnowledgePath(_settings, BuildAtomRelativePath(item));
    }

    private static string BuildAtomRelativePath(MemoryItem item)
    {
        var date = item.CreatedAt.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var fileName = $"{date}-{Slugify(item.Title)}-{item.Id.ToString("N", CultureInfo.InvariantCulture)[..8]}.md";
        return Path.Combine("2-atoms", item.Category.ToString(), fileName);
    }

    private static string BuildAtomMarkdown(MemoryItem item)
    {
        var subject = item.Subject ?? string.Empty;
        var tags = string.Join(", ", item.Tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0));
        var sourcePath = item.SourcePath ?? string.Empty;
        return $$"""
---
id: {{item.Id}}
category: {{item.Category}}
subject: {{subject}}
tags: [{{tags}}]
confidence: {{item.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}}
source_path: {{sourcePath}}
created_utc: {{item.CreatedAt.ToUniversalTime():O}}
updated_utc: {{item.UpdatedAt.ToUniversalTime():O}}
---

# {{item.Title}}

{{item.Content}}
""";
    }

    private static string BuildMemoryDashboardMarkdown(IReadOnlyList<MemoryItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"generated_utc: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"item_count: {items.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("# Mira Memory Dashboard");
        builder.AppendLine();
        builder.AppendLine("Local Obsidian-friendly index of saved Mira memories. Full memory atoms live under `2-atoms/`.");
        builder.AppendLine();
        if (items.Count == 0)
        {
            builder.AppendLine("No saved memories yet.");
            return builder.ToString();
        }

        builder.AppendLine("## Categories");
        foreach (var group in items.GroupBy(item => item.Category).OrderBy(group => group.Key.ToString()))
        {
            builder.AppendLine($"- {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var group in items.GroupBy(item => item.Category).OrderBy(group => group.Key.ToString()))
        {
            builder.AppendLine();
            builder.AppendLine($"## {group.Key}");
            builder.AppendLine();
            builder.AppendLine("| Updated | Memory | Subject | Confidence | Content |");
            builder.AppendLine("|---|---|---|---:|---|");

            foreach (var item in group.OrderByDescending(item => item.UpdatedAt))
            {
                var relativePath = NormalizeMarkdownPath(Path.Combine("..", BuildAtomRelativePath(item)));
                builder.Append("| ")
                    .Append(item.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append(" | [")
                    .Append(EscapeMarkdownTable(item.Title))
                    .Append("](")
                    .Append(relativePath)
                    .Append(") | ")
                    .Append(EscapeMarkdownTable(item.Subject ?? string.Empty))
                    .Append(" | ")
                    .Append(item.Confidence.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append(" | ")
                    .Append(EscapeMarkdownTable(TruncateForDashboard(item.Content, 180)))
                    .AppendLine(" |");
            }
        }

        return builder.ToString();
    }

    private static string NormalizeMarkdownPath(string path) => path.Replace('\\', '/');

    private static string EscapeMarkdownTable(string value)
    {
        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Trim();
    }

    private static string TruncateForDashboard(string value, int maxLength)
    {
        var normalized = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        return builder.ToString();
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousDash = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousDash = false;
            }
            else if (!previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "memory" : slug[..Math.Min(slug.Length, 80)];
    }
}

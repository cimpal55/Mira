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
        var date = item.CreatedAt.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var fileName = $"{date}-{Slugify(item.Title)}-{item.Id.ToString("N", CultureInfo.InvariantCulture)[..8]}.md";
        return StoragePathResolver.CombineKnowledgePath(_settings, Path.Combine("2-atoms", item.Category.ToString(), fileName));
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

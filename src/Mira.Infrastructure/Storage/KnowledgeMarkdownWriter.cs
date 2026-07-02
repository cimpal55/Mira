namespace Mira.Infrastructure.Storage;

using System.Globalization;
using System.Net;
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
            var dashboardDirectory = StoragePathResolver.CombineKnowledgePath(_settings, "0-dashboard");
            Directory.CreateDirectory(dashboardDirectory);
            await File.WriteAllTextAsync(Path.Combine(dashboardDirectory, "memory.md"), BuildMemoryDashboardMarkdown(items), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(dashboardDirectory, "index.html"), BuildMemoryDashboardHtml(items), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
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

    private static string BuildMemoryDashboardHtml(IReadOnlyList<MemoryItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\">");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("  <title>Mira Memory Dashboard</title>");
        builder.AppendLine("  <style>");
        builder.AppendLine("    :root { color-scheme: light dark; font-family: system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif; }");
        builder.AppendLine("    body { margin: 0; padding: 2rem; background: Canvas; color: CanvasText; }");
        builder.AppendLine("    main { max-width: 72rem; margin: 0 auto; }");
        builder.AppendLine("    header, section { margin-bottom: 2rem; }");
        builder.AppendLine("    .summary { color: color-mix(in srgb, CanvasText 72%, transparent); }");
        builder.AppendLine("    .search { width: 100%; box-sizing: border-box; padding: 0.85rem 1rem; border: 1px solid color-mix(in srgb, CanvasText 25%, transparent); border-radius: 0.75rem; font: inherit; }");
        builder.AppendLine("    .counts { display: flex; flex-wrap: wrap; gap: 0.75rem; padding: 0; list-style: none; }");
        builder.AppendLine("    .counts a { display: inline-block; padding: 0.55rem 0.75rem; border-radius: 999px; background: color-mix(in srgb, CanvasText 9%, transparent); color: inherit; text-decoration: none; }");
        builder.AppendLine("    .category { margin-top: 2rem; }");
        builder.AppendLine("    .memory-card { border: 1px solid color-mix(in srgb, CanvasText 16%, transparent); border-radius: 1rem; padding: 1rem; margin: 1rem 0; background: color-mix(in srgb, CanvasText 4%, transparent); }");
        builder.AppendLine("    .memory-card h3 { margin: 0 0 0.5rem; }");
        builder.AppendLine("    .meta { display: flex; flex-wrap: wrap; gap: 0.5rem 1rem; color: color-mix(in srgb, CanvasText 65%, transparent); font-size: 0.92rem; }");
        builder.AppendLine("    .content { white-space: pre-wrap; }");
        builder.AppendLine("    .empty { padding: 1rem; border-radius: 0.75rem; background: color-mix(in srgb, CanvasText 8%, transparent); }");
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main>");
        builder.AppendLine("  <header>");
        builder.AppendLine("    <h1>Mira Memory Dashboard</h1>");
        builder.Append("    <p class=\"summary\">Generated ")
            .Append(Html(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)))
            .Append(" with ")
            .Append(Html(items.Count.ToString(CultureInfo.InvariantCulture)))
            .AppendLine(" saved memories. Full memory atoms live under <code>2-atoms/</code>.</p>");
        builder.AppendLine("    <label for=\"memory-search\">Search memories</label>");
        builder.AppendLine("    <input id=\"memory-search\" class=\"search\" type=\"search\" placeholder=\"Filter by title, subject, category, tags, or content\" autocomplete=\"off\">");
        builder.AppendLine("  </header>");

        if (items.Count == 0)
        {
            builder.AppendLine("  <p class=\"empty\">No saved memories yet.</p>");
            builder.AppendLine("</main>");
            builder.AppendLine("</body>");
            builder.AppendLine("</html>");
            return builder.ToString();
        }

        var groups = items.GroupBy(item => item.Category).OrderBy(group => group.Key.ToString()).ToArray();
        builder.AppendLine("  <section aria-labelledby=\"category-counts\">");
        builder.AppendLine("    <h2 id=\"category-counts\">Category counts</h2>");
        builder.AppendLine("    <ul class=\"counts\">");
        foreach (var group in groups)
        {
            var category = group.Key.ToString();
            builder.Append("      <li><a href=\"#")
                .Append(HtmlAttribute(CategoryAnchor(category)))
                .Append("\">")
                .Append(Html(category))
                .Append(": ")
                .Append(Html(group.Count().ToString(CultureInfo.InvariantCulture)))
                .AppendLine("</a></li>");
        }

        builder.AppendLine("    </ul>");
        builder.AppendLine("  </section>");
        builder.AppendLine("  <section aria-labelledby=\"memory-list\">");
        builder.AppendLine("    <h2 id=\"memory-list\">Memories</h2>");
        foreach (var group in groups)
        {
            var category = group.Key.ToString();
            builder.Append("    <section class=\"category\" id=\"")
                .Append(HtmlAttribute(CategoryAnchor(category)))
                .AppendLine("\">");
            builder.Append("      <h2>")
                .Append(Html(category))
                .AppendLine("</h2>");

            foreach (var item in group.OrderByDescending(item => item.UpdatedAt))
            {
                var relativePath = NormalizeMarkdownPath(Path.Combine("..", BuildAtomRelativePath(item)));
                var subject = string.IsNullOrWhiteSpace(item.Subject) ? "No subject" : item.Subject.Trim();
                var tagText = string.Join(", ", item.Tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0));
                var tags = string.IsNullOrWhiteSpace(tagText) ? "No tags" : tagText;
                builder.AppendLine("      <article class=\"memory-card\">");
                builder.Append("        <h3><a href=\"")
                    .Append(HtmlAttribute(relativePath))
                    .Append("\">")
                    .Append(Html(item.Title))
                    .AppendLine("</a></h3>");
                builder.Append("        <div class=\"meta\"><span>")
                    .Append(Html(group.Key.ToString()))
                    .Append("</span><span>Updated ")
                    .Append(Html(item.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                    .Append("</span><span>Confidence ")
                    .Append(Html(item.Confidence.ToString("0.00", CultureInfo.InvariantCulture)))
                    .Append("</span><span>")
                    .Append(Html(subject))
                    .Append("</span><span>")
                    .Append(Html(tags))
                    .AppendLine("</span></div>");
                builder.Append("        <p class=\"content\">")
                    .Append(Html(TruncateForDashboard(item.Content, 360)))
                    .AppendLine("</p>");
                builder.AppendLine("      </article>");
            }

            builder.AppendLine("    </section>");
        }

        builder.AppendLine("  </section>");
        builder.AppendLine("</main>");
        builder.AppendLine("""
<script>
const input = document.getElementById('memory-search');
const cards = Array.from(document.querySelectorAll('.memory-card'));
input.addEventListener('input', () => {
  const query = input.value.trim().toLocaleLowerCase();
  for (const card of cards) {
    card.hidden = query.length > 0 && !card.textContent.toLocaleLowerCase().includes(query);
  }
});
</script>
""");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static string CategoryAnchor(string value) => "category-" + Slugify(value);

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string HtmlAttribute(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

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

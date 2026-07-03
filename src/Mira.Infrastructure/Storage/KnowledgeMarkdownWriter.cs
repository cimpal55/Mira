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
    private const int DashboardRefreshSeconds = 5;
    private const int DashboardRecentMemoryCount = 25;
    private const int DashboardPreviewCharacters = 180;

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
            var markdownPath = StoragePathResolver.BuildDashboardMarkdownPath(_settings);
            var dashboardDirectory = Path.GetDirectoryName(markdownPath) ?? StoragePathResolver.ExpandPath(_settings.KnowledgeRootPath);
            Directory.CreateDirectory(dashboardDirectory);
            await File.WriteAllTextAsync(markdownPath, BuildMemoryDashboardMarkdown(items), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            var htmlPath = StoragePathResolver.BuildDashboardHtmlPath(_settings);
            await File.WriteAllTextAsync(htmlPath, BuildMemoryDashboardHtml(items), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
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
        var generatedUtc = DateTimeOffset.UtcNow;
        var orderedItems = items.OrderByDescending(item => item.UpdatedAt).ToList();
        var categoryGroups = orderedItems
            .GroupBy(item => item.Category)
            .OrderBy(group => group.Key.ToString())
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"generated_utc: {generatedUtc:O}");
        builder.AppendLine($"item_count: {items.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"auto_refresh_html: {StoragePathResolver.DashboardHtmlRelativePath.Replace('\\', '/')}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("# Mira Memory Dashboard");
        builder.AppendLine();
        builder.AppendLine("Local Obsidian-friendly index of saved Mira memories. Full memory atoms live under `../2-atoms/`.");
        builder.AppendLine();
        builder.AppendLine($"> Regenerated immediately after Mira saves or deletes memory. Open [memory.html](memory.html) for a {DashboardRefreshSeconds.ToString(CultureInfo.InvariantCulture)}-second auto-refresh browser view.");
        builder.AppendLine();
        builder.AppendLine("## At a glance");
        builder.AppendLine();
        builder.AppendLine($"- Total memories in this dashboard: {items.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Categories represented: {categoryGroups.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Last dashboard refresh: {FormatDashboardTimestamp(generatedUtc)}");
        builder.AppendLine($"- Most recent memory update: {(orderedItems.Count == 0 ? "none" : FormatDashboardTimestamp(orderedItems[0].UpdatedAt))}");
        builder.AppendLine();
        builder.AppendLine("## Source inbox");
        builder.AppendLine();
        builder.AppendLine("Raw captures are stored under `0-raw/sources` before Mira derives memories, tasks, reminders, or decisions.");
        builder.AppendLine();

        if (items.Count == 0)
        {
            builder.AppendLine("No saved memories yet.");
            return builder.ToString();
        }

        builder.AppendLine("## Category totals");
        foreach (var group in categoryGroups)
        {
            builder.AppendLine($"- {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine();
        builder.AppendLine("## Recent memories");
        builder.AppendLine();
        builder.AppendLine("| Updated | Category | Memory | Subject | Confidence | Tags | Snapshot |");
        builder.AppendLine("|---|---|---|---|---:|---|---|");
        foreach (var item in orderedItems.Take(DashboardRecentMemoryCount))
        {
            AppendMarkdownMemoryRow(builder, item);
        }

        foreach (var group in categoryGroups)
        {
            builder.AppendLine();
            builder.AppendLine($"## {group.Key}");
            builder.AppendLine();
            builder.AppendLine("| Updated | Memory | Subject | Confidence | Tags | Snapshot |");
            builder.AppendLine("|---|---|---|---:|---|---|");

            foreach (var item in group)
            {
                AppendMarkdownMemoryRow(builder, item, includeCategory: false);
            }
        }

        return builder.ToString();
    }

    private static void AppendMarkdownMemoryRow(StringBuilder builder, MemoryItem item, bool includeCategory = true)
    {
        var relativePath = DashboardAtomRelativePath(item);
        builder.Append("| ")
            .Append(FormatDashboardDate(item.UpdatedAt))
            .Append(" | ");

        if (includeCategory)
        {
            builder.Append(EscapeMarkdownTable(item.Category.ToString())).Append(" | ");
        }

        builder.Append("[")
            .Append(EscapeMarkdownTable(item.Title))
            .Append("](")
            .Append(relativePath)
            .Append(") | ")
            .Append(EscapeMarkdownTable(item.Subject ?? string.Empty))
            .Append(" | ")
            .Append(item.Confidence.ToString("0.00", CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(EscapeMarkdownTable(FormatTags(item.Tags)))
            .Append(" | ")
            .Append(EscapeMarkdownTable(TruncateForDashboard(item.Content, DashboardPreviewCharacters)))
            .AppendLine(" |");
    }

    private static string BuildMemoryDashboardHtml(IReadOnlyList<MemoryItem> items)
    {
        var generatedUtc = DateTimeOffset.UtcNow;
        var orderedItems = items.OrderByDescending(item => item.UpdatedAt).ToList();
        var categoryGroups = orderedItems
            .GroupBy(item => item.Category)
            .OrderBy(group => group.Key.ToString())
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\">");
        builder.AppendLine($"  <meta http-equiv=\"refresh\" content=\"{DashboardRefreshSeconds.ToString(CultureInfo.InvariantCulture)}\">");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("  <title>Mira Memory Dashboard</title>");
        builder.AppendLine("""
  <style>
    :root { color-scheme: light dark; font-family: Inter, Segoe UI, system-ui, sans-serif; }
    body { margin: 0; padding: 2rem; background: Canvas; color: CanvasText; }
    main { max-width: 1180px; margin: 0 auto; }
    header { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 1rem; align-items: baseline; margin-bottom: 1.5rem; }
    h1, h2 { margin: 0 0 0.75rem; }
    .muted { color: color-mix(in srgb, CanvasText 68%, transparent); }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 1rem; margin: 1rem 0 2rem; }
    .card, .panel { border: 1px solid color-mix(in srgb, CanvasText 18%, transparent); border-radius: 0.85rem; padding: 1rem; background: color-mix(in srgb, Canvas 92%, CanvasText 8%); }
    .card strong { display: block; font-size: 1.75rem; line-height: 1; margin-top: 0.35rem; }
    table { width: 100%; border-collapse: collapse; font-size: 0.95rem; }
    th, td { border-bottom: 1px solid color-mix(in srgb, CanvasText 14%, transparent); padding: 0.55rem 0.45rem; text-align: left; vertical-align: top; }
    th { font-size: 0.78rem; letter-spacing: 0.04em; text-transform: uppercase; }
    a { color: LinkText; }
    .confidence { text-align: right; font-variant-numeric: tabular-nums; }
    .snapshot { max-width: 34rem; }
  </style>
""");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main>");
        builder.AppendLine("<header>");
        builder.AppendLine("  <div>");
        builder.AppendLine("    <h1>Mira Memory Dashboard</h1>");
        builder.AppendLine($"    <p class=\"muted\">Static local view. Auto-refreshes every {DashboardRefreshSeconds.ToString(CultureInfo.InvariantCulture)} seconds after Mira rewrites the file.</p>");
        builder.AppendLine("  </div>");
        builder.AppendLine($"  <p class=\"muted\">Generated {Html(FormatDashboardTimestamp(generatedUtc))}</p>");
        builder.AppendLine("</header>");
        builder.AppendLine("<section class=\"grid\" aria-label=\"Dashboard summary\">");
        builder.AppendLine($"  <div class=\"card\">Indexed memories<strong>{items.Count.ToString(CultureInfo.InvariantCulture)}</strong></div>");
        builder.AppendLine($"  <div class=\"card\">Categories<strong>{categoryGroups.Count.ToString(CultureInfo.InvariantCulture)}</strong></div>");
        builder.AppendLine($"  <div class=\"card\">Latest update<strong>{Html(orderedItems.Count == 0 ? "none" : FormatDashboardDate(orderedItems[0].UpdatedAt))}</strong></div>");
        builder.AppendLine("</section>");

        if (items.Count == 0)
        {
            builder.AppendLine("<section class=\"panel\"><h2>No saved memories yet</h2><p>Mira will populate this dashboard after the first saved memory.</p></section>");
            builder.AppendLine("</main>");
            builder.AppendLine("</body>");
            builder.AppendLine("</html>");
            return builder.ToString();
        }

        builder.AppendLine("<section class=\"grid\" aria-label=\"Category totals\">");
        foreach (var group in categoryGroups)
        {
            builder.AppendLine($"  <div class=\"card\">{Html(group.Key.ToString())}<strong>{group.Count().ToString(CultureInfo.InvariantCulture)}</strong></div>");
        }

        builder.AppendLine("</section>");
        builder.AppendLine("<section class=\"panel\">");
        builder.AppendLine("<h2>Recent memories</h2>");
        builder.AppendLine("<table><thead><tr><th>Updated</th><th>Category</th><th>Memory</th><th>Subject</th><th>Confidence</th><th>Tags</th><th>Snapshot</th></tr></thead><tbody>");
        foreach (var item in orderedItems.Take(DashboardRecentMemoryCount))
        {
            AppendHtmlMemoryRow(builder, item, includeCategory: true);
        }

        builder.AppendLine("</tbody></table>");
        builder.AppendLine("</section>");

        foreach (var group in categoryGroups)
        {
            builder.AppendLine("<section class=\"panel\">");
            builder.AppendLine($"<h2>{Html(group.Key.ToString())}</h2>");
            builder.AppendLine("<table><thead><tr><th>Updated</th><th>Memory</th><th>Subject</th><th>Confidence</th><th>Tags</th><th>Snapshot</th></tr></thead><tbody>");
            foreach (var item in group)
            {
                AppendHtmlMemoryRow(builder, item, includeCategory: false);
            }

            builder.AppendLine("</tbody></table>");
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static void AppendHtmlMemoryRow(StringBuilder builder, MemoryItem item, bool includeCategory)
    {
        builder.Append("<tr><td>")
            .Append(Html(FormatDashboardDate(item.UpdatedAt)))
            .Append("</td>");

        if (includeCategory)
        {
            builder.Append("<td>").Append(Html(item.Category.ToString())).Append("</td>");
        }

        builder.Append("<td><a href=\"")
            .Append(Html(DashboardAtomRelativePath(item)))
            .Append("\">")
            .Append(Html(item.Title))
            .Append("</a></td><td>")
            .Append(Html(item.Subject ?? string.Empty))
            .Append("</td><td class=\"confidence\">")
            .Append(Html(item.Confidence.ToString("0.00", CultureInfo.InvariantCulture)))
            .Append("</td><td>")
            .Append(Html(FormatTags(item.Tags)))
            .Append("</td><td class=\"snapshot\">")
            .Append(Html(TruncateForDashboard(item.Content, DashboardPreviewCharacters)))
            .AppendLine("</td></tr>");
    }

    private static string DashboardAtomRelativePath(MemoryItem item) =>
        NormalizeMarkdownPath(Path.Combine("..", BuildAtomRelativePath(item)));

    private static string FormatDashboardDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatDashboardTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatTags(IReadOnlyList<string> tags) =>
        string.Join(", ", tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0));

    private static string Html(string value) => WebUtility.HtmlEncode(value) ?? string.Empty;

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

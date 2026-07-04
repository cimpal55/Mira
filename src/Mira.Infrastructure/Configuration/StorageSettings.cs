namespace Mira.Infrastructure.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class StorageSettings
{
    public const string SectionName = "Storage";

    [Required]
    public string DatabasePath { get; set; } = "%LOCALAPPDATA%/Mira/mira.db";

    [Required]
    public string KnowledgeRootPath { get; set; } = "%LOCALAPPDATA%/Mira/knowledge";

    public bool EnableMarkdownMirror { get; set; } = true;

    [Range(1, 5000)]
    public int DashboardMemoryLimit { get; set; } = 1000;
}

using System.ComponentModel.DataAnnotations;

namespace Mira.Infrastructure.Configuration;

public sealed class StorageSettings
{
    public const string SectionName = "Storage";

    [Required]
    public string ConnectionString { get; init; } = string.Empty;
}

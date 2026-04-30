using System.ComponentModel.DataAnnotations;

namespace Mira.Infrastructure.Configuration;

public sealed class LocalLlmSettings
{
    public const string SectionName = "LocalLlm";

    [Required]
    public string BaseUrl { get; init; } = string.Empty;

    [Required]
    public string Model { get; init; } = string.Empty;
}

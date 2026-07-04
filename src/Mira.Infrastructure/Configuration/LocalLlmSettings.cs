namespace Mira.Infrastructure.Configuration;

using System.ComponentModel.DataAnnotations;
public sealed class LocalLlmSettings
{
    public const string SectionName = "LocalLlm";

    [Required]
    public string BaseUrl { get; set; } = "http://localhost:1234";

    [Required]
    public string Model { get; set; } = "hermes-3-llama-3.1-8b";

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 120;

    [Range(0.0, 2.0)]
    public double DefaultTemperature { get; set; } = 0.2;
}

namespace Mira.Infrastructure.Configuration;

public sealed class LocalLlmSettings
{
    public const string SectionName = "LocalLlm";

    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
}

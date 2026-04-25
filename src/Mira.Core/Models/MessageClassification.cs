using Mira.Core.Enums;

namespace Mira.Core.Models;

public sealed class MessageClassification
{
    public string Type { get; set; } = string.Empty;
    public PersonExtraction? Person { get; set; }
    public string? FactContent { get; set; }
    public string? SearchQuery { get; set; }
}

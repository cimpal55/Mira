namespace Mira.Core.Models;

public sealed class PersonExtraction
{
    public required string Name { get; set; }
    public string? RelationshipType { get; set; }
    public List<string>? Interests { get; set; }
    public List<string>? ImportantDates { get; set; }
    public string? Notes { get; set; }
}

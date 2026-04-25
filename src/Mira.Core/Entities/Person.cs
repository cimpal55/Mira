namespace Mira.Core.Entities;

public sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Interests { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

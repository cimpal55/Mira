using Mira.Core.Enums;

public sealed class PersonFact
{
    public int Id { get; set; }
    public int PersonId { get; set; }
    public PersonFactType Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
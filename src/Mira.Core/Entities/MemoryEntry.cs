using Mira.Core.Enums;

namespace Mira.Core.Entities;

public sealed class MemoryEntry
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public MemoryCategory Category { get; set; }
    public string Tags { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}


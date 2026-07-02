namespace Mira.Core.Models;

public enum MemoryCategory
{
    Person,
    Health,
    Medical,
    Job,
    Hobby,
    Gear,
    Thought,
    Decision,
    DailyNote,
    General
}

public sealed record MemoryItem(
    Guid Id,
    MemoryCategory Category,
    string Title,
    string Content,
    string? Subject,
    IReadOnlyList<string> Tags,
    double Confidence,
    long? SourceMessageId,
    string? SourcePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MemoryUpsert(
    MemoryCategory Category,
    string Title,
    string Content,
    string? Subject,
    IReadOnlyList<string> Tags,
    double Confidence,
    long? SourceMessageId,
    string? SourcePath);

public sealed record MemorySearchQuery(string Text, IReadOnlyList<MemoryCategory> Categories, int Limit = 8);

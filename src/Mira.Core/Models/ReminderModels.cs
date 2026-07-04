namespace Mira.Core.Models;

public enum ReminderRepeatKind
{
    None,
    Daily,
    Weekly,
    Monthly
}

public enum ReminderStatus
{
    Pending,
    Sent,
    Completed,
    Cancelled
}

public sealed record Reminder(
    Guid Id,
    string Title,
    string? Notes,
    DateTimeOffset DueAtUtc,
    ReminderRepeatKind RepeatKind,
    ReminderStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSentAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ReminderCreateRequest(
    string Title,
    string? Notes,
    DateTimeOffset DueAtUtc,
    ReminderRepeatKind RepeatKind);

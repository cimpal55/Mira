namespace Mira.Core.Models;

public enum AutomationRunStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Rejected
}

public sealed record AutomationRequest(string Name, IReadOnlyDictionary<string, string> Arguments);

public sealed record AutomationResult(AutomationRunStatus Status, int? ExitCode, string Output, string Error);

public sealed record PendingAutomation(
    Guid Id,
    string Name,
    IReadOnlyDictionary<string, string> Arguments,
    DateTimeOffset ExpiresAtUtc);

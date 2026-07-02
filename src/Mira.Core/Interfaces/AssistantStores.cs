namespace Mira.Core.Interfaces;

using Mira.Core.Models;

public interface IMemoryStore
{
    Task SaveConversationMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetRecentConversationAsync(long chatId, int limit, DateTimeOffset beforeUtc, CancellationToken cancellationToken = default);

    Task<string> SaveRawCaptureAsync(string content, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);

    Task<MemoryItem> UpsertAsync(MemoryUpsert request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> SearchAsync(MemorySearchQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> GetRecentAsync(int limit, DateTimeOffset? sinceUtc = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> GetByCategoryAsync(MemoryCategory category, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> GetBySubjectAsync(string subject, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> GetLowConfidenceAsync(double maxConfidence, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> GetStaleAsync(DateTimeOffset olderThanUtc, int limit, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IReminderStore
{
    Task<Reminder> AddAsync(ReminderCreateRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reminder>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default);

    Task MarkSentAsync(Guid id, DateTimeOffset sentAtUtc, DateTimeOffset? nextDueAtUtc, CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid id, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IProactiveRunStore
{
    Task<bool> TryRecordRunAsync(string kind, string periodKey, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
}

public interface IKnowledgeArtifactWriter
{
    Task WriteBriefingAsync(string fileName, string content, CancellationToken cancellationToken = default);

    Task WriteThreadAsync(string fileName, string content, CancellationToken cancellationToken = default);
}

public interface IAutomationStore
{
    Task<PendingAutomation> SavePendingAsync(AutomationRequest request, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default);

    Task<PendingAutomation?> TakePendingAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public interface IAutomationRunner
{
    Task<AutomationResult> RunAsync(AutomationRequest request, CancellationToken cancellationToken = default);

    Task<string?> GetRejectionReasonAsync(AutomationRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }
}

public interface INotificationSink
{
    Task SendOwnerMessageAsync(string text, CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

namespace Mira.Core.Tests.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Proactive;
using Xunit;

public sealed class KnowledgeMaintenanceServiceTests
{
    [Fact]
    public async Task Weekly_audit_runs_once_per_period_and_sends_counts()
    {
        var now = new DateTimeOffset(2026, 7, 5, 22, 5, 0, TimeSpan.Zero);
        var memory = new FakeMemoryStore
        {
            LowConfidence = [Memory("Low confidence item", "needs confirmation", "Maxim", 0.4)],
            Stale = [Memory("Stale item", "old note", "Gear", 0.9)],
            Recent =
            [
                Memory("Subject one", "first version", "Plan", 0.8),
                Memory("Subject two", "second version", "Plan", 0.8)
            ]
        };
        var runs = new FakeProactiveRunStore();
        var sink = new FakeNotificationSink();
        var writer = new FakeArtifactWriter();
        var service = CreateService(now, memory, runs, writer, sink);

        await service.RunOnceAsync(TestContext.Current.CancellationToken);
        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(writer.Briefings);
        var message = Assert.Single(sink.Messages);
        Assert.Contains("Low confidence: 1", message, StringComparison.Ordinal);
        Assert.Contains("Stale: 1", message, StringComparison.Ordinal);
        Assert.Contains("Possible contradiction candidates: 2", message, StringComparison.Ordinal);
        Assert.Equal(2, runs.Attempts);
    }

    [Fact]
    public async Task Weekly_audit_does_not_modify_or_delete_memory_items()
    {
        var now = new DateTimeOffset(2026, 7, 5, 22, 5, 0, TimeSpan.Zero);
        var memory = new FakeMemoryStore
        {
            LowConfidence = [Memory("Low confidence item", "needs confirmation", "Maxim", 0.4)],
            Stale = [Memory("Stale item", "old note", "Gear", 0.9)],
            Recent =
            [
                Memory("Subject one", "first version", "Plan", 0.8),
                Memory("Subject two", "second version", "Plan", 0.8)
            ]
        };
        var service = CreateService(now, memory, new FakeProactiveRunStore(), new FakeArtifactWriter(), new FakeNotificationSink());

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(memory.Upserts);
        Assert.Empty(memory.DeletedIds);
        Assert.Empty(memory.RawCaptures);
    }

    private static KnowledgeMaintenanceService CreateService(
        DateTimeOffset now,
        FakeMemoryStore memory,
        FakeProactiveRunStore runs,
        FakeArtifactWriter writer,
        FakeNotificationSink sink)
    {
        var settings = new ProactiveSettings
        {
            KnowledgeAuditEnabled = true,
            PollIntervalSeconds = 60,
            TimeZoneId = "UTC",
            KnowledgeAuditDay = DayOfWeek.Sunday,
            KnowledgeAuditLocalTime = "22:00",
            StaleMemoryAfterDays = 90
        };

        return new KnowledgeMaintenanceService(
            Options.Create(settings),
            memory,
            runs,
            new FakeLlmProvider(),
            writer,
            sink,
            new FakeClock(now),
            NullLogger<KnowledgeMaintenanceService>.Instance);
    }

    private static MemoryItem Memory(string title, string content, string subject, double confidence) => new(
        Guid.NewGuid(),
        MemoryCategory.Thought,
        title,
        content,
        subject,
        [],
        confidence,
        123,
        "0-raw/test.md",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FakeMemoryStore : IMemoryStore
    {
        public IReadOnlyList<MemoryItem> LowConfidence { get; init; } = [];

        public IReadOnlyList<MemoryItem> Stale { get; init; } = [];

        public IReadOnlyList<MemoryItem> Recent { get; init; } = [];

        public List<MemoryUpsert> Upserts { get; } = [];

        public List<Guid> DeletedIds { get; } = [];

        public List<string> RawCaptures { get; } = [];

        public Task SaveConversationMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationMessage>> GetRecentConversationAsync(long chatId, int limit, DateTimeOffset beforeUtc, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConversationMessage>>([]);

        public Task<string> SaveRawCaptureAsync(string content, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
        {
            RawCaptures.Add(content);
            return Task.FromResult("0-raw/test.md");
        }

        public Task<MemoryItem> UpsertAsync(MemoryUpsert request, Guid? sourceCaptureId = null, CancellationToken cancellationToken = default)
        {
            Upserts.Add(request);
            return Task.FromResult(new MemoryItem(Guid.NewGuid(), request.Category, request.Title, request.Content, request.Subject, request.Tags, request.Confidence, request.SourceMessageId, request.SourcePath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<MemoryItem>> SearchAsync(MemorySearchQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoryItem>>([]);

        public Task<IReadOnlyList<MemoryItem>> GetRecentAsync(int limit, DateTimeOffset? sinceUtc = null, CancellationToken cancellationToken = default) => Task.FromResult(Recent);

        public Task<IReadOnlyList<MemoryItem>> GetByCategoryAsync(MemoryCategory category, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoryItem>>([]);

        public Task<IReadOnlyList<MemoryItem>> GetBySubjectAsync(string subject, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoryItem>>([]);

        public Task<IReadOnlyList<MemoryItem>> GetLowConfidenceAsync(double maxConfidence, int limit, CancellationToken cancellationToken = default) => Task.FromResult(LowConfidence);

        public Task<IReadOnlyList<MemoryItem>> GetStaleAsync(DateTimeOffset olderThanUtc, int limit, CancellationToken cancellationToken = default) => Task.FromResult(Stale);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DeletedIds.Add(id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProactiveRunStore : IProactiveRunStore
    {
        private readonly HashSet<(string Kind, string PeriodKey)> _recorded = [];

        public int Attempts { get; private set; }

        public Task<bool> TryRecordRunAsync(string kind, string periodKey, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.FromResult(_recorded.Add((kind, periodKey)));
        }
    }

    private sealed class FakeLlmProvider : ILlmProvider
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            const string response = """
                {"lowConfidence":[{"id":"memory-1","title":"Low confidence item"}],"stale":[{"id":"memory-2","title":"Stale item"}],"possibleContradictions":[],"suggestedQuestions":["Confirm stale items?"]}
                """;
            return Task.FromResult(new LlmResponse(response));
        }
    }

    private sealed class FakeArtifactWriter : IKnowledgeArtifactWriter
    {
        public List<(string FileName, string Content)> Briefings { get; } = [];

        public Task WriteBriefingAsync(string fileName, string content, CancellationToken cancellationToken = default)
        {
            Briefings.Add((fileName, content));
            return Task.CompletedTask;
        }

        public Task WriteThreadAsync(string fileName, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeNotificationSink : INotificationSink
    {
        public List<string> Messages { get; } = [];

        public Task SendOwnerMessageAsync(string text, CancellationToken cancellationToken = default)
        {
            Messages.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}

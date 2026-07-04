namespace Mira.Infrastructure.Proactive;

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;

public sealed class KnowledgeMaintenanceService(
    IOptions<ProactiveSettings> options,
    IMemoryStore memoryStore,
    IProactiveRunStore proactiveRunStore,
    ILlmProvider llmProvider,
    IKnowledgeArtifactWriter artifactWriter,
    INotificationSink notificationSink,
    IClock clock,
    ILogger<KnowledgeMaintenanceService> logger) : BackgroundService
{
    private readonly ProactiveSettings _settings = options.Value;

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.KnowledgeAuditEnabled)
        {
            return;
        }

        var timeZone = ProactiveSchedule.ResolveTimeZone(_settings);
        var nowUtc = clock.UtcNow;
        var localNow = ProactiveSchedule.ToLocal(nowUtc, timeZone);
        if (localNow.DayOfWeek != _settings.KnowledgeAuditDay
            || !ProactiveSchedule.IsAtOrAfter(localNow, _settings.KnowledgeAuditLocalTime))
        {
            return;
        }

        var dateKey = ProactiveSchedule.LocalDateKey(localNow);
        if (!await proactiveRunStore.TryRecordRunAsync("knowledge-audit", dateKey, nowUtc, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var lowConfidence = await memoryStore.GetLowConfidenceAsync(0.6, 20, cancellationToken).ConfigureAwait(false);
        var stale = await memoryStore.GetStaleAsync(nowUtc.AddDays(-_settings.StaleMemoryAfterDays), 20, cancellationToken).ConfigureAwait(false);
        var recent = await memoryStore.GetRecentAsync(100, nowUtc.AddDays(-30), cancellationToken).ConfigureAwait(false);
        var sameSubject = recent
            .Where(item => !string.IsNullOrWhiteSpace(item.Subject))
            .GroupBy(item => item.Subject!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Take(5))
            .Take(20)
            .ToArray();

        var audit = await GenerateAuditAsync(lowConfidence, stale, sameSubject, cancellationToken).ConfigureAwait(false);
        await artifactWriter.WriteBriefingAsync($"{dateKey}-knowledge-audit.md", audit, cancellationToken).ConfigureAwait(false);
        await notificationSink.SendOwnerMessageAsync(BuildSummary(lowConfidence, stale, sameSubject), cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Knowledge maintenance service failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<string> GenerateAuditAsync(
        IReadOnlyList<MemoryItem> lowConfidence,
        IReadOnlyList<MemoryItem> stale,
        IReadOnlyList<MemoryItem> sameSubject,
        CancellationToken cancellationToken)
    {
        var prompt = $$"""
Produce a knowledge audit as JSON only.
Return JSON object with arrays named lowConfidence, stale, possibleContradictions, and suggestedQuestions.
Cite memory IDs and titles only. Do not invent new facts. Do not edit or delete anything.

Low confidence memories:
{{FormatMemoryRefs(lowConfidence)}}

Stale memories:
{{FormatMemoryRefs(stale)}}

Recent memories sharing the same subject:
{{FormatMemoryRefs(sameSubject)}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, "Audit the local knowledge base.")], Temperature: 0.1, RequireJson: true),
            cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(response.Content), new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildSummary(IReadOnlyList<MemoryItem> lowConfidence, IReadOnlyList<MemoryItem> stale, IReadOnlyList<MemoryItem> sameSubject)
    {
        var top = lowConfidence.Concat(stale).Concat(sameSubject).DistinctBy(item => item.Id).Take(3).ToArray();
        var topText = top.Length == 0
            ? "No urgent human-judgment items surfaced."
            : string.Join("\n", top.Select(item => $"- {item.Id}: {item.Title}"));
        return $"Knowledge audit complete.\nLow confidence: {lowConfidence.Count}\nStale: {stale.Count}\nPossible contradiction candidates: {sameSubject.Count}\n\nTop items:\n{topText}";
    }

    private static string FormatMemoryRefs(IEnumerable<MemoryItem> memories)
    {
        var value = string.Join("\n", memories.Select(memory => $"- id={memory.Id}; title={memory.Title}; subject={memory.Subject ?? string.Empty}; category={memory.Category}"));
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}

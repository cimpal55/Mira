namespace Mira.Infrastructure.Proactive;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;

public sealed class ProactiveBriefService(
    IOptions<ProactiveSettings> options,
    IReminderStore reminderStore,
    IMemoryStore memoryStore,
    IProactiveRunStore proactiveRunStore,
    ILlmProvider llmProvider,
    IKnowledgeArtifactWriter artifactWriter,
    INotificationSink notificationSink,
    IClock clock,
    ILogger<ProactiveBriefService> logger) : BackgroundService
{
    private readonly ProactiveSettings _settings = options.Value;

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var timeZone = ProactiveSchedule.ResolveTimeZone(_settings);
        var nowUtc = clock.UtcNow;
        var localNow = ProactiveSchedule.ToLocal(nowUtc, timeZone);

        if (_settings.DailyBriefEnabled && ProactiveSchedule.IsAtOrAfter(localNow, _settings.DailyBriefLocalTime))
        {
            var key = ProactiveSchedule.LocalDateKey(localNow);
            if (await proactiveRunStore.TryRecordRunAsync("daily-brief", key, nowUtc, cancellationToken).ConfigureAwait(false))
            {
                var brief = await GenerateDailyBriefAsync(nowUtc, key, cancellationToken).ConfigureAwait(false);
                await notificationSink.SendOwnerMessageAsync(brief, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_settings.WeeklyReviewEnabled
            && localNow.DayOfWeek == _settings.WeeklyReviewDay
            && ProactiveSchedule.IsAtOrAfter(localNow, _settings.WeeklyReviewLocalTime))
        {
            var periodKey = ProactiveSchedule.WeeklyPeriodKey(localNow);
            if (await proactiveRunStore.TryRecordRunAsync("weekly-review", periodKey, nowUtc, cancellationToken).ConfigureAwait(false))
            {
                var review = await GenerateWeeklyReviewAsync(nowUtc, ProactiveSchedule.LocalDateKey(localNow), cancellationToken).ConfigureAwait(false);
                await notificationSink.SendOwnerMessageAsync(review, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_settings.WorkoutReminderEnabled)
        {
            await MaybeSendWorkoutReminderAsync(nowUtc, localNow, cancellationToken).ConfigureAwait(false);
        }
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
                logger.LogError(ex, "Proactive brief service failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<string> GenerateDailyBriefAsync(DateTimeOffset nowUtc, string dateKey, CancellationToken cancellationToken)
    {
        var pending = await reminderStore.GetPendingAsync(50, cancellationToken).ConfigureAwait(false);
        var dueSoon = pending.Where(reminder => reminder.DueAtUtc <= nowUtc.AddHours(48)).OrderBy(reminder => reminder.DueAtUtc).Take(20).ToArray();
        var recent = await memoryStore.GetRecentAsync(50, nowUtc.AddHours(-48), cancellationToken).ConfigureAwait(false);
        var decisions = await memoryStore.GetByCategoryAsync(MemoryCategory.Decision, 20, cancellationToken).ConfigureAwait(false);
        var prompt = $$"""
Create a concise daily brief using only this local personal context.
Include reminders due within 48 hours, recent memories, and open decisions. Do not invent facts.

Due reminders:
{{FormatReminderContext(dueSoon)}}

Recent memories:
{{FormatMemoryContext(recent)}}

Open decisions:
{{FormatMemoryContext(decisions)}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, "Create the daily brief.")], Temperature: 0.2),
            cancellationToken).ConfigureAwait(false);
        var brief = response.Content.Trim();
        await artifactWriter.WriteBriefingAsync($"{dateKey}-daily-brief.md", brief, cancellationToken).ConfigureAwait(false);
        return brief;
    }

    private async Task<string> GenerateWeeklyReviewAsync(DateTimeOffset nowUtc, string dateKey, CancellationToken cancellationToken)
    {
        var since = nowUtc.AddDays(-7);
        var memories = new List<MemoryItem>();
        foreach (var category in new[] { MemoryCategory.Health, MemoryCategory.Thought, MemoryCategory.Decision, MemoryCategory.DailyNote })
        {
            var categoryItems = await memoryStore.GetByCategoryAsync(category, 50, cancellationToken).ConfigureAwait(false);
            memories.AddRange(categoryItems.Where(item => item.CreatedAt >= since || item.UpdatedAt >= since));
        }

        var decisions = await memoryStore.GetByCategoryAsync(MemoryCategory.Decision, 20, cancellationToken).ConfigureAwait(false);
        var prompt = $$"""
Create a concise weekly review using only local personal context.
Sections: wins, patterns, next actions. Do not invent facts.

Last 7 days:
{{FormatMemoryContext(memories)}}

Pending decisions:
{{FormatMemoryContext(decisions)}}
""";
        var response = await llmProvider.CompleteAsync(
            new LlmRequest([new LlmMessage(LlmRole.System, prompt), new LlmMessage(LlmRole.User, "Create the weekly review.")], Temperature: 0.2),
            cancellationToken).ConfigureAwait(false);
        var review = response.Content.Trim();
        var fileName = $"{dateKey}-weekly-review.md";
        await artifactWriter.WriteBriefingAsync(fileName, review, cancellationToken).ConfigureAwait(false);
        await artifactWriter.WriteThreadAsync(fileName, review, cancellationToken).ConfigureAwait(false);
        return review;
    }

    private async Task MaybeSendWorkoutReminderAsync(DateTimeOffset nowUtc, DateTimeOffset localNow, CancellationToken cancellationToken)
    {
        var healthItems = await memoryStore.GetByCategoryAsync(MemoryCategory.Health, 200, cancellationToken).ConfigureAwait(false);
        var workoutItems = healthItems.Where(IsWorkout).ToArray();
        if (workoutItems.Length == 0)
        {
            return;
        }

        var cutoff = nowUtc.AddDays(-_settings.WorkoutReminderAfterDays);
        if (workoutItems.Any(item => item.CreatedAt >= cutoff || item.UpdatedAt >= cutoff))
        {
            return;
        }

        var key = ProactiveSchedule.LocalDateKey(localNow);
        if (await proactiveRunStore.TryRecordRunAsync("workout-reminder", key, nowUtc, cancellationToken).ConfigureAwait(false))
        {
            await notificationSink.SendOwnerMessageAsync(
                $"No workout has been logged in {_settings.WorkoutReminderAfterDays} days. Want to plan one today?",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsWorkout(MemoryItem item)
    {
        return item.Tags.Any(tag => tag.Equals("workout", StringComparison.OrdinalIgnoreCase))
            || item.Title.Contains("workout", StringComparison.OrdinalIgnoreCase)
            || item.Content.Contains("workout", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMemoryContext(IEnumerable<MemoryItem> memories)
    {
        var lines = memories.Select(memory => $"- id={memory.Id}; category={memory.Category}; title={memory.Title}; subject={memory.Subject ?? string.Empty}; tags={string.Join(',', memory.Tags)}; content={memory.Content}");
        var value = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FormatReminderContext(IEnumerable<Reminder> reminders)
    {
        var lines = reminders.Select(reminder => $"- id={reminder.Id}; title={reminder.Title}; due_utc={reminder.DueAtUtc:O}; repeat={reminder.RepeatKind}");
        var value = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}

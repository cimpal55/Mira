namespace Mira.Infrastructure.Reminders;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Proactive;

public sealed class ReminderDispatchService(
    IOptions<ProactiveSettings> options,
    IReminderStore reminderStore,
    INotificationSink notificationSink,
    IClock clock,
    ILogger<ReminderDispatchService> logger) : BackgroundService
{
    private readonly ProactiveSettings _settings = options.Value;

    public async Task DispatchDueAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.RemindersEnabled)
        {
            return;
        }

        var now = clock.UtcNow;
        var due = await reminderStore.GetDueAsync(now, 20, cancellationToken).ConfigureAwait(false);
        var timeZone = ProactiveSchedule.ResolveTimeZone(_settings);
        foreach (var reminder in due)
        {
            await notificationSink.SendOwnerMessageAsync(FormatReminder(reminder), cancellationToken).ConfigureAwait(false);
            var nextDue = reminder.RepeatKind is ReminderRepeatKind.None
                ? null
                : ComputeNextDueUtc(reminder, timeZone);
            await reminderStore.MarkSentAsync(reminder.Id, now, nextDue, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reminder dispatch failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private static DateTimeOffset? ComputeNextDueUtc(Reminder reminder, TimeZoneInfo timeZone)
    {
        var local = ProactiveSchedule.ToLocal(reminder.DueAtUtc, timeZone);
        var nextLocal = reminder.RepeatKind switch
        {
            ReminderRepeatKind.Daily => local.AddDays(1),
            ReminderRepeatKind.Weekly => local.AddDays(7),
            ReminderRepeatKind.Monthly => local.AddMonths(1),
            _ => local
        };
        return ProactiveSchedule.FromLocal(nextLocal, timeZone);
    }

    private static string FormatReminder(Reminder reminder)
    {
        return string.IsNullOrWhiteSpace(reminder.Notes)
            ? $"Reminder: {reminder.Title}"
            : $"Reminder: {reminder.Title}\n{reminder.Notes}";
    }
}

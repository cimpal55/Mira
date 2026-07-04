namespace Mira.Core.Tests.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mira.Core.Interfaces;
using Mira.Core.Models;
using Mira.Infrastructure.Configuration;
using Mira.Infrastructure.Reminders;
using Xunit;

public sealed class ReminderDispatchServiceTests
{
    [Fact]
    public async Task Due_one_time_reminder_is_sent_once_and_marked_sent()
    {
        var now = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        var reminder = new Reminder(Guid.NewGuid(), "stretch", null, now.AddMinutes(-1), ReminderRepeatKind.None, ReminderStatus.Pending, now.AddDays(-1), null, null);
        var store = new FakeReminderStore([reminder]);
        var sink = new FakeNotificationSink();
        var service = CreateService(store, sink, now);

        await service.DispatchDueAsync(TestContext.Current.CancellationToken);
        await service.DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Single(sink.Messages);
        Assert.Equal(reminder.Id, store.Marked.Single().Id);
        Assert.Null(store.Marked.Single().NextDueAtUtc);
    }

    [Fact]
    public async Task Due_daily_reminder_is_rescheduled_in_local_time()
    {
        var now = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        var reminder = new Reminder(Guid.NewGuid(), "stand up", null, now.AddMinutes(-1), ReminderRepeatKind.Daily, ReminderStatus.Pending, now.AddDays(-1), null, null);
        var store = new FakeReminderStore([reminder]);
        var sink = new FakeNotificationSink();
        var service = CreateService(store, sink, now);

        await service.DispatchDueAsync(TestContext.Current.CancellationToken);

        var marked = Assert.Single(store.Marked);
        var local = TimeZoneInfo.ConvertTime(reminder.DueAtUtc, TimeZoneInfo.Local).AddDays(1);
        var expectedUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.DateTime, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        Assert.Equal(new DateTimeOffset(expectedUtc, TimeSpan.Zero), marked.NextDueAtUtc);
    }

    private static ReminderDispatchService CreateService(FakeReminderStore store, FakeNotificationSink sink, DateTimeOffset now)
    {
        return new ReminderDispatchService(
            Options.Create(new ProactiveSettings { RemindersEnabled = true, PollIntervalSeconds = 60, TimeZoneId = string.Empty }),
            store,
            sink,
            new FakeClock(now),
            NullLogger<ReminderDispatchService>.Instance);
    }

    private sealed class FakeReminderStore(List<Reminder> due) : IReminderStore
    {
        public List<(Guid Id, DateTimeOffset SentAtUtc, DateTimeOffset? NextDueAtUtc)> Marked { get; } = [];

        public Task<Reminder> AddAsync(ReminderCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Reminder>> GetPendingAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Reminder>>([]);

        public Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Reminder>>(due.Where(reminder => reminder.Status == ReminderStatus.Pending && reminder.DueAtUtc <= nowUtc).Take(limit).ToArray());
        }

        public Task MarkSentAsync(Guid id, DateTimeOffset sentAtUtc, DateTimeOffset? nextDueAtUtc, CancellationToken cancellationToken = default)
        {
            Marked.Add((id, sentAtUtc, nextDueAtUtc));
            var index = due.FindIndex(reminder => reminder.Id == id);
            if (index >= 0)
            {
                var reminder = due[index];
                due[index] = nextDueAtUtc is null
                    ? reminder with { Status = ReminderStatus.Sent, LastSentAtUtc = sentAtUtc }
                    : reminder with { DueAtUtc = nextDueAtUtc.Value, LastSentAtUtc = sentAtUtc };
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync(Guid id, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CancelAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

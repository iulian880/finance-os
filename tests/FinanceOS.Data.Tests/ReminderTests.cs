using Dapper;
using FinanceOS.Core.Events;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.Data.Reminders;
using FinanceOS.Data.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FinanceOS.Data.Tests;

/// <summary>Toasts recorded instead of shown.</summary>
internal sealed class FakeSink : INotificationSink
{
    public List<ReminderAlert> Shown { get; } = [];

    public List<string> Withdrawn { get; } = [];

    public void Show(ReminderAlert alert) => Shown.Add(alert);

    public void Withdraw(string occurrenceId) => Withdrawn.Add(occurrenceId);
}

/// <summary>Windows' scheduled toasts, in memory: what was scheduled, what Windows "showed".</summary>
internal sealed class FakeScheduledToasts : IScheduledToasts
{
    public List<(ReminderAlert Alert, DateTimeOffset At)> Scheduled { get; } = [];

    public HashSet<string> Delivered { get; } = [];

    public int Cancelled { get; private set; }

    public void Schedule(ReminderAlert alert, DateTimeOffset atUtc) => Scheduled.Add((alert, atUtc));

    public IReadOnlySet<string> DeliveredTags() => Delivered;

    public void CancelAll()
    {
        Scheduled.Clear();
        Cancelled++;
    }
}

/// <summary>
/// M9: reminders while the app is open, on real SQLite and a <see cref="FakeTimeProvider"/> whose
/// timers fire when time is advanced. Local time zone: fixed UTC+2 (no DST).
/// </summary>
public sealed class ReminderTests : IDisposable
{
    // Monday 9 November 2026, 08:00 local (06:00 UTC).
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private readonly TestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 6, 0, 0, TimeSpan.Zero));
    private readonly FakeSink _sink = new();
    private readonly FakeScheduledToasts _os = new();
    private TaskService _tasks = null!;
    private ReminderService _reminders = null!;
    private ReminderScheduler _scheduler = null!;

    public ReminderTests()
    {
        _time.SetLocalTimeZone(Zone);
        _db.StartReady();
        Boot();
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Relative_reminder_fires_exactly_at_its_moment_once()
    {
        var task = _tasks.Create(new TaskDraft("VAT return") { DueDate = Today, DueTime = new TimeOnly(10, 0) });
        _reminders.AddRelative(task.Id, 30);
        _scheduler.Start();

        AdvanceTo(9, 29, 59);
        Assert.Empty(_sink.Shown);

        AdvanceTo(9, 30);
        var alert = Assert.Single(_sink.Shown);
        Assert.Equal(("VAT return", task.Id), (alert.TaskTitle, alert.TaskId));

        var notification = Assert.Single(_scheduler.Notifications());
        Assert.Equal((NotificationType.Reminder, "VAT return", task.Id), (notification.Type, notification.Title, notification.TaskId));
        Assert.Equal(1, _scheduler.UnreadCount());

        AdvanceTo(12, 0);
        Assert.Single(_sink.Shown);
    }

    [Fact]
    public void A_due_date_without_a_time_counts_as_nine_in_the_morning()
    {
        var task = _tasks.Create(new TaskDraft("Payroll") { DueDate = Today });
        var reminder = _reminders.AddRelative(task.Id, 0);

        Assert.Equal(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero), _reminders.PlannedUtc(reminder)); // 09:00 local
    }

    [Fact]
    public void Several_reminders_on_one_task_fire_separately_and_duplicates_are_not_added()
    {
        var task = _tasks.Create(new TaskDraft("Close") { DueDate = Today, DueTime = new TimeOnly(12, 0) });
        var hourBefore = _reminders.AddRelative(task.Id, 60);
        _reminders.AddRelative(task.Id, 0);
        Assert.Equal(hourBefore.Id, _reminders.AddRelative(task.Id, 60).Id);
        _scheduler.Start();

        AdvanceTo(11, 0);
        Assert.Single(_sink.Shown);
        AdvanceTo(12, 0);
        Assert.Equal(2, _sink.Shown.Count);
        Assert.Equal(2, _reminders.For(task.Id).Count);
    }

    [Fact]
    public void Moving_the_due_date_reschedules_the_reminder()
    {
        var task = _tasks.Create(new TaskDraft("Report") { DueDate = Today, DueTime = new TimeOnly(10, 0) });
        _reminders.AddRelative(task.Id, 0);
        _scheduler.Start();

        _tasks.Edit(task.Id, t => t with { DueTime = new TimeOnly(15, 0) });

        AdvanceTo(10, 30);
        Assert.Empty(_sink.Shown);
        AdvanceTo(15, 0);
        Assert.Single(_sink.Shown);

        // Moving it away and back to a moment that already fired does not fire again.
        _tasks.Edit(task.Id, t => t with { DueTime = new TimeOnly(10, 0) });
        _tasks.Edit(task.Id, t => t with { DueTime = new TimeOnly(15, 0) });
        AdvanceTo(16, 0);
        Assert.Single(_sink.Shown);
    }

    [Fact]
    public void Completing_trashing_or_archiving_the_task_cancels_and_undoing_brings_it_back()
    {
        var task = _tasks.Create(new TaskDraft("Audit") { DueDate = Today, DueTime = new TimeOnly(11, 0) });
        _reminders.AddRelative(task.Id, 0);
        _scheduler.Start();

        _tasks.Complete(task.Id);
        Assert.Empty(OpenOccurrences());
        _tasks.Reopen(task.Id);
        Assert.Single(OpenOccurrences());

        _tasks.Delete(task.Id);
        Assert.Empty(OpenOccurrences());
        _tasks.Undo();
        Assert.Single(OpenOccurrences());

        _tasks.Archive(task.Id);
        AdvanceTo(11, 30);
        Assert.Empty(_sink.Shown);
        _tasks.Unarchive(task.Id);
        AdvanceTo(11, 31);
        Assert.Empty(_sink.Shown); // its moment passed while archived: not fired late
    }

    [Fact]
    public void Snooze_fires_again_later_and_survives_a_due_date_change()
    {
        var task = _tasks.Create(new TaskDraft("Call bank") { DueDate = Today, DueTime = new TimeOnly(9, 0) });
        _reminders.AddRelative(task.Id, 0);
        _scheduler.Start();
        AdvanceTo(9, 0);
        var first = _sink.Shown.Single().OccurrenceId;

        Assert.True(_scheduler.Snooze(first, TimeSpan.FromMinutes(10)));
        Assert.False(_scheduler.Snooze(first, TimeSpan.FromMinutes(10))); // already handled
        Assert.Contains(first, _sink.Withdrawn);

        _tasks.Edit(task.Id, t => t with { DueDate = Today.AddDays(3) });

        AdvanceTo(9, 9);
        Assert.Single(_sink.Shown);
        AdvanceTo(9, 10);
        Assert.Equal(2, _sink.Shown.Count);

        Assert.True(_scheduler.SnoozeUntilTomorrow(_sink.Shown[^1].OccurrenceId));
        AdvanceTo(23, 0);
        Assert.Equal(2, _sink.Shown.Count);
        _time.Advance(TimeSpan.FromHours(10)); // tomorrow 09:00 local
        Assert.Equal(3, _sink.Shown.Count);
    }

    [Fact]
    public void Done_completes_the_task_once_and_dismiss_only_closes_the_reminder()
    {
        var task = _tasks.Create(new TaskDraft("Pay supplier") { DueDate = Today, DueTime = new TimeOnly(9, 0) });
        var other = _tasks.Create(new TaskDraft("Read memo") { DueDate = Today, DueTime = new TimeOnly(9, 0) });
        _reminders.AddRelative(task.Id, 0);
        _reminders.AddRelative(other.Id, 0);
        _scheduler.Start();
        AdvanceTo(9, 0);
        var done = _sink.Shown.Single(a => a.TaskId == task.Id).OccurrenceId;
        var dismissed = _sink.Shown.Single(a => a.TaskId == other.Id).OccurrenceId;

        Assert.True(_scheduler.Complete(done));
        Assert.False(_scheduler.Complete(done));
        Assert.Equal(SystemStatuses.Done, _tasks.Get(task.Id)!.StatusId);

        Assert.True(_scheduler.Dismiss(dismissed));
        Assert.False(_scheduler.CanAct(dismissed));
        Assert.Equal(SystemStatuses.Todo, _tasks.Get(other.Id)!.StatusId);
    }

    [Fact]
    public void Absolute_reminder_fires_at_its_local_time_and_one_in_the_past_is_not_planned()
    {
        var task = _tasks.Create(new TaskDraft("No due date"));
        var past = _reminders.AddAbsolute(task.Id, new DateTime(2026, 11, 9, 7, 0, 0));
        _reminders.AddAbsolute(task.Id, new DateTime(2026, 11, 9, 13, 45, 30));
        _scheduler.Start();

        using (var connection = _db.Open())
        {
            Assert.Empty(connection.Query<string>("SELECT id FROM reminder_occurrences WHERE reminder_id = @Id;", new { past.Id }));
        }

        AdvanceTo(13, 44);
        Assert.Empty(_sink.Shown);
        AdvanceTo(13, 45);
        Assert.Single(_sink.Shown);
    }

    [Fact]
    public void At_start_old_pending_reminders_become_one_missed_notification_and_recent_ones_fire()
    {
        var late = _tasks.Create(new TaskDraft("Late one") { DueDate = Today, DueTime = new TimeOnly(8, 30) });
        var recent = _tasks.Create(new TaskDraft("Recent one") { DueDate = Today, DueTime = new TimeOnly(9, 58) });
        _reminders.AddRelative(late.Id, 0);
        _reminders.AddRelative(recent.Id, 0);

        // The app was not running between 08:00 and 10:00.
        AdvanceTo(10, 0);
        _scheduler.Start();

        Assert.Equal(["Recent one"], _sink.Shown.Select(a => a.TaskTitle));
        var missed = _scheduler.Notifications().Single(n => n.Type == NotificationType.Missed);
        Assert.Equal(1, missed.Count);
    }

    [Fact]
    public void Removing_a_reminder_cancels_it()
    {
        var task = _tasks.Create(new TaskDraft("x") { DueDate = Today, DueTime = new TimeOnly(10, 0) });
        var reminder = _reminders.AddRelative(task.Id, 0);
        _scheduler.Start();

        _reminders.Remove(reminder.Id);

        Assert.Empty(_reminders.For(task.Id));
        AdvanceTo(11, 0);
        Assert.Empty(_sink.Shown);
    }

    [Fact]
    public void Notifications_can_be_read_one_by_one_or_all_at_once()
    {
        foreach (var hour in new[] { 9, 10, 11 })
        {
            var task = _tasks.Create(new TaskDraft($"T{hour}") { DueDate = Today, DueTime = new TimeOnly(hour, 0) });
            _reminders.AddRelative(task.Id, 0);
        }

        _scheduler.Start();
        AdvanceTo(11, 0);
        var notifications = _scheduler.Notifications();
        Assert.Equal(["T11", "T10", "T9"], notifications.Select(n => n.Title)); // newest first
        Assert.Equal(3, _scheduler.UnreadCount());

        _scheduler.MarkRead(notifications[0].Id);
        Assert.Equal(2, _scheduler.UnreadCount());
        _scheduler.MarkRead();
        Assert.Equal(0, _scheduler.UnreadCount());
    }

    [Fact]
    public void Planned_reminders_survive_a_restart()
    {
        var task = _tasks.Create(new TaskDraft("Persisted") { DueDate = Today, DueTime = new TimeOnly(9, 0) });
        _reminders.AddRelative(task.Id, 0);
        _scheduler.Start();
        _scheduler.Dispose();

        Boot();
        _scheduler.Start();
        AdvanceTo(9, 0);

        Assert.Equal(["Persisted"], _sink.Shown.Select(a => a.TaskTitle));
    }

    [Fact]
    public void Planner_handles_the_dst_gap_and_closed_tasks()
    {
        var bucharest = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "GTB Standard Time" : "Europe/Bucharest");
        var task = new TaskItem { Id = "t", Title = "t", DueDate = new DateOnly(2027, 3, 28), DueTime = new TimeOnly(3, 30) };
        var reminder = new Reminder { Id = "r", TaskId = "t", Kind = ReminderKind.RelativeToDue, TimeZoneId = bucharest.Id };

        // 03:30 does not exist on 28 March 2027 in Bucharest (03:00 → 04:00): it fires at 04:00 local = 01:00 UTC.
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), ReminderPlanner.DueUtc(reminder, task, false, bucharest));
        Assert.Null(ReminderPlanner.DueUtc(reminder, task, true, bucharest));
        Assert.Null(ReminderPlanner.DueUtc(reminder, task with { DueDate = null }, false, bucharest));
    }

    // M10 ------------------------------------------------------------------------------------------

    [Fact]
    public void On_exit_the_next_week_is_handed_to_windows_and_taken_back_at_start()
    {
        foreach (var days in new[] { 1, 3, 6, 9 })
        {
            var task = _tasks.Create(new TaskDraft($"In {days} days") { DueDate = Today.AddDays(days), DueTime = new TimeOnly(10, 0) });
            _reminders.AddRelative(task.Id, 0);
        }

        _scheduler.Start();
        Assert.Equal(3, _scheduler.ScheduleAhead()); // 9 days is beyond the 7-day window
        Assert.Equal(["In 1 days", "In 3 days", "In 6 days"], _os.Scheduled.Select(s => s.Alert.TaskTitle));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), _os.Scheduled[0].At);

        Boot();
        _scheduler.Start();
        Assert.Empty(_os.Scheduled); // the running app delivers itself: no duplicates from Windows
    }

    [Fact]
    public void After_a_closed_app_windows_shown_reminders_are_listed_and_the_rest_are_missed()
    {
        var shown = _tasks.Create(new TaskDraft("Shown by Windows") { DueDate = Today, DueTime = new TimeOnly(9, 0) });
        var off = _tasks.Create(new TaskDraft("PC was off") { DueDate = Today, DueTime = new TimeOnly(9, 30) });
        _reminders.AddRelative(shown.Id, 0);
        _reminders.AddRelative(off.Id, 0);
        _scheduler.Start();
        _scheduler.ScheduleAhead();
        _os.Delivered.Add(_os.Scheduled.Single(s => s.Alert.TaskId == shown.Id).Alert.OccurrenceId);
        _scheduler.Dispose();

        AdvanceTo(12, 0); // app closed from 08:00 to 12:00
        Boot();
        _scheduler.Start();

        Assert.Empty(_sink.Shown); // nothing is shown again
        var notifications = _scheduler.Notifications();
        var listed = notifications.Single(n => n.Type == NotificationType.Reminder);
        Assert.Equal("Shown by Windows", listed.Title);
        Assert.True(_scheduler.CanAct(listed.OccurrenceId!)); // Done / Snooze still work from the center
        Assert.Equal(1, notifications.Single(n => n.Type == NotificationType.Missed).Count);
    }

    [Fact]
    public void Resume_after_sleep_sums_up_instead_of_firing_everything_at_once()
    {
        foreach (var hour in new[] { 9, 10, 11 })
        {
            var task = _tasks.Create(new TaskDraft($"T{hour}") { DueDate = Today, DueTime = new TimeOnly(hour, 0) });
            _reminders.AddRelative(task.Id, 0);
        }

        _scheduler.Start();
        _scheduler.Dispose(); // timers stopped: the PC sleeps
        AdvanceTo(11, 2);
        Boot();
        _scheduler.Start(); // resume reconciles like a start

        Assert.Equal(["T11"], _sink.Shown.Select(a => a.TaskTitle)); // 2 min ago: still shown
        Assert.Equal(2, _scheduler.Notifications().Single(n => n.Type == NotificationType.Missed).Count);
    }

    [Fact]
    public void Quiet_hours_defer_the_reminder_without_touching_the_due_date()
    {
        var task = _tasks.Create(new TaskDraft("Late call") { DueDate = Today, DueTime = new TimeOnly(21, 0) });
        _reminders.AddRelative(task.Id, 0);
        _scheduler.Quiet = new QuietHours(new TimeOnly(20, 0), new TimeOnly(8, 0));
        _scheduler.Start();

        AdvanceTo(21, 0);
        Assert.Empty(_sink.Shown);
        Assert.Equal(new TimeOnly(21, 0), _tasks.Get(task.Id)!.DueTime);

        _time.Advance(TimeSpan.FromHours(11)); // next day 08:00 local
        Assert.Single(_sink.Shown);
    }

    [Theory]
    [InlineData("2026-11-09T21:00", "2026-11-10T08:00")] // Monday evening -> Tuesday 08:00
    [InlineData("2026-11-10T07:59", "2026-11-10T08:00")]
    [InlineData("2026-11-10T08:00", "2026-11-10T08:00")]
    [InlineData("2026-11-13T21:00", "2026-11-16T08:00")] // Friday evening -> Monday (weekends quiet)
    [InlineData("2026-11-14T12:00", "2026-11-16T08:00")] // Saturday noon -> Monday
    public void Quiet_hours_next_allowed_moment(string local, string expected)
    {
        var quiet = new QuietHours(new TimeOnly(20, 0), new TimeOnly(8, 0), Weekends: true);
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            quiet.NextAllowed(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>(Re)creates the services over the same file, like an app start, with a fresh event bus.</summary>
    private void Boot()
    {
        var bus = new EventBus();
        var database = new SqliteDatabase(_db.Location.DatabasePath);
        _tasks = new TaskService(new SqliteTaskStore(database), bus, _time);
        var store = new SqliteReminderStore(database);
        _reminders = new ReminderService(store, _tasks, bus, _time);
        _scheduler = new ReminderScheduler(store, _reminders, _tasks, _sink, _os, _time);
    }

    private List<string> OpenOccurrences()
    {
        using var connection = _db.Open();
        return connection.Query<string>("SELECT id FROM reminder_occurrences WHERE state IN ('pending', 'deferred');").ToList();
    }

    /// <summary>Moves the clock forward to a local time today (UTC+2).</summary>
    private void AdvanceTo(int hour, int minute, int second = 0)
    {
        var target = new DateTimeOffset(2026, 11, 9, hour, minute, second, TimeSpan.FromHours(2));
        var delta = target - _time.GetUtcNow();
        if (delta > TimeSpan.Zero)
        {
            _time.Advance(delta);
        }
    }
}

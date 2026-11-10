using FinanceOS.Core.Common;
using FinanceOS.Core.Tasks;

namespace FinanceOS.Core.Reminders;

/// <summary>
/// Delivers reminders while the app runs (ARCHITECTURE §6.3, levels 1 and 2): one timer to the next
/// pending occurrence, re-armed on every change, plus a cheap safety check every minute. Delivering
/// = state Delivered + a line in the notification center + a toast through <see cref="INotificationSink"/>.
/// Also applies what the user does on a reminder (done, snooze, dismiss), idempotently: acting twice on
/// the same occurrence has no further effect.
/// </summary>
/// <remarks>
/// Timer callbacks run on the thread pool; when <see cref="Start"/> is called on a thread with a
/// synchronization context (the UI), the work is posted there, so events reach the UI on its thread.
/// Delivery while the app is closed and full reconciliation after sleep come in M10.
/// </remarks>
public sealed class ReminderScheduler : IDisposable
{
    public static readonly TimeSpan SafetyInterval = TimeSpan.FromMinutes(1);

    /// <summary>At start-up, a pending occurrence older than this was not delivered in time: it is missed, not shown late.</summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(5);

    /// <summary>Scheduled toasts handed to Windows at exit cover this far ahead, at most <see cref="ScheduleMax"/>.</summary>
    public static readonly TimeSpan ScheduleWindow = TimeSpan.FromDays(7);

    public const int ScheduleMax = 50;

    /// <summary>"Tomorrow" snoozes to this local time.</summary>
    public static readonly TimeOnly TomorrowAt = new(9, 0);

    private readonly IReminderStore _store;
    private readonly ReminderService _reminders;
    private readonly TaskService _tasks;
    private readonly INotificationSink _sink;
    private readonly IScheduledToasts _scheduled;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private SynchronizationContext? _context;
    private ITimer? _next;
    private ITimer? _safety;
    private bool _started;

    public ReminderScheduler(IReminderStore store, ReminderService reminders, TaskService tasks, INotificationSink sink, IScheduledToasts scheduled, TimeProvider time)
    {
        _store = store;
        _reminders = reminders;
        _tasks = tasks;
        _sink = sink;
        _scheduled = scheduled;
        _time = time;
        reminders.Changed += (_, _) =>
        {
            if (_started)
            {
                Arm();
            }
        };
    }

    /// <summary>The notification center changed (a reminder fired, something was read or handled).</summary>
    public event EventHandler? NotificationsChanged;

    /// <summary>Start-up: old pending occurrences become one "missed" notification, recent ones fire now; then the timers start.</summary>
    public void Start()
    {
        _context = SynchronizationContext.Current;
        _started = true;
        Reconcile();
        _safety = _time.CreateTimer(_ => OnTimer(), null, SafetyInterval, SafetyInterval);
    }

    /// <summary>Reminders do not interrupt during these hours (deferred instead). Null: never quiet.</summary>
    public QuietHours? Quiet { get; set; }

    /// <summary>
    /// ARCHITECTURE §6.4 (start-up, resume, clock change, unlock). Each passed pending moment: shown by Windows
    /// meanwhile → DeliveredByOs (listed, still actionable); older than <see cref="MissedAfter"/> → Missed, summed
    /// up in one notification; recent → delivered now. Then Windows' scheduled toasts are withdrawn (this process
    /// delivers from now on) and the timer is re-armed.
    /// </summary>
    public void Reconcile()
    {
        var changed = false;
        lock (_gate)
        {
            var now = Ids.Now(_time);
            var shownByWindows = _scheduled.DeliveredTags();
            var missed = 0;
            foreach (var occurrence in _store.GetOpenOccurrences().Where(o => o.DueUtc <= now))
            {
                if (shownByWindows.Contains(occurrence.Id))
                {
                    changed |= Deliver(occurrence, occurrence.DueUtc, byWindows: true);
                }
                else if (occurrence.DueUtc < now - MissedAfter)
                {
                    _store.SaveOccurrence(occurrence with { State = OccurrenceState.Missed });
                    missed++;
                }
            }

            if (missed > 0)
            {
                _store.AddNotification(new Notification { Id = Ids.New(_time), Type = NotificationType.Missed, Count = missed, CreatedAt = now });
                changed = true;
            }

            _scheduled.CancelAll();
        }

        if (changed)
        {
            NotificationsChanged?.Invoke(this, EventArgs.Empty);
        }

        DeliverDue();
        Arm();
    }

    /// <summary>Before exit or sleep: hands the next moments to Windows as scheduled toasts. Returns how many.</summary>
    public int ScheduleAhead()
    {
        lock (_gate)
        {
            _scheduled.CancelAll();
            var now = Ids.Now(_time);
            var count = 0;
            foreach (var occurrence in _store.GetOpenOccurrences().Where(o => o.DueUtc > now && o.DueUtc <= now + ScheduleWindow))
            {
                if (count == ScheduleMax)
                {
                    break;
                }

                if (AlertFor(occurrence) is { } alert)
                {
                    _scheduled.Schedule(alert, Allowed(occurrence.DueUtc));
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Delivers every pending occurrence whose moment has come. Returns how many fired.</summary>
    public int DeliverDue()
    {
        var delivered = 0;
        lock (_gate)
        {
            var now = Ids.Now(_time);
            foreach (var occurrence in _store.GetOpenOccurrences().Where(o => o.DueUtc <= now))
            {
                // Quiet hours: wait for the next allowed moment (Deferred); the due date never changes.
                var allowed = Allowed(now);
                if (allowed > now)
                {
                    _store.SaveOccurrence(occurrence with { State = OccurrenceState.Deferred, DueUtc = allowed });
                    continue;
                }

                if (Deliver(occurrence, now, byWindows: false))
                {
                    delivered++;
                }
            }
        }

        if (delivered > 0)
        {
            NotificationsChanged?.Invoke(this, EventArgs.Empty);
        }

        return delivered;
    }

    /// <summary>Fires again after <paramref name="delay"/>.</summary>
    public bool Snooze(string occurrenceId, TimeSpan delay) => SnoozeUntil(occurrenceId, Ids.Now(_time) + delay);

    /// <summary>Fires again tomorrow at 09:00 local time.</summary>
    public bool SnoozeUntilTomorrow(string occurrenceId)
    {
        var tomorrow = DateOnly.FromDateTime(_time.GetLocalNow().DateTime).AddDays(1);
        return SnoozeUntil(occurrenceId, ReminderPlanner.ToUtc(tomorrow.ToDateTime(TomorrowAt), _time.LocalTimeZone));
    }

    public bool SnoozeUntil(string occurrenceId, DateTimeOffset untilUtc)
    {
        lock (_gate)
        {
            if (Actionable(occurrenceId) is not { } occurrence)
            {
                return false;
            }

            var until = new DateTimeOffset(untilUtc.UtcDateTime.Ticks - (untilUtc.UtcDateTime.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
            var now = Ids.Now(_time);
            _store.SaveOccurrence(occurrence with { State = OccurrenceState.Snoozed, Action = OccurrenceAction.Snoozed, ActedAt = now, SnoozedUntil = until });
            var id = ReminderOccurrence.IdFor(occurrence.ReminderId, until);
            if (_store.GetOccurrence(id) is null)
            {
                _store.SaveOccurrence(new ReminderOccurrence { Id = id, ReminderId = occurrence.ReminderId, DueUtc = until, IsSnooze = true });
            }
        }

        _sink.Withdraw(occurrenceId);
        Arm();
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>"Done" on a reminder: completes the task (one undoable step in TaskService).</summary>
    public bool Complete(string occurrenceId)
    {
        string? taskId;
        lock (_gate)
        {
            if (Actionable(occurrenceId) is not { } occurrence)
            {
                return false;
            }

            _store.SaveOccurrence(occurrence with { State = OccurrenceState.Acted, Action = OccurrenceAction.Done, ActedAt = Ids.Now(_time) });
            taskId = _store.GetReminder(occurrence.ReminderId)?.TaskId;
        }

        if (taskId is not null && _tasks.Get(taskId) is { IsDeleted: false })
        {
            _tasks.Complete(taskId);
        }

        _sink.Withdraw(occurrenceId);
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Dismiss(string occurrenceId) => Act(occurrenceId, OccurrenceAction.Dismissed);

    /// <summary>The user opened the task from the reminder.</summary>
    public bool Opened(string occurrenceId) => Act(occurrenceId, OccurrenceAction.Opened);

    /// <summary>Task the occurrence reminds about (for "open" from a toast).</summary>
    public string? TaskOf(string occurrenceId) =>
        _store.GetOccurrence(occurrenceId) is { } occurrence ? _store.GetReminder(occurrence.ReminderId)?.TaskId : null;

    /// <summary>True while the occurrence was delivered and nobody acted on it yet.</summary>
    public bool CanAct(string occurrenceId) => Actionable(occurrenceId) is not null;

    public IReadOnlyList<Notification> Notifications(int limit = 100) => _store.GetNotifications(limit);

    public int UnreadCount() => _store.UnreadCount();

    /// <summary>Marks one notification read, or all when <paramref name="id"/> is null.</summary>
    public void MarkRead(string? id = null)
    {
        _store.MarkRead(id, Ids.Now(_time));
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _next?.Dispose();
        _safety?.Dispose();
    }

    private bool Act(string occurrenceId, OccurrenceAction action)
    {
        lock (_gate)
        {
            if (Actionable(occurrenceId) is not { } occurrence)
            {
                return false;
            }

            _store.SaveOccurrence(occurrence with { State = OccurrenceState.Acted, Action = action, ActedAt = Ids.Now(_time) });
        }

        _sink.Withdraw(occurrenceId);
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>An occurrence the user can still act on: delivered (in the app or by Windows) or missed.</summary>
    private ReminderOccurrence? Actionable(string occurrenceId) =>
        _store.GetOccurrence(occurrenceId) is { State: OccurrenceState.Delivered or OccurrenceState.DeliveredByOs or OccurrenceState.Missed } occurrence
            ? occurrence
            : null;

    private DateTimeOffset Allowed(DateTimeOffset utc)
    {
        if (Quiet is not { } quiet)
        {
            return utc;
        }

        var local = TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).DateTime;
        var next = quiet.NextAllowed(local);
        return next == local ? utc : ReminderPlanner.ToUtc(next, _time.LocalTimeZone);
    }

    private ReminderAlert? AlertFor(ReminderOccurrence occurrence)
    {
        var reminder = _store.GetReminder(occurrence.ReminderId);
        var task = reminder is null ? null : _tasks.Get(reminder.TaskId);
        return reminder is null || reminder.DeletedAt is not null || task is null || task.IsDeleted || task.IsArchived || _reminders.IsClosed(task)
            ? null
            : new ReminderAlert(occurrence.Id, task.Id, task.Title, task.DueDate, task.DueTime);
    }

    /// <param name="byWindows">Windows already showed it (scheduled toast): record and list it, do not show it again.</param>
    private bool Deliver(ReminderOccurrence occurrence, DateTimeOffset now, bool byWindows)
    {
        var reminder = _store.GetReminder(occurrence.ReminderId);
        var task = reminder is null ? null : _tasks.Get(reminder.TaskId);
        if (reminder is null || reminder.DeletedAt is not null || task is null || task.IsDeleted || task.IsArchived || _reminders.IsClosed(task))
        {
            // Nothing left to remind about (normally replanning already cancelled it).
            _store.SaveOccurrence(occurrence with { State = OccurrenceState.Cancelled });
            return false;
        }

        _store.SaveOccurrence(occurrence with { State = byWindows ? OccurrenceState.DeliveredByOs : OccurrenceState.Delivered, DeliveredAt = now });
        _store.AddNotification(new Notification
        {
            Id = Ids.New(_time),
            Type = NotificationType.Reminder,
            OccurrenceId = occurrence.Id,
            TaskId = task.Id,
            Title = task.Title,
            CreatedAt = now,
        });
        if (!byWindows)
        {
            _sink.Show(new ReminderAlert(occurrence.Id, task.Id, task.Title, task.DueDate, task.DueTime));
        }

        return true;
    }

    /// <summary>One timer to the next pending moment (nothing pending: only the safety check runs).</summary>
    private void Arm()
    {
        lock (_gate)
        {
            _next?.Dispose();
            _next = null;
            if (_store.GetOpenOccurrences().FirstOrDefault() is not { } next)
            {
                return;
            }

            var delay = next.DueUtc - _time.GetUtcNow();
            _next = _time.CreateTimer(_ => OnTimer(), null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        void Work()
        {
            DeliverDue();
            Arm();
        }

        if (_context is null)
        {
            Work();
        }
        else
        {
            _context.Post(_ => Work(), null);
        }
    }
}

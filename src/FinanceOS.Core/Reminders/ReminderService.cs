using FinanceOS.Core.Common;
using FinanceOS.Core.Events;
using FinanceOS.Core.Tasks;

namespace FinanceOS.Core.Reminders;

/// <summary>
/// Reminders on tasks and their planned occurrences (ARCHITECTURE §6). Adding, removing or changing a
/// task (due date, status, trash, archive, undo) replans the task's reminders, so there is always exactly
/// one pending occurrence per reminder at the right moment, or none when it should not fire.
/// The database is the source of truth; <see cref="ReminderScheduler"/> delivers what is planned here.
/// </summary>
public sealed class ReminderService
{
    /// <summary>A planned moment this close in the past still fires (e.g. a reminder set "at 14:00" at 14:00:20).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>Longest "before the due date" offset offered: 30 days.</summary>
    public const int MaxOffsetMinutes = 60 * 24 * 30;

    private readonly IReminderStore _store;
    private readonly TaskService _tasks;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public ReminderService(IReminderStore store, TaskService tasks, IEventBus bus, TimeProvider time)
    {
        _store = store;
        _tasks = tasks;
        _time = time;
        bus.Subscribe<TaskEvent>(e => Replan(e.TaskId));
    }

    /// <summary>Planned occurrences changed: the scheduler re-arms its timer, views refresh.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Reminder> For(string taskId) => _store.GetReminders(taskId);

    /// <summary>Reminds <paramref name="minutesBefore"/> minutes before the task's due moment (0 = at the due moment).</summary>
    public Reminder AddRelative(string taskId, int minutesBefore)
    {
        if (minutesBefore is < 0 or > MaxOffsetMinutes)
        {
            throw new DomainValidationException([new(nameof(Reminder.OffsetMinutes), ValidationCodes.OutOfRange)]);
        }

        return Add(taskId, r => r.Kind == ReminderKind.RelativeToDue && r.OffsetMinutes == minutesBefore,
            id => new Reminder { Id = id, TaskId = taskId, Kind = ReminderKind.RelativeToDue, OffsetMinutes = minutesBefore, TimeZoneId = _time.LocalTimeZone.Id, CreatedAt = Ids.Now(_time) });
    }

    /// <summary>Reminds at a local date and time (seconds are dropped).</summary>
    public Reminder AddAbsolute(string taskId, DateTime local)
    {
        var at = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        return Add(taskId, r => r.Kind == ReminderKind.Absolute && r.AtLocal == at,
            id => new Reminder { Id = id, TaskId = taskId, Kind = ReminderKind.Absolute, AtLocal = at, TimeZoneId = _time.LocalTimeZone.Id, CreatedAt = Ids.Now(_time) });
    }

    public void Remove(string reminderId)
    {
        lock (_gate)
        {
            if (_store.GetReminder(reminderId) is not { DeletedAt: null } reminder)
            {
                return;
            }

            _store.SaveReminder(reminder with { DeletedAt = Ids.Now(_time) });
            foreach (var occurrence in _store.GetOccurrences(reminderId).Where(o => o.IsOpen))
            {
                _store.SaveOccurrence(occurrence with { State = OccurrenceState.Cancelled });
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>When the reminder fires next for the task as it is now; null when it will not fire.</summary>
    public DateTimeOffset? PlannedUtc(Reminder reminder) =>
        _tasks.Get(reminder.TaskId) is { } task ? ReminderPlanner.DueUtc(reminder, task, IsClosed(task), Zone(reminder.TimeZoneId)) : null;

    /// <summary>Brings the pending occurrences of the task's reminders in line with the task.</summary>
    public void Replan(string taskId)
    {
        var changed = false;
        lock (_gate)
        {
            var reminders = _store.GetReminders(taskId);
            if (reminders.Count == 0)
            {
                return;
            }

            var task = _tasks.Get(taskId);
            var closed = task is not null && IsClosed(task);
            var now = Ids.Now(_time);
            foreach (var reminder in reminders)
            {
                var planned = task is null ? null : ReminderPlanner.DueUtc(reminder, task, closed, Zone(reminder.TimeZoneId));
                var occurrences = _store.GetOccurrences(reminder.Id);

                // Cancel what no longer matches: everything when the reminder should not fire; otherwise planned
                // occurrences at another moment. A snooze the user asked for stays while the task is open.
                foreach (var open in occurrences.Where(o => o.IsOpen && (planned is null || (!o.IsSnooze && o.DueUtc != planned))))
                {
                    _store.SaveOccurrence(open with { State = OccurrenceState.Cancelled });
                    changed = true;
                }

                if (planned is not { } due || due < now - Grace)
                {
                    continue;
                }

                var id = ReminderOccurrence.IdFor(reminder.Id, due);
                var existing = occurrences.FirstOrDefault(o => o.Id == id);
                if (existing is null)
                {
                    _store.SaveOccurrence(new ReminderOccurrence { Id = id, ReminderId = reminder.Id, DueUtc = due });
                    changed = true;
                }
                else if (existing.State == OccurrenceState.Cancelled)
                {
                    // Due date moved away and back: the same moment fires again (it never fired).
                    _store.SaveOccurrence(existing with { State = OccurrenceState.Pending });
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal TimeZoneInfo Zone(string id)
    {
        if (id == _time.LocalTimeZone.Id)
        {
            return _time.LocalTimeZone;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return _time.LocalTimeZone;
        }
    }

    internal bool IsClosed(TaskItem task) => _tasks.Statuses().FirstOrDefault(s => s.Id == task.StatusId)?.IsClosed == true;

    private Reminder Add(string taskId, Func<Reminder, bool> same, Func<string, Reminder> create)
    {
        Reminder reminder;
        lock (_gate)
        {
            var task = _tasks.Get(taskId) ?? throw new EntityNotFoundException("task", taskId);
            if (task.IsDeleted)
            {
                throw new DomainValidationException([new(nameof(TaskItem.DeletedAt), ValidationCodes.ReadOnly)]);
            }

            // The same reminder twice would fire twice at the same moment: return the existing one.
            if (_store.GetReminders(taskId).FirstOrDefault(same) is { } existing)
            {
                return existing;
            }

            reminder = create(Ids.New(_time));
            _store.SaveReminder(reminder);
        }

        Replan(taskId);
        Changed?.Invoke(this, EventArgs.Empty);
        return reminder;
    }
}

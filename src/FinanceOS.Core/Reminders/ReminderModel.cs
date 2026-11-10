using FinanceOS.Core.Tasks;

namespace FinanceOS.Core.Reminders;

public enum ReminderKind
{
    /// <summary>At a fixed local date and time.</summary>
    Absolute,

    /// <summary>A number of minutes before the task's due moment (0 = at the due moment).</summary>
    RelativeToDue,
}

/// <summary>
/// A reminder on a task. Several per task are allowed. Follow-up, overdue and repeating reminders
/// come in later milestones (M14, M15); quiet hours in M10.
/// </summary>
public sealed record Reminder
{
    public required string Id { get; init; }

    public required string TaskId { get; init; }

    public ReminderKind Kind { get; init; }

    /// <summary>Minutes before the due moment (<see cref="ReminderKind.RelativeToDue"/>).</summary>
    public int OffsetMinutes { get; init; }

    /// <summary>Local wall-clock moment (<see cref="ReminderKind.Absolute"/>), in <see cref="TimeZoneId"/>.</summary>
    public DateTime? AtLocal { get; init; }

    /// <summary>Time zone the reminder was set in (a due date is a local calendar date).</summary>
    public required string TimeZoneId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>States from ARCHITECTURE §6.2.</summary>
public enum OccurrenceState
{
    Pending,
    Deferred,
    Delivered,
    DeliveredByOs,
    Snoozed,
    Acted,
    Missed,
    Cancelled,
}

public enum OccurrenceAction
{
    Done,
    Dismissed,
    Opened,
    Snoozed,
}

/// <summary>
/// One moment a reminder fires. The id is deterministic (reminder + due moment), so the same moment can
/// never be scheduled twice; <see cref="IsSnooze"/> ones were asked for by the user and do not follow
/// the task's due date.
/// </summary>
public sealed record ReminderOccurrence
{
    public required string Id { get; init; }

    public required string ReminderId { get; init; }

    public required DateTimeOffset DueUtc { get; init; }

    public OccurrenceState State { get; init; } = OccurrenceState.Pending;

    public bool IsSnooze { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public DateTimeOffset? ActedAt { get; init; }

    public OccurrenceAction? Action { get; init; }

    public DateTimeOffset? SnoozedUntil { get; init; }

    public bool IsOpen => State is OccurrenceState.Pending or OccurrenceState.Deferred;

    public static string IdFor(string reminderId, DateTimeOffset dueUtc) =>
        $"{reminderId}@{dueUtc.UtcDateTime:yyyyMMddTHHmmssZ}";
}

public enum NotificationType
{
    /// <summary>A reminder fired: about one task.</summary>
    Reminder,

    /// <summary>Reminders that could not be delivered while the app was not running (count in <see cref="Notification.Count"/>).</summary>
    Missed,
}

/// <summary>A line in the in-app notification center. Texts are formatted by the UI in the current language.</summary>
public sealed record Notification
{
    public required string Id { get; init; }

    public NotificationType Type { get; init; }

    public string? OccurrenceId { get; init; }

    public string? TaskId { get; init; }

    /// <summary>Task title at the time it fired (the task may be renamed or deleted later).</summary>
    public string? Title { get; init; }

    public int Count { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ReadAt { get; init; }

    public bool IsRead => ReadAt is not null;
}

/// <summary>Persistence for reminders, occurrences and the notification center (implemented in FinanceOS.Data).</summary>
public interface IReminderStore
{
    /// <summary>Reminders of a task that are not deleted, oldest first.</summary>
    IReadOnlyList<Reminder> GetReminders(string taskId);

    Reminder? GetReminder(string id);

    void SaveReminder(Reminder reminder);

    ReminderOccurrence? GetOccurrence(string id);

    IReadOnlyList<ReminderOccurrence> GetOccurrences(string reminderId);

    /// <summary>Every pending or deferred occurrence, earliest first.</summary>
    IReadOnlyList<ReminderOccurrence> GetOpenOccurrences();

    void SaveOccurrence(ReminderOccurrence occurrence);

    void AddNotification(Notification notification);

    /// <summary>Newest first.</summary>
    IReadOnlyList<Notification> GetNotifications(int limit);

    /// <summary>Marks one notification read, or all of them when <paramref name="id"/> is null.</summary>
    void MarkRead(string? id, DateTimeOffset at);

    int UnreadCount();
}

/// <summary>What a delivered reminder shows (a Windows toast in the app). The UI formats it in its language.</summary>
public sealed record ReminderAlert(string OccurrenceId, string TaskId, string TaskTitle, DateOnly? DueDate, TimeOnly? DueTime);

/// <summary>Delivers reminders outside the app window (Windows toasts). Behind an interface for tests.</summary>
public interface INotificationSink
{
    void Show(ReminderAlert alert);

    /// <summary>Takes a shown toast back (the reminder was handled in the app).</summary>
    void Withdraw(string occurrenceId);
}

/// <summary>
/// Reminders handed to Windows while the app is not running (ARCHITECTURE §6.3, level 3): scheduled
/// toasts that Windows shows on time if the PC is on. Tags are occurrence ids (§6.5).
/// </summary>
public interface IScheduledToasts
{
    void Schedule(ReminderAlert alert, DateTimeOffset atUtc);

    /// <summary>Tags of toasts Windows already showed (still in Action Center).</summary>
    IReadOnlySet<string> DeliveredTags();

    /// <summary>Withdraws every toast scheduled by this app (the in-process scheduler takes over).</summary>
    void CancelAll();
}

/// <summary>
/// When reminders must not interrupt (ARCHITECTURE §6.6): a daily quiet interval (may cross midnight) and,
/// optionally, the whole weekend. A reminder due then is deferred to the next allowed moment; due dates
/// never change. Configured in Settings from M12; none by default.
/// </summary>
public sealed record QuietHours(TimeOnly Start, TimeOnly End, bool Weekends = false)
{
    public bool IsQuiet(DateTime local)
    {
        if (Weekends && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return true;
        }

        var time = TimeOnly.FromDateTime(local);
        return Start == End ? false : Start < End ? time >= Start && time < End : time >= Start || time < End;
    }

    /// <summary>The first local moment at or after <paramref name="local"/> that is not quiet.</summary>
    public DateTime NextAllowed(DateTime local)
    {
        var candidate = local;
        for (var i = 0; i < 8 && IsQuiet(candidate); i++)
        {
            // Jump to the end of the quiet interval (today or tomorrow), then past a weekend day by day.
            var endToday = candidate.Date + End.ToTimeSpan();
            candidate = IsQuietTime(candidate) ? (endToday > candidate ? endToday : endToday.AddDays(1)) : candidate.Date.AddDays(1) + End.ToTimeSpan();
        }

        return candidate;
    }

    private bool IsQuietTime(DateTime local) => (this with { Weekends = false }).IsQuiet(local);
}

/// <summary>When reminders fire, for a task as stored. Pure, so it is tested without a clock.</summary>
public static class ReminderPlanner
{
    /// <summary>A due date without a time counts as this local time (working hours become configurable in M12).</summary>
    public static readonly TimeOnly DefaultDueTime = new(9, 0);

    /// <summary>
    /// The UTC moment <paramref name="reminder"/> fires for <paramref name="task"/>, or null when it should not
    /// fire: task closed, trashed or archived, or a relative reminder on a task without a due date.
    /// </summary>
    public static DateTimeOffset? DueUtc(Reminder reminder, TaskItem task, bool taskIsClosed, TimeZoneInfo zone)
    {
        if (reminder.DeletedAt is not null || task.IsDeleted || task.IsArchived || taskIsClosed)
        {
            return null;
        }

        DateTime local;
        if (reminder.Kind == ReminderKind.Absolute)
        {
            if (reminder.AtLocal is not { } at)
            {
                return null;
            }

            local = at;
        }
        else
        {
            if (task.DueDate is not { } date)
            {
                return null;
            }

            local = date.ToDateTime(task.DueTime ?? DefaultDueTime).AddMinutes(-reminder.OffsetMinutes);
        }

        return ToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }

    /// <summary>Local wall clock → UTC. A time skipped by a DST jump moves forward; an ambiguous one takes the first.</summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

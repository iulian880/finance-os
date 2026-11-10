using System.Globalization;
using Dapper;
using FinanceOS.Core.Reminders;

namespace FinanceOS.Data.Reminders;

/// <summary><see cref="IReminderStore"/> over the reminders, reminder_occurrences and notifications tables (migrations 001, 003).</summary>
public sealed class SqliteReminderStore(SqliteDatabase database) : IReminderStore
{
    private const string MomentFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    private const string LocalFormat = "yyyy-MM-ddTHH:mm";

    private const string ReminderColumns =
        "id AS Id, task_id AS TaskId, kind AS Kind, offset_min AS OffsetMinutes, at_local AS AtLocal, tz_id AS TimeZoneId, created_at AS CreatedAt, deleted_at AS DeletedAt";

    private const string OccurrenceColumns =
        "id AS Id, reminder_id AS ReminderId, due_utc AS DueUtc, state AS State, origin AS Origin, delivered_at AS DeliveredAt, acted_at AS ActedAt, action AS Action, snoozed_until AS SnoozedUntil";

    public IReadOnlyList<Reminder> GetReminders(string taskId)
    {
        using var connection = database.Open();
        return [.. connection.Query<ReminderRow>($"SELECT {ReminderColumns} FROM reminders WHERE task_id = @taskId AND deleted_at IS NULL ORDER BY created_at, rowid;", new { taskId })
            .Select(r => r.ToReminder())];
    }

    public Reminder? GetReminder(string id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<ReminderRow>($"SELECT {ReminderColumns} FROM reminders WHERE id = @id;", new { id })?.ToReminder();
    }

    public void SaveReminder(Reminder reminder)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO reminders (id, task_id, kind, offset_min, at_local, tz_id, created_at, updated_at, deleted_at)
            VALUES (@Id, @TaskId, @Kind, @OffsetMinutes, @AtLocal, @TimeZoneId, @CreatedAt, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), @DeletedAt)
            ON CONFLICT (id) DO UPDATE SET kind = excluded.kind, offset_min = excluded.offset_min, at_local = excluded.at_local,
                tz_id = excluded.tz_id, updated_at = excluded.updated_at, deleted_at = excluded.deleted_at;
            """,
            new
            {
                reminder.Id,
                reminder.TaskId,
                Kind = reminder.Kind == ReminderKind.Absolute ? "absolute" : "relative_to_due",
                OffsetMinutes = reminder.Kind == ReminderKind.RelativeToDue ? reminder.OffsetMinutes : (int?)null,
                AtLocal = reminder.AtLocal?.ToString(LocalFormat, CultureInfo.InvariantCulture),
                reminder.TimeZoneId,
                CreatedAt = Moment(reminder.CreatedAt),
                DeletedAt = Moment(reminder.DeletedAt),
            });
    }

    public ReminderOccurrence? GetOccurrence(string id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<OccurrenceRow>($"SELECT {OccurrenceColumns} FROM reminder_occurrences WHERE id = @id;", new { id })?.ToOccurrence();
    }

    public IReadOnlyList<ReminderOccurrence> GetOccurrences(string reminderId)
    {
        using var connection = database.Open();
        return [.. connection.Query<OccurrenceRow>($"SELECT {OccurrenceColumns} FROM reminder_occurrences WHERE reminder_id = @reminderId ORDER BY due_utc;", new { reminderId })
            .Select(r => r.ToOccurrence())];
    }

    public IReadOnlyList<ReminderOccurrence> GetOpenOccurrences()
    {
        using var connection = database.Open();
        return [.. connection.Query<OccurrenceRow>($"SELECT {OccurrenceColumns} FROM reminder_occurrences WHERE state IN ('pending', 'deferred') ORDER BY due_utc, id;")
            .Select(r => r.ToOccurrence())];
    }

    public void SaveOccurrence(ReminderOccurrence occurrence)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO reminder_occurrences (id, reminder_id, due_utc, state, origin, delivered_via, delivered_at, acted_at, action, snoozed_until)
            VALUES (@Id, @ReminderId, @DueUtc, @State, @Origin, @DeliveredVia, @DeliveredAt, @ActedAt, @Action, @SnoozedUntil)
            ON CONFLICT (id) DO UPDATE SET due_utc = excluded.due_utc, state = excluded.state, delivered_via = excluded.delivered_via, delivered_at = excluded.delivered_at,
                acted_at = excluded.acted_at, action = excluded.action, snoozed_until = excluded.snoozed_until,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
            """,
            new
            {
                occurrence.Id,
                occurrence.ReminderId,
                DueUtc = Moment(occurrence.DueUtc),
                State = Snake(occurrence.State.ToString()),
                Origin = occurrence.IsSnooze ? "snooze" : "plan",
                DeliveredVia = occurrence.DeliveredAt is null ? null : occurrence.State == OccurrenceState.DeliveredByOs ? "scheduled_toast" : "toast",
                DeliveredAt = Moment(occurrence.DeliveredAt),
                ActedAt = Moment(occurrence.ActedAt),
                Action = occurrence.Action is { } action ? Snake(action.ToString()) : null,
                SnoozedUntil = Moment(occurrence.SnoozedUntil),
            });
    }

    public void AddNotification(Notification notification)
    {
        using var connection = database.Open();
        connection.Execute(
            "INSERT INTO notifications (id, occurrence_id, task_id, type, title, body, created_at, read_at) VALUES (@Id, @OccurrenceId, @TaskId, @Type, @Title, @Body, @CreatedAt, @ReadAt);",
            new
            {
                notification.Id,
                notification.OccurrenceId,
                notification.TaskId,
                Type = notification.Type == NotificationType.Missed ? "missed" : "reminder",
                Title = notification.Title ?? "",
                Body = notification.Count.ToString(CultureInfo.InvariantCulture),
                CreatedAt = Moment(notification.CreatedAt),
                ReadAt = Moment(notification.ReadAt),
            });
    }

    public IReadOnlyList<Notification> GetNotifications(int limit)
    {
        using var connection = database.Open();
        return [.. connection.Query<NotificationRow>(
                "SELECT id AS Id, occurrence_id AS OccurrenceId, task_id AS TaskId, type AS Type, title AS Title, body AS Body, created_at AS CreatedAt, read_at AS ReadAt FROM notifications ORDER BY created_at DESC, rowid DESC LIMIT @limit;",
                new { limit })
            .Select(r => r.ToNotification())];
    }

    public void MarkRead(string? id, DateTimeOffset at)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE notifications SET read_at = @At WHERE read_at IS NULL AND (@id IS NULL OR id = @id);", new { id, At = Moment(at) });
    }

    public int UnreadCount()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT count(*) FROM notifications WHERE read_at IS NULL;");
    }

    private static string? Moment(DateTimeOffset? at) => at?.UtcDateTime.ToString(MomentFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? text) => text is null
        ? null
        : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>"DeliveredByOs" → "delivered_by_os" (the CHECK constraints in 001).</summary>
    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static T FromSnake<T>(string value)
        where T : struct, Enum => Enum.Parse<T>(value.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);

    private sealed class ReminderRow
    {
        public string Id { get; init; } = "";

        public string TaskId { get; init; } = "";

        public string Kind { get; init; } = "";

        public long? OffsetMinutes { get; init; }

        public string? AtLocal { get; init; }

        public string? TimeZoneId { get; init; }

        public string CreatedAt { get; init; } = "";

        public string? DeletedAt { get; init; }

        public Reminder ToReminder() => new()
        {
            Id = Id,
            TaskId = TaskId,
            Kind = Kind == "absolute" ? ReminderKind.Absolute : ReminderKind.RelativeToDue,
            OffsetMinutes = (int)(OffsetMinutes ?? 0),
            AtLocal = AtLocal is null ? null : DateTime.ParseExact(AtLocal, LocalFormat, CultureInfo.InvariantCulture),
            TimeZoneId = TimeZoneId ?? TimeZoneInfo.Local.Id,
            CreatedAt = Parse(CreatedAt)!.Value,
            DeletedAt = Parse(DeletedAt),
        };
    }

    private sealed class OccurrenceRow
    {
        public string Id { get; init; } = "";

        public string ReminderId { get; init; } = "";

        public string DueUtc { get; init; } = "";

        public string State { get; init; } = "";

        public string Origin { get; init; } = "plan";

        public string? DeliveredAt { get; init; }

        public string? ActedAt { get; init; }

        public string? Action { get; init; }

        public string? SnoozedUntil { get; init; }

        public ReminderOccurrence ToOccurrence() => new()
        {
            Id = Id,
            ReminderId = ReminderId,
            DueUtc = Parse(DueUtc)!.Value,
            State = FromSnake<OccurrenceState>(State),
            IsSnooze = Origin == "snooze",
            DeliveredAt = Parse(DeliveredAt),
            ActedAt = Parse(ActedAt),
            Action = Action is null ? null : FromSnake<OccurrenceAction>(Action),
            SnoozedUntil = Parse(SnoozedUntil),
        };
    }

    private sealed class NotificationRow
    {
        public string Id { get; init; } = "";

        public string? OccurrenceId { get; init; }

        public string? TaskId { get; init; }

        public string Type { get; init; } = "";

        public string? Title { get; init; }

        public string? Body { get; init; }

        public string CreatedAt { get; init; } = "";

        public string? ReadAt { get; init; }

        public Notification ToNotification() => new()
        {
            Id = Id,
            Type = Type == "missed" ? NotificationType.Missed : NotificationType.Reminder,
            OccurrenceId = OccurrenceId,
            TaskId = TaskId,
            Title = string.IsNullOrEmpty(Title) ? null : Title,
            Count = int.TryParse(Body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0,
            CreatedAt = Parse(CreatedAt)!.Value,
            ReadAt = Parse(ReadAt),
        };
    }
}

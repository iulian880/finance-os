using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.Platform.Windows.Notifications;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Reminders;

/// <summary>Reminder texts in the UI language.</summary>
public static class ReminderText
{
    /// <summary>"Before the due date" presets offered in the details panel, in minutes.</summary>
    public static readonly int[] Presets = [0, 15, 30, 60, 120, 1440];

    /// <summary>Snooze choices (toast list and notification center): minutes, or "tomorrow".</summary>
    public static readonly string[] SnoozeChoices = ["5", "10", "15", "30", "60", "tomorrow"];

    /// <summary>"Cu 15 min înainte", "La termen", "10 noiembrie 2026, 09:00".</summary>
    public static string Describe(Reminder reminder, Localizer l)
    {
        if (reminder.Kind == ReminderKind.Absolute && reminder.AtLocal is { } at)
        {
            return DisplayFormat.DateAndTime(at, l.Culture);
        }

        return l.TryGet($"Reminder.Preset.{reminder.OffsetMinutes}", out var preset)
            ? preset
            : l.Format("Reminder.MinutesBefore", reminder.OffsetMinutes);
    }

    public static string Snooze(string choice, Localizer l) => l[$"Reminder.Snooze.{choice}"];

    public static IReadOnlyList<SnoozeChoice> ToastChoices(Localizer l) => [.. SnoozeChoices.Select(c => new SnoozeChoice(c, Snooze(c, l)))];

    /// <summary>Applies a snooze choice ("15" minutes or "tomorrow") to an occurrence.</summary>
    public static bool Apply(ReminderScheduler scheduler, string occurrenceId, string choice) => choice == "tomorrow"
        ? scheduler.SnoozeUntilTomorrow(occurrenceId)
        : int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            && scheduler.Snooze(occurrenceId, TimeSpan.FromMinutes(minutes));
}

/// <summary>
/// Scheduled toasts for a separate data folder (tests, <c>--data-root</c>, <c>FINANCEOS_DATA_ROOT</c>). Windows keeps
/// scheduled toasts per app, not per data folder: a test instance must neither schedule its own nor cancel
/// the ones the real data left for when the app is closed. Reminders still show while it runs.
/// </summary>
public sealed class InertScheduledToasts : IScheduledToasts
{
    public void Schedule(ReminderAlert alert, DateTimeOffset atUtc)
    {
    }

    public IReadOnlySet<string> DeliveredTags() => new HashSet<string>();

    public void CancelAll()
    {
    }
}

/// <summary>Shows reminders as Windows toasts (title = task, text = due, snooze list + Done) and takes them back.</summary>
/// <remarks>Also schedules toasts in Windows for when the app is closed (ARCHITECTURE §6.3, level 3).</remarks>
public sealed class WindowsReminderSink(Localizer localizer, TimeProvider time) : INotificationSink, IScheduledToasts
{
    public void Show(ReminderAlert alert) => ToastService.ShowNow(alert.OccurrenceId, alert.TaskTitle, Body(alert, time.GetLocalNow().DateTime));

    public void Withdraw(string occurrenceId) => ToastService.Remove(occurrenceId);

    /// <summary>The text is written now in the current language (Windows shows it as it is).</summary>
    public void Schedule(ReminderAlert alert, DateTimeOffset atUtc) =>
        ToastService.Schedule(alert.OccurrenceId, alert.TaskTitle, Body(alert, TimeZoneInfo.ConvertTime(atUtc, time.LocalTimeZone).DateTime), atUtc);

    public IReadOnlySet<string> DeliveredTags() => ToastService.GetDeliveredTags().ToHashSet();

    public void CancelAll() => ToastService.CancelAllScheduled();

    private string Body(ReminderAlert alert, DateTime localNow)
    {
        var task = new TaskItem { Id = alert.TaskId, Title = alert.TaskTitle, DueDate = alert.DueDate, DueTime = alert.DueTime };
        var due = TaskText.DueLabel(task, localNow, localizer);
        return due is null ? localizer["Notifications.Reminder"] : localizer.Format("Reminder.Toast.Due", due);
    }
}

/// <summary>One line in the notification center, with its own commands.</summary>
public sealed class NotificationItemViewModel
{
    public NotificationItemViewModel(Notification notification, string title, string detail, string time, bool canAct,
        IReadOnlyList<ChoiceOption<string>> snoozeOptions, Action<NotificationItemViewModel, string?> act)
    {
        Notification = notification;
        Title = title;
        Detail = detail;
        Time = time;
        CanAct = canAct;
        SnoozeOptions = snoozeOptions;
        OpenCommand = new RelayCommand(() => act(this, "open"));
        DoneCommand = new RelayCommand(() => act(this, "done"));
        DismissCommand = new RelayCommand(() => act(this, "dismiss"));
        SnoozeCommand = new RelayCommand<ChoiceOption<string>>(choice => act(this, choice is null ? null : "snooze:" + choice.Value));
    }

    public Notification Notification { get; }

    public string Title { get; }

    public string Detail { get; }

    public string Time { get; }

    public bool IsUnread => !Notification.IsRead;

    /// <summary>The reminder was not handled yet: Done / Snooze / Dismiss are offered.</summary>
    public bool CanAct { get; }

    public bool HasTask => Notification.TaskId is not null;

    public IReadOnlyList<ChoiceOption<string>> SnoozeOptions { get; }

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand DoneCommand { get; }

    public IRelayCommand DismissCommand { get; }

    public IRelayCommand<ChoiceOption<string>> SnoozeCommand { get; }
}

/// <summary>
/// The in-app notification center (right panel): every reminder that fired and the "missed" summaries,
/// newest first, always complete even when Windows hid the toast (Focus Assist). Open goes to the task;
/// Done / Snooze / Dismiss act on the reminder and mark the line read.
/// </summary>
public sealed partial class NotificationCenterViewModel : ObservableObject
{
    private readonly ReminderScheduler _scheduler;
    private readonly TimeProvider _time;
    private readonly Localizer _l;
    private readonly SynchronizationContext? _ui;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public NotificationCenterViewModel(ReminderScheduler scheduler, TimeProvider time, Localizer localizer)
    {
        _scheduler = scheduler;
        _time = time;
        _l = localizer;
        _ui = SynchronizationContext.Current;
        SnoozeOptions = [.. ReminderText.SnoozeChoices.Select(c => new ChoiceOption<string>(c, () => ReminderText.Snooze(c, _l)))];
        scheduler.NotificationsChanged += (_, _) => Post(Refresh);
        localizer.Changed += (_, _) =>
        {
            foreach (var option in SnoozeOptions)
            {
                option.Refresh();
            }

            Refresh();
        };
        Refresh();
    }

    /// <summary>Opens a task in the app (set by the shell).</summary>
    public Action<string>? OpenTask { get; set; }

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    public IReadOnlyList<ChoiceOption<string>> SnoozeOptions { get; }

    public bool HasUnread => UnreadCount > 0;

    public bool IsEmpty => Items.Count == 0;

    public void Refresh()
    {
        Items.Clear();
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        foreach (var notification in _scheduler.Notifications())
        {
            var local = TimeZoneInfo.ConvertTime(notification.CreatedAt, _time.LocalTimeZone).DateTime;
            var when = DateOnly.FromDateTime(local) == today ? local.ToString("HH:mm", _l.Culture) : DisplayFormat.DateAndTime(local, _l.Culture);
            Items.Add(notification.Type == NotificationType.Missed
                ? new NotificationItemViewModel(notification, _l.Format("Notifications.Missed", notification.Count), _l["Notifications.MissedHint"], when, false, SnoozeOptions, Act)
                : new NotificationItemViewModel(notification, notification.Title ?? "", _l["Notifications.Reminder"], when,
                    notification.OccurrenceId is { } occurrence && _scheduler.CanAct(occurrence), SnoozeOptions, Act));
        }

        UnreadCount = _scheduler.UnreadCount();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void MarkAllRead() => _scheduler.MarkRead();

    /// <summary>open, done, dismiss, snooze:&lt;choice&gt;. Every action marks the line read.</summary>
    private void Act(NotificationItemViewModel item, string? action)
    {
        if (action is null)
        {
            return;
        }

        var occurrence = item.Notification.OccurrenceId;
        switch (action)
        {
            case "open":
                if (occurrence is not null)
                {
                    _scheduler.Opened(occurrence);
                }

                if (item.Notification.TaskId is { } task)
                {
                    OpenTask?.Invoke(task);
                }

                break;
            case "done" when occurrence is not null:
                _scheduler.Complete(occurrence);
                break;
            case "dismiss" when occurrence is not null:
                _scheduler.Dismiss(occurrence);
                break;
            default:
                if (occurrence is not null && action.StartsWith("snooze:", StringComparison.Ordinal))
                {
                    ReminderText.Apply(_scheduler, occurrence, action["snooze:".Length..]);
                }

                break;
        }

        _scheduler.MarkRead(item.Notification.Id);
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}

using Microsoft.Toolkit.Uwp.Notifications;

namespace FinanceOS.Platform.Windows.Notifications;

/// <summary>What the user did on a toast. <see cref="Action"/> is "open", "done" or "snooze".</summary>
public sealed record ToastActivation(string Action, string? OccurrenceId, string? SnoozeMinutes);

public sealed record ScheduledToastInfo(string Tag, DateTimeOffset DeliveryTime);

/// <summary>One choice in the toast's snooze list: id sent back on activation ("15", "tomorrow"), label shown.</summary>
public sealed record SnoozeChoice(string Id, string Label);

/// <summary>Texts on toasts, in the UI language (set by the app; a toast keeps the texts it was created with).</summary>
public sealed record ToastLabels(string Snooze, string Done, IReadOnlyList<SnoozeChoice> SnoozeChoices, string DefaultSnooze = "15");

/// <summary>
/// Thin wrapper over Windows toast notifications for an unpackaged desktop exe.
/// M0 spike shape; becomes the <c>INotificationSink</c> implementation in M9.
/// </summary>
public sealed class ToastService
{
    public const string ReminderGroup = "reminders";

    /// <summary>Raised on a background thread when the user clicks a toast or one of its buttons.</summary>
    public event Action<ToastActivation>? Activated;

    /// <summary>
    /// Call as early as possible during startup, after attaching <see cref="Activated"/>.
    /// Subscribing registers the COM activator, so clicks on toasts — including ones
    /// Windows delivered while the app was closed — are routed to this process.
    /// </summary>
    public void Initialize() => ToastNotificationManagerCompat.OnActivated += OnActivated;

    public static ToastLabels Labels { get; set; } = new("Snooze", "Done",
        [new("5", "5 min"), new("10", "10 min"), new("15", "15 min"), new("30", "30 min"), new("60", "1 h"), new("tomorrow", "Tomorrow")]);

    /// <summary>Removes a delivered toast from Action Center (the reminder was handled in the app).</summary>
    public static void Remove(string tag)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(tag, ReminderGroup);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Not registered or already gone: nothing to take back.
        }
    }

    public static bool WasProcessLaunchedByToast() => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

    /// <summary>"Enabled", or the reason toasts are blocked (user, group policy, manifest).</summary>
    public static string NotificationSetting => ToastNotificationManagerCompat.CreateToastNotifier().Setting.ToString();

    public static void ShowNow(string tag, string title, string body) =>
        Build(tag, title, body).Show(t =>
        {
            t.Tag = tag;
            t.Group = ReminderGroup;
        });

    public static void Schedule(string tag, string title, string body, DateTimeOffset deliveryTime) =>
        Build(tag, title, body).Schedule(deliveryTime, t =>
        {
            t.Tag = tag;
            t.Group = ReminderGroup;
        });

    public static IReadOnlyList<ScheduledToastInfo> GetScheduled() =>
        [.. ToastNotificationManagerCompat.CreateToastNotifier()
            .GetScheduledToastNotifications()
            .Select(s => new ScheduledToastInfo(s.Tag, s.DeliveryTime))];

    public static int CancelAllScheduled()
    {
        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        var scheduled = notifier.GetScheduledToastNotifications();
        foreach (var toast in scheduled)
        {
            notifier.RemoveFromSchedule(toast);
        }

        return scheduled.Count;
    }

    /// <summary>Tags of toasts that were delivered and are still in Action Center.</summary>
    public static IReadOnlyList<string> GetDeliveredTags() =>
        [.. ToastNotificationManagerCompat.History.GetHistory().Select(t => t.Tag)];

    private static ToastContentBuilder Build(string tag, string title, string body) =>
        new ToastContentBuilder()
            // Builder-level arguments are copied onto every button, so each action knows its occurrence.
            .AddArgument("occ", tag)
            .AddText(title)
            .AddText(body)
            .AddToastInput(SnoozeBox())
            .AddButton(new ToastButton().SetContent(Labels.Snooze).AddArgument("action", "snooze"))
            .AddButton(new ToastButton().SetContent(Labels.Done).AddArgument("action", "done"));

    private static ToastSelectionBox SnoozeBox()
    {
        var box = new ToastSelectionBox("snooze") { DefaultSelectionBoxItemId = Labels.DefaultSnooze };
        foreach (var choice in Labels.SnoozeChoices)
        {
            box.Items.Add(new ToastSelectionBoxItem(choice.Id, choice.Label));
        }

        return box;
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        args.TryGetValue("action", out string? action);
        args.TryGetValue("occ", out string? occurrence);
        e.UserInput.TryGetValue("snooze", out var snooze);
        Activated?.Invoke(new ToastActivation(action ?? "open", occurrence, snooze as string));
    }
}

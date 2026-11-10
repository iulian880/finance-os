using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FinanceOS.UI.Controls;

public enum ToastTone
{
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>An in-app notification. <see cref="Duration"/> null keeps it until dismissed.</summary>
public sealed class ToastMessage
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    public required string Title { get; init; }

    public string? Message { get; init; }

    public ToastTone Tone { get; init; } = ToastTone.Info;

    /// <summary>Optional single action, e.g. "Undo". Running it also dismisses the toast.</summary>
    public string? ActionText { get; init; }

    public Action? Action { get; init; }

    public TimeSpan? Duration { get; init; } = DefaultDuration;

    /// <summary>Text read by screen readers when the toast appears.</summary>
    public string AccessibleText => string.IsNullOrEmpty(Message) ? Title : $"{Title}. {Message}";

    internal DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// Stack of in-app toasts in the bottom-right corner of the window. Newest at the bottom, at most
/// <see cref="MaxVisible"/>; hovering pauses expiry. Each toast is announced to screen readers
/// (live region). Place it over the page in the window's root grid.
/// </summary>
public class ToastHost : Control
{
    public static readonly RoutedCommand DismissCommand = new(nameof(DismissCommand), typeof(ToastHost));
    public static readonly RoutedCommand RunActionCommand = new(nameof(RunActionCommand), typeof(ToastHost));

    public static readonly DependencyProperty MaxVisibleProperty = DependencyProperty.Register(
        nameof(MaxVisible), typeof(int), typeof(ToastHost), new FrameworkPropertyMetadata(3), v => v is int i && i > 0);

    /// <summary>After the pointer leaves, remaining toasts stay at least this long.</summary>
    private static readonly TimeSpan GraceAfterHover = TimeSpan.FromSeconds(2);

    private readonly ObservableCollection<ToastMessage> _items = [];
    private readonly DispatcherTimer _timer;

    static ToastHost()
    {
        FocusableProperty.OverrideMetadata(typeof(ToastHost), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ToastHost), new FrameworkPropertyMetadata(false));
    }

    public ToastHost()
    {
        Items = new ReadOnlyObservableCollection<ToastMessage>(_items);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RemoveExpired(), Dispatcher);
        CommandBindings.Add(new CommandBinding(DismissCommand, (_, e) => Dismiss(e.Parameter as ToastMessage)));
        CommandBindings.Add(new CommandBinding(RunActionCommand, (_, e) => RunAction(e.Parameter as ToastMessage)));
        MouseLeave += (_, _) => ExtendAfterHover();
        Unloaded += (_, _) => _timer.Stop();
    }

    public ReadOnlyObservableCollection<ToastMessage> Items { get; }

    public int MaxVisible
    {
        get => (int)GetValue(MaxVisibleProperty);
        set => SetValue(MaxVisibleProperty, value);
    }

    /// <summary>Clock for expiry; replaceable in tests.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    public ToastMessage Show(ToastMessage toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        toast.ExpiresAt = toast.Duration is { } duration ? Time.GetUtcNow() + duration : null;
        _items.Add(toast);
        while (_items.Count > MaxVisible)
        {
            _items.RemoveAt(0);
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AnnounceAndAnimate(toast));
        return toast;
    }

    public void Dismiss(ToastMessage? toast)
    {
        if (toast is not null)
        {
            _items.Remove(toast);
        }

        if (_items.Count == 0)
        {
            _timer.Stop();
        }
    }

    /// <summary>Removes toasts whose time is up. Paused while the pointer is over the stack.</summary>
    internal void RemoveExpired()
    {
        if (IsMouseOver)
        {
            return;
        }

        var now = Time.GetUtcNow();
        foreach (var toast in _items.Where(t => t.ExpiresAt is { } at && at <= now).ToList())
        {
            Dismiss(toast);
        }
    }

    private void RunAction(ToastMessage? toast)
    {
        if (toast is null)
        {
            return;
        }

        Dismiss(toast);
        toast.Action?.Invoke();
    }

    private void ExtendAfterHover()
    {
        var minimum = Time.GetUtcNow() + GraceAfterHover;
        foreach (var toast in _items)
        {
            if (toast.ExpiresAt is { } at && at < minimum)
            {
                toast.ExpiresAt = minimum;
            }
        }
    }

    private void AnnounceAndAnimate(ToastMessage toast)
    {
        if (GetTemplateChild("PART_Items") is not ItemsControl list
            || list.ItemContainerGenerator.ContainerFromItem(toast) is not FrameworkElement container)
        {
            return;
        }

        Motion.Enter(container, offsetY: 12);
        AutomationProperties.SetName(container, toast.AccessibleText);
        AutomationProperties.SetLiveSetting(container, toast.Tone == ToastTone.Danger ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        UIElementAutomationPeer.CreatePeerForElement(container)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

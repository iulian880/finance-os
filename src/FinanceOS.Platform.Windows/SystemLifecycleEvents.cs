using Microsoft.Win32;

namespace FinanceOS.Platform.Windows;

/// <summary>
/// System events that require reminder reconciliation (ARCHITECTURE §6.4):
/// "resume", "time-changed", "session-unlock". "suspend" is reported for diagnostics.
/// </summary>
public sealed class SystemLifecycleEvents : IDisposable
{
    public SystemLifecycleEvents()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Raised on the SystemEvents thread.</summary>
    public event Action<string>? Changed;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Changed?.Invoke("resume");
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            Changed?.Invoke("suspend");
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => Changed?.Invoke("time-changed");

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            Changed?.Invoke("session-unlock");
        }
    }

    public void Dispose()
    {
        // SystemEvents are static; not unsubscribing would leak this instance.
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}

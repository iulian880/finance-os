using System.Diagnostics;
using System.Windows;
using FinanceOS.Platform.Windows.Notifications;
using FinanceOS.App.Services;
using Microsoft.Extensions.Logging;

namespace FinanceOS.App.Dev;

/// <summary>M0 spike window (Debug, --spike): buttons that exercise toasts and tray behavior, plus the spike log.</summary>
public partial class SpikeWindow : Window
{
    public SpikeWindow(bool toastsAvailable, double? startupMs)
    {
        InitializeComponent();
        ToastButtons.IsEnabled = toastsAvailable;
        if (startupMs is { } ms)
        {
            SetStartupTime(ms);
        }

        RefreshStatus(toastsAvailable);
        RefreshLog();
#if DEBUG
        TokensButton.Visibility = Visibility.Visible;
#endif
    }

    private void OnOpenTokens(object sender, RoutedEventArgs e)
    {
#if DEBUG
        new DesignTokensWindow().Show();
#endif
    }

    public void SetStartupTime(double milliseconds) => StartupText.Text = $"{milliseconds:F0} ms (până la primul frame)";

    public void RefreshLog()
    {
        LogBox.Text = string.Join(Environment.NewLine, AppLog.Writer.Tail(300));
        LogBox.ScrollToEnd();
    }

    private void RefreshStatus(bool toastsAvailable)
    {
        using var process = Process.GetCurrentProcess();
        MemoryText.Text = $"working set {process.WorkingSet64 / 1048576.0:F0} MB · privat {process.PrivateMemorySize64 / 1048576.0:F0} MB";
        PidText.Text = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        NotificationText.Text = toastsAvailable ? ToastService.NotificationSetting : "neînregistrate (mod benchmark)";
    }

    private void OnShowNow(object sender, RoutedEventArgs e)
    {
        var tag = NewTag();
        ToastService.ShowNow(tag, "Verifică facturile intercompany", "Test M0: toast imediat. Încearcă butoanele.");
        AppLog.Spike.LogInformation("toast shown tag={Tag}", tag);
        RefreshLog();
    }

    private void OnScheduleOneMinute(object sender, RoutedEventArgs e) => Schedule(1);

    private void OnScheduleThreeMinutes(object sender, RoutedEventArgs e) => Schedule(3);

    private void Schedule(int minutes)
    {
        var tag = NewTag();
        var at = DateTimeOffset.Now.AddMinutes(minutes);
        ToastService.Schedule(tag, "Reminder programat", $"Programat pentru {at:HH:mm:ss}. Închide aplicația din tray și așteaptă.", at);
        AppLog.Spike.LogInformation("toast scheduled tag={Tag} at={DueUtc}", tag, at);
        RefreshLog();
    }

    private void OnListToasts(object sender, RoutedEventArgs e)
    {
        var scheduled = ToastService.GetScheduled();
        AppLog.Spike.LogInformation("scheduled count={Count}: {Tags}", scheduled.Count, string.Join(", ", scheduled.Select(s => $"{s.Tag}@{s.DeliveryTime:HH:mm:ss}")));
        var delivered = ToastService.GetDeliveredTags();
        AppLog.Spike.LogInformation("delivered (in Action Center) count={Count}: {Tags}", delivered.Count, string.Join(", ", delivered));
        RefreshLog();
    }

    private void OnCancelScheduled(object sender, RoutedEventArgs e)
    {
        AppLog.Spike.LogInformation("cancelled scheduled count={Count}", ToastService.CancelAllScheduled());
        RefreshLog();
    }

    private void OnCloseToTray(object sender, RoutedEventArgs e) => Close();

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshStatus(ToastButtons.IsEnabled);
        RefreshLog();
    }

    private void OnExit(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApplication();

    private static string NewTag() => $"m0-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
}

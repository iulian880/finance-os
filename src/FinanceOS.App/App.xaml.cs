using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FinanceOS.App.Localization;
using FinanceOS.App.Recovery;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.Data;
using FinanceOS.Data.Backup;
using FinanceOS.Platform.Windows;
using FinanceOS.Platform.Windows.Notifications;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinanceOS.App;

public partial class App : Application
{
    /// <summary>How often the tray-resident app checks whether today's automatic backup exists.</summary>
    private static readonly TimeSpan DailyBackupCheck = TimeSpan.FromHours(1);

    private readonly ILogger _log = AppLog.Factory.CreateLogger("App");
    private StartupOptions _options = StartupOptions.Parse([]);
    private SingleInstance? _singleInstance;
    private ToastService? _toasts;
    private SystemLifecycleEvents? _systemEvents;
    private TrayIcon? _tray;
    private Window? _window;
    private RecoveryWindow? _recovery;
    private ServiceProvider? _services;
    private UserSettings? _settings;
    private DispatcherTimer? _backupTimer;
    private double? _startupMs;
    private bool _exiting;
    private bool _databaseReady;
    private bool _showWindowWhenReady;
    private readonly List<ToastActivation> _pendingActivations = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = StartupOptions.Parse(e.Args);
        _log.LogInformation("start args=[{Args}]", string.Join(' ', e.Args));

        DispatcherUnhandledException += (_, ex) => _log.LogCritical(ex.Exception, "Unhandled exception on the UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            _log.LogCritical(ex.ExceptionObject as Exception, "Unhandled exception, terminating={Terminating}", ex.IsTerminating);

        if (_options.Probe)
        {
            RunProbe();
            Shutdown();
            return;
        }

        // Settings first (preferences.json works without the database), then the theme they ask for.
        // --theme / --accent override them for this session only.
        _settings = new UserSettings(new PreferencesStore(Path.Combine(_options.DataRoot ?? AppPaths.DataRoot, "preferences.json")));
        var theme = _options.Theme switch
        {
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            "system" => ThemePreference.System,
            _ => _settings.Theme ?? ThemePreference.System,
        };
        ThemeManager.Current.Initialize(this, theme, _options.AccentId ?? _settings.Current.Accent);
        if (_options.Theme is null && _options.AccentId is null)
        {
            ThemeManager.Current.Changed += (_, _) => _settings.Update(p => p with
            {
                Theme = UserSettings.ThemeCode(ThemeManager.Current.Preference),
                Accent = ThemeManager.Current.Accent.Id,
            });
        }
        InitializeLanguage();

        // Date and time pickers: interface language plus the chosen numeric date format.
        ApplyDateFormat();
        Localizer.Current.Changed += (_, _) => ApplyDateFormat();
        _settings.Changed += (_, _) => ApplyDateFormat();

#if DEBUG
        if (_options.IsDevPage)
        {
            ShowDevPage();
            return;
        }
#endif

        if (!_options.Benchmark)
        {
            // Single-instance check comes before toast registration and before the database: only the
            // primary instance may register the COM activator and open the data file.
            _singleInstance = new SingleInstance("FinanceOS");
            if (!_singleInstance.IsPrimary)
            {
                var forwarded = _singleInstance.TrySendToPrimary(e.Args, TimeSpan.FromSeconds(2));
                _log.LogInformation("secondary instance, forwarded={Forwarded}", forwarded);
                Shutdown();
                return;
            }

            _singleInstance.StartListening(args => Dispatcher.BeginInvoke(() => OnForwardedArguments(args)));

            _toasts = new ToastService();
            _toasts.Activated += a => Dispatcher.BeginInvoke(() => OnToastActivated(a));
            _toasts.Initialize();

            _systemEvents = new SystemLifecycleEvents();
            _systemEvents.Changed += reason => Dispatcher.BeginInvoke(() => OnSystemEvent(reason));
        }

        // The process lives in the tray; closing the window does not exit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _tray = new TrayIcon(ShowMainWindow, ExitApplication, Localizer.Current);

        var launchedByToast = !_options.Benchmark && ToastService.WasProcessLaunchedByToast();
        _showWindowWhenReady = !(launchedByToast || _options.StartInTray);
        if (!_showWindowWhenReady)
        {
            _log.LogInformation("started hidden (toast={Toast}, tray={Tray})", launchedByToast, _options.StartInTray);
        }

        StartDatabase();
    }

    /// <summary>
    /// Language before any window, the recovery screen included: <c>--lang</c> for this session, else the
    /// saved choice, else Windows' display language. Every later change (Settings) is saved right away.
    /// </summary>
    private void InitializeLanguage()
    {
        var localizer = Localizer.Current;
        localizer.Register(AppStrings.Manager);
        var saved = Localizer.FromCode(_settings!.Current.Language);
        localizer.SetLanguage(_options.Language ?? saved ?? Localizer.FromCulture(CultureInfo.CurrentUICulture));
        ToastService.Labels = new ToastLabels(localizer["Toast.Snooze"], localizer["Toast.Done"], FinanceOS.App.Reminders.ReminderText.ToastChoices(localizer));
        _log.LogInformation("language={Language} saved={Saved}", Localizer.CodeFor(localizer.Language), saved is not null);

        localizer.Changed += (_, _) =>
        {
            var code = Localizer.CodeFor(localizer.Language);
            _settings.Update(p => p with { Language = code });
            ToastService.Labels = new ToastLabels(localizer["Toast.Snooze"], localizer["Toast.Done"], FinanceOS.App.Reminders.ReminderText.ToastChoices(localizer));
            _log.LogInformation("language changed -> {Language}", code);
        };
    }

    private void ApplyDateFormat() => DateFormats.Apply(Localizer.Current.Culture, _settings?.DateFormat);

    /// <summary>
    /// ARCHITECTURE §7 steps 2–3: integrity check, migrations (with a backup first). If the database is not
    /// usable, the recovery screen is shown instead of the main window, even when starting in the tray:
    /// the user must know their data needs attention.
    /// </summary>
    private void StartDatabase()
    {
        _services ??= AppServices.Build(AppLog.Factory, Localizer.Current, _options.DataRoot, _settings);
        var startup = _services.GetRequiredService<DatabaseStartup>();
        var result = startup.Run();

        if (result.Status == DatabaseStatus.Ready)
        {
            OnDatabaseReady();
            return;
        }

        var viewModel = new RecoveryViewModel(result, new RecoveryActions(startup, _services.GetRequiredService<BackupService>()), TimeProvider.System, Localizer.Current);
        _recovery = new RecoveryWindow(viewModel);
        viewModel.Recovered += (_, _) =>
        {
            var window = _recovery;
            _recovery = null;
            _showWindowWhenReady = true;
            OnDatabaseReady();
            window?.Close();
        };
        viewModel.ExitRequested += (_, _) => ExitApplication();
        _recovery.Closed += (_, _) =>
        {
            if (_recovery is not null)
            {
                // Closed with the X while the data is still unusable: nothing to run, exit.
                _recovery = null;
                ExitApplication();
            }
        };
        _recovery.Show();
    }

    private void OnDatabaseReady()
    {
        _databaseReady = true;
#if DEBUG
        if (_options.Seed)
        {
            Dev.DevSeed.Run(_services!.GetRequiredService<FinanceOS.Core.Tasks.TaskService>(), _services!.GetRequiredService<FinanceOS.Core.Reminders.ReminderService>(), TimeProvider.System);
        }
#endif

        // Automatic daily backup: off the UI thread, after start-up, re-checked while the app lives in the tray.
        _backupTimer = new DispatcherTimer(DailyBackupCheck, DispatcherPriority.Background, (_, _) => RunDailyBackup(), Dispatcher);
        _backupTimer.Start();

        if (_showWindowWhenReady)
        {
            ShowMainWindow();
        }

        // Reminders: deliver what is due, then one timer to the next one (ARCHITECTURE §6.3).
        if (!_options.Benchmark)
        {
            _services!.GetRequiredService<FinanceOS.Core.Reminders.ReminderScheduler>().Start();
        }

        foreach (var activation in _pendingActivations)
        {
            OnToastActivated(activation);
        }

        _pendingActivations.Clear();

        if (!_options.Benchmark)
        {
            // Benchmarks measure start-up and idle memory; a backup in the middle would skew both.
            Dispatcher.BeginInvoke(RunDailyBackup, DispatcherPriority.ApplicationIdle);
        }
    }

    private void RunDailyBackup()
    {
        var backups = _services?.GetRequiredService<BackupService>();
        if (backups is null || _exiting)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                backups.EnsureDaily();
            }
            catch (BackupException ex)
            {
                // Already logged with details by BackupService; the next check retries.
                _log.LogWarning("Daily backup skipped: {Reason}", ex.Message);
            }
        });
    }

#if DEBUG
    /// <summary>
    /// Developer pages run standalone: no tray, no toasts, no single-instance check, no database, so they
    /// can be opened (and screenshotted) while the real app is running.
    /// </summary>
    private void ShowDevPage()
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        Window page = _options.ShowGallery ? new Dev.ComponentGalleryWindow() : new Dev.DesignTokensWindow();

        if (_options.ScreenshotPath is { } path)
        {
            // Capture final states, not frames in the middle of an entrance animation.
            FinanceOS.UI.Controls.Motion.Enabled = false;

            page.ContentRendered += async (_, _) =>
            {
                // Let deferred layout and bindings settle before rendering.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Dev.DevScreenshot.Capture(page, path, _options.ScreenshotScale);
                _log.LogInformation("screenshot scale={ScaleLevel}", _options.ScreenshotScale);

                if (page is Dev.ComponentGalleryWindow gallery)
                {
                    // Popups are separate windows: capture each one next to the page, e.g. gallery-light.select.png.
                    var stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
                    await foreach (var (name, content) in gallery.OpenOverlaysForCapture())
                    {
                        Dev.DevScreenshot.CaptureElement(content, page.Background, $"{stem}.{name}.png", _options.ScreenshotScale);
                    }
                }

                Shutdown();
            };
        }

        page.Show();
    }
#endif

    internal void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log.LogInformation("exit requested");
        HandRemindersToWindows();
        _backupTimer?.Stop();
        _window?.Close();
        _recovery?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_databaseReady)
        {
            _services?.GetRequiredService<DatabaseStartup>().MarkCleanShutdown();
        }

        _tray?.Dispose();
        _services?.Dispose();
        _systemEvents?.Dispose();
        _singleInstance?.Dispose();
        _log.LogInformation("exit code={ExitCode}", e.ApplicationExitCode);
        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (!_databaseReady)
        {
            // Tray click or toast while the recovery screen is open: bring that forward instead.
            _recovery?.Activate();
            return;
        }

        if (_window is null)
        {
            _window = CreateMainWindow();
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (!_exiting)
                {
                    _log.LogInformation("window closed -> tray");
                }
            };
            if (_startupMs is null)
            {
                _window.ContentRendered += OnFirstFrame;
            }

            _window.Show();
        }
        else
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
        }
    }

    private Window CreateMainWindow()
    {
#if DEBUG
        if (_options.ShowSpike)
        {
            return new Dev.SpikeWindow(_toasts is not null, _startupMs);
        }
#endif
        var services = _services ?? throw new InvalidOperationException("Services are built before the database starts.");
        var shell = services.GetRequiredService<ShellWindow>();
        var viewModel = services.GetRequiredService<ShellViewModel>();

        // First run: the short setup guide over the window.
        if (!_settings!.Current.Onboarded || _options.ShowOnboarding)
        {
            viewModel.StartOnboarding();
        }

        // Start-up switches for reviews and screenshots: --section=tasks, --panel=notifications.
        if (_options.Section is { } section)
        {
            viewModel.NavigateCommand.Execute(section);
        }

        if (_options.Panel == "notifications")
        {
            viewModel.ToggleNotificationsCommand.Execute(null);
        }
        else if (_options.Panel == "details")
        {
            viewModel.ToggleDetailsCommand.Execute(null);
            if (viewModel.CurrentPage is Tasks.TasksPageViewModel { Rows.Count: > 0 } tasks)
            {
                tasks.Select(tasks.Rows[0].Id);
            }
        }

        return shell;
    }

    private async void OnFirstFrame(object? sender, EventArgs e)
    {
        if (sender is Window w)
        {
            w.ContentRendered -= OnFirstFrame;
        }

        _startupMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        _log.LogInformation("first-frame startup_ms={StartupMs:F0}", _startupMs);
#if DEBUG
        (_window as Dev.SpikeWindow)?.SetStartupTime(_startupMs.Value);
#endif

        if (_options.Benchmark)
        {
            await RunBenchmarkAsync();
        }
    }

    private async Task RunBenchmarkAsync()
    {
        if (_options.BenchIdleSeconds is { } idle)
        {
            await Task.Delay(TimeSpan.FromSeconds(idle));
            WriteBenchResult("idle-window");
            _window?.Close();
            await Task.Delay(TimeSpan.FromSeconds(idle));
            WriteBenchResult("idle-tray");
        }
        else
        {
            // Let the first layout/render settle before sampling memory.
            await Task.Delay(TimeSpan.FromSeconds(1));
            WriteBenchResult("startup");
        }

        ExitApplication();
    }

    private void WriteBenchResult(string label)
    {
        using var process = Process.GetCurrentProcess();
        var result = new
        {
            label,
            startupMs = Math.Round(_startupMs ?? -1),
            workingSetMb = Math.Round(process.WorkingSet64 / 1048576.0, 1),
            privateMb = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1),
        };
        AppendResult(JsonSerializer.Serialize(result));
    }

    private void RunProbe()
    {
        var result = new
        {
            setting = ToastService.NotificationSetting,
            scheduled = ToastService.GetScheduled().Select(s => new { s.Tag, deliveryTime = s.DeliveryTime.ToString("O") }),
            delivered = ToastService.GetDeliveredTags(),
        };
        AppendResult(JsonSerializer.Serialize(result));
    }

    private void AppendResult(string json)
    {
        _log.LogInformation("result {ResultJson}", json);
        if (_options.OutputPath is { } path)
        {
            File.AppendAllText(path, json + Environment.NewLine);
        }
    }

    private void OnForwardedArguments(string[] args)
    {
        _log.LogInformation("forwarded args=[{Args}]", string.Join(' ', args));
        ShowMainWindow();
    }

    /// <summary>
    /// Sleep: Windows delivers the reminders while this process is frozen. Resume, clock change, unlock:
    /// reconcile (ARCHITECTURE §6.4) and take the delivery back.
    /// </summary>
    private void OnSystemEvent(string reason)
    {
        _log.LogInformation("system-event {Reason}", reason);
        if (!_databaseReady || _services is null || _options.Benchmark)
        {
            return;
        }

        var scheduler = _services.GetRequiredService<FinanceOS.Core.Reminders.ReminderScheduler>();
        if (reason == "suspend")
        {
            HandRemindersToWindows();
        }
        else
        {
            scheduler.Reconcile();
        }
    }

    /// <summary>Before exit or sleep: the next reminders become scheduled Windows toasts (level 3).</summary>
    private void HandRemindersToWindows()
    {
        if (!_databaseReady || _services is null || _options.Benchmark)
        {
            return;
        }

        try
        {
            var count = _services.GetRequiredService<FinanceOS.Core.Reminders.ReminderScheduler>().ScheduleAhead();
            _log.LogInformation("reminders scheduled in Windows count={Count}", count);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Toasts blocked by policy or not registered: the in-app center and reconciliation still cover it.
            _log.LogWarning(ex, "Could not schedule reminders in Windows");
        }
    }

    /// <summary>
    /// A click on a reminder toast (or one of its buttons), also when Windows started the app for it.
    /// Before the database is ready the activation waits; acting on an occurrence twice has no effect.
    /// </summary>
    private void OnToastActivated(ToastActivation activation)
    {
        _log.LogInformation("toast-activated action={Action} occ={OccurrenceId} snooze={SnoozeMinutes}",
            activation.Action, activation.OccurrenceId, activation.SnoozeMinutes);
        if (!_databaseReady || _services is null)
        {
            _pendingActivations.Add(activation);
            return;
        }

        var scheduler = _services.GetRequiredService<FinanceOS.Core.Reminders.ReminderScheduler>();
        var occurrence = activation.OccurrenceId;
        switch (activation.Action)
        {
            case "snooze" when occurrence is not null:
                FinanceOS.App.Reminders.ReminderText.Apply(scheduler, occurrence, activation.SnoozeMinutes ?? "15");
                break;
            case "done" when occurrence is not null:
                scheduler.Complete(occurrence);
                break;
            default:
                var task = occurrence is null ? null : scheduler.TaskOf(occurrence);
                if (occurrence is not null)
                {
                    scheduler.Opened(occurrence);
                }

                ShowMainWindow();
                if (task is not null)
                {
                    _services.GetRequiredService<ShellViewModel>().OpenTask(task);
                }

                break;
        }

#if DEBUG
        (_window as Dev.SpikeWindow)?.RefreshLog();
#endif
    }
}

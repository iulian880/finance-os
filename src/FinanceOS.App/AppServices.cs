using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.Core.Events;
using FinanceOS.Core.Reminders;
using FinanceOS.Data.Reminders;
using FinanceOS.App.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.Data.Tasks;
using FinanceOS.Data;
using FinanceOS.Data.Backup;
using FinanceOS.Data.Migrations;
using FinanceOS.Platform.Windows;
using FinanceOS.Platform.Windows.Windowing;
using FinanceOS.UI.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinanceOS.App;

/// <summary>Composition root (ARCHITECTURE §4.1): the only place that wires layers together.</summary>
internal static class AppServices
{
    public static ServiceProvider Build(ILoggerFactory loggerFactory, Localizer localizer, string? dataRoot = null, UserSettings? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings ?? new UserSettings(new PreferencesStore(System.IO.Path.Combine(dataRoot ?? AppPaths.DataRoot, "preferences.json"))));

        services.AddSingleton(loggerFactory);
        services.AddSingleton(localizer);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<WindowPlacementStore>(_ => new WindowPlacementStore());

        // Data (M5): one database per user under %LOCALAPPDATA%\FinanceOS, never next to the exe.
        services.AddSingleton(new DataLocation(dataRoot ?? AppPaths.DataRoot));
        services.AddSingleton(sp => new MigrationRunner(sp.GetRequiredService<ILogger<MigrationRunner>>()));
        services.AddSingleton(sp => new BackupService(
            sp.GetRequiredService<DataLocation>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BackupService>>()));
        services.AddSingleton(sp => new DatabaseStartup(
            sp.GetRequiredService<DataLocation>(), sp.GetRequiredService<MigrationRunner>(), sp.GetRequiredService<BackupService>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DatabaseStartup>>()));
        services.AddSingleton<IDataMaintenance, DataMaintenance>();

        // Tasks (M6): domain service over SQLite, events on an in-process bus. Pages use it from M7.
        services.AddSingleton(sp =>
        {
            var bus = new EventBus();
            var log = sp.GetRequiredService<ILogger<EventBus>>();
            bus.HandlerFailed += (domainEvent, error) => log.LogError(error, "Event handler failed for {EventType}", domainEvent.GetType().Name);
            return bus;
        });
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        services.AddSingleton<ITaskStore>(sp => new SqliteTaskStore(new SqliteDatabase(sp.GetRequiredService<DataLocation>().DatabasePath)));
        services.AddSingleton(sp => new TaskService(
            sp.GetRequiredService<ITaskStore>(), sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<TimeProvider>()));
        // Reminders (M9): planned from the tasks, delivered by an in-process scheduler as Windows toasts.
        services.AddSingleton<IReminderStore>(sp => new SqliteReminderStore(new SqliteDatabase(sp.GetRequiredService<DataLocation>().DatabasePath)));
        services.AddSingleton(sp => new ReminderService(
            sp.GetRequiredService<IReminderStore>(), sp.GetRequiredService<TaskService>(), sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new WindowsReminderSink(localizer, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<WindowsReminderSink>());
        // Only the real data folder owns the toasts Windows keeps for when the app is closed.
        var separateData = dataRoot is not null || !AppPaths.IsDefaultDataRoot;
        services.AddSingleton<IScheduledToasts>(sp => separateData ? new InertScheduledToasts() : sp.GetRequiredService<WindowsReminderSink>());
        services.AddSingleton(sp =>
        {
            var scheduler = new ReminderScheduler(
                sp.GetRequiredService<IReminderStore>(), sp.GetRequiredService<ReminderService>(), sp.GetRequiredService<TaskService>(),
                sp.GetRequiredService<INotificationSink>(), sp.GetRequiredService<IScheduledToasts>(), sp.GetRequiredService<TimeProvider>());
            var user = sp.GetRequiredService<UserSettings>();
            scheduler.Quiet = user.Quiet;
            user.Changed += (_, _) => scheduler.Quiet = user.Quiet;
            return scheduler;
        });
        services.AddSingleton(sp => new NotificationCenterViewModel(sp.GetRequiredService<ReminderScheduler>(), sp.GetRequiredService<TimeProvider>(), localizer));
        services.AddSingleton<ILauncher, ShellLauncher>();
        services.AddSingleton<InAppToasts>();
        services.AddSingleton<IInAppToasts>(sp => sp.GetRequiredService<InAppToasts>());

        // One shell state for the process: reopening the window from the tray keeps the current page.
        services.AddSingleton(sp => new PageServices(
            sp.GetRequiredService<IThemeService>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IDataMaintenance>(),
            sp.GetRequiredService<ILauncher>(), localizer, sp.GetRequiredService<TaskService>(), sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<IInAppToasts>(), sp.GetRequiredService<ReminderService>(),
            Environment.ProcessPath is { } exe ? new StartupRegistration(exe) : null, sp.GetRequiredService<UserSettings>()));
        services.AddSingleton<Func<Section, PageViewModel>>(sp =>
        {
            var pages = sp.GetRequiredService<PageServices>();
            return section => PageFactory.Create(section, pages);
        });
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<ShellWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

using System.IO;
using FinanceOS.App.Localization;
using FinanceOS.App.Services;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;

namespace FinanceOS.App.Tests;

/// <summary>Theme service without WPF resources: records what the ViewModels ask for.</summary>
internal sealed class FakeThemeService : IThemeService
{
    public event EventHandler? Changed;

    public bool IsDark => Preference == ThemePreference.Dark;

    public ThemePreference Preference { get; private set; } = ThemePreference.Light;

    public AccentOption Accent { get; private set; } = UI.Theming.Accents.Default;

    public IReadOnlyList<AccentOption> Accents => UI.Theming.Accents.All;

    public List<ThemePreference> PreferenceCalls { get; } = [];

    public List<AccentOption> AccentCalls { get; } = [];

    public void SetPreference(ThemePreference preference)
    {
        PreferenceCalls.Add(preference);
        Preference = preference;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetAccent(AccentOption accent)
    {
        AccentCalls.Add(accent);
        Accent = accent;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Simulates a change made elsewhere (e.g. Windows switching to dark), not through the ViewModel.</summary>
    public void ChangeExternally(ThemePreference preference, AccentOption accent)
    {
        Preference = preference;
        Accent = accent;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A fresh localizer per test (switching language in one test never leaks into another).</summary>
internal static class TestLoc
{
    public static Localizer Ro() => AppStrings.CreateLocalizer(AppLanguage.Romanian);

    public static Localizer En() => AppStrings.CreateLocalizer(AppLanguage.English);
}

/// <summary>Toasts recorded instead of shown; <see cref="RunAction"/> clicks the toast's button.</summary>
internal sealed class FakeToasts : IInAppToasts
{
    public List<FinanceOS.UI.Controls.ToastMessage> Shown { get; } = [];

    public FinanceOS.UI.Controls.ToastMessage Last => Shown[^1];

    public void Show(FinanceOS.UI.Controls.ToastMessage toast) => Shown.Add(toast);

    public void RunAction() => Last.Action!.Invoke();
}

/// <summary>
/// A real <see cref="FinanceOS.Core.Tasks.TaskService"/> over SQLite in a temporary folder (deleted
/// afterwards), with its event bus: ViewModel tests run against the same code the app uses.
/// </summary>
internal sealed class TaskKit : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FinanceOS.App.Tests", Guid.NewGuid().ToString("N"));

    public TaskKit(TimeProvider time)
    {
        var location = new FinanceOS.Data.DataLocation(_root);
        var backups = new FinanceOS.Data.Backup.BackupService(location, time);
        var startup = new FinanceOS.Data.DatabaseStartup(location, new FinanceOS.Data.Migrations.MigrationRunner(FinanceOS.Data.Migrations.MigrationCatalog.All), backups, time);
        if (startup.Run().Status != FinanceOS.Data.DatabaseStatus.Ready)
        {
            throw new InvalidOperationException("Test database did not start.");
        }

        var database = new FinanceOS.Data.SqliteDatabase(location.DatabasePath);
        Tasks = new FinanceOS.Core.Tasks.TaskService(new FinanceOS.Data.Tasks.SqliteTaskStore(database), Bus, time);
        var reminders = new FinanceOS.Data.Reminders.SqliteReminderStore(database);
        Reminders = new FinanceOS.Core.Reminders.ReminderService(reminders, Tasks, Bus, time);
        Scheduler = new FinanceOS.Core.Reminders.ReminderScheduler(reminders, Reminders, Tasks, Sink, Sink, time);
    }

    public FinanceOS.Core.Reminders.ReminderService Reminders { get; }

    public FinanceOS.Core.Reminders.ReminderScheduler Scheduler { get; }

    public RecordingSink Sink { get; } = new();

    public FinanceOS.Core.Events.EventBus Bus { get; } = new();

    public FinanceOS.Core.Tasks.TaskService Tasks { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup.
        }
    }
}

/// <summary>Reminder toasts recorded instead of shown.</summary>
internal sealed class RecordingSink : FinanceOS.Core.Reminders.INotificationSink, FinanceOS.Core.Reminders.IScheduledToasts
{
    public void Schedule(FinanceOS.Core.Reminders.ReminderAlert alert, DateTimeOffset atUtc)
    {
    }

    public IReadOnlySet<string> DeliveredTags() => new HashSet<string>();

    public void CancelAll()
    {
    }

    public List<FinanceOS.Core.Reminders.ReminderAlert> Shown { get; } = [];

    public void Show(FinanceOS.Core.Reminders.ReminderAlert alert) => Shown.Add(alert);

    public void Withdraw(string occurrenceId)
    {
    }
}

/// <summary>Clock frozen at a given local time.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("Test", now.Offset, "Test", "Test");
}

/// <summary>Backups in memory; <see cref="Fail"/> makes the next backup throw like a full disk would.</summary>
internal sealed class FakeDataMaintenance : IDataMaintenance
{
    public string DataFolder => @"C:\Data\FinanceOS";

    public DateTimeOffset? LastBackupUtc { get; set; }

    public bool Fail { get; set; }

    public int OpenedFolder { get; private set; }

    public Task<FinanceOS.Data.Backup.BackupInfo> BackupNowAsync()
    {
        if (Fail)
        {
            return Task.FromException<FinanceOS.Data.Backup.BackupInfo>(new FinanceOS.Data.Backup.BackupException("disk full"));
        }

        var at = new DateTimeOffset(2026, 11, 5, 8, 30, 0, TimeSpan.Zero);
        LastBackupUtc = at;
        return Task.FromResult(new FinanceOS.Data.Backup.BackupInfo(@"C:\b\x.db", FinanceOS.Data.Backup.BackupKind.Manual, at, 1024));
    }

    public void OpenDataFolder() => OpenedFolder++;

    public event EventHandler? BackupsChanged;

    /// <summary>Simulates the automatic daily backup finishing.</summary>
    public void CompleteAutomaticBackup(DateTimeOffset at)
    {
        LastBackupUtc = at;
        BackupsChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeLauncher : ILauncher
{
    public List<Uri> Opened { get; } = [];

    public void Open(Uri uri) => Opened.Add(uri);
}

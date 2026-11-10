using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Shell;
using FinanceOS.Data;
using FinanceOS.Data.Backup;
using FinanceOS.Data.Migrations;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Recovery;

/// <summary>A backup offered for restore, already inspected (healthy and not newer than the app).</summary>
public sealed record BackupChoice(BackupInfo Backup, bool IsUsable, string Problem = "");

/// <summary>What the recovery screen can do. Every action ends by running database start-up again.</summary>
public interface IRecoveryActions
{
    IReadOnlyList<BackupChoice> ListBackups();

    DatabaseStartupResult Retry();

    /// <summary>Restores <paramref name="backup"/> (current state kept first), then starts the database.</summary>
    DatabaseStartupResult Restore(BackupInfo backup);

    /// <summary>Keeps the damaged file as a backup, creates a new empty database, starts it.</summary>
    DatabaseStartupResult StartFresh();

    void OpenDataFolder();
}

/// <summary>Backup row: "5 noiembrie 2026, 10:30" + "Zilnic · 84 KB".</summary>
public sealed record BackupRow(BackupChoice Choice, string When, string Detail)
{
    public bool IsUsable => Choice.IsUsable;
}

/// <summary>
/// Shown instead of the main window when the database cannot be used (ARCHITECTURE §7: "ecran de
/// recuperare"). Explains what happened in plain words, offers only actions that are safe for that case,
/// and never deletes data: restore and "start fresh" both keep a copy of what is there now.
/// </summary>
public sealed partial class RecoveryViewModel : ObservableObject
{
    private readonly IRecoveryActions _actions;
    private readonly TimeProvider _time;
    private readonly Localizer _l;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Message), nameof(CanRestore), nameof(CanStartFresh), nameof(CanRetry), nameof(Details))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private DatabaseStartupResult _result;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private BackupRow? _selectedBackup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(RetryCommand), nameof(ConfirmStartFreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isConfirmingStartFresh;

    /// <summary>Why the last action did not work, in plain words. Null when nothing went wrong.</summary>
    [ObservableProperty]
    private string? _actionError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsableBackup), nameof(HasNoBackups))]
    private IReadOnlyList<BackupRow> _backups = [];

    /// <param name="localizer">Texts in the saved language. This screen has no language switch: it is
    /// shown before Settings exist, so the language is fixed for its lifetime.</param>
    public RecoveryViewModel(DatabaseStartupResult result, IRecoveryActions actions, TimeProvider time, Localizer localizer)
    {
        _result = result;
        _actions = actions;
        _time = time;
        _l = localizer;
        LoadBackups();
    }

    /// <summary>Raised once the database is ready: the app continues to the main window.</summary>
    public event EventHandler<DatabaseStartupResult>? Recovered;

    public event EventHandler? ExitRequested;

    public string Title => _l[Result.Status switch
    {
        DatabaseStatus.Corrupt => "Recovery.Title.Corrupt",
        DatabaseStatus.TooNew => "Recovery.Title.TooNew",
        _ => "Recovery.Title.Failed",
    }];

    public string Message => Result.Status switch
    {
        DatabaseStatus.Corrupt => _l["Recovery.Message.Corrupt"],
        DatabaseStatus.TooNew => _l.Format("Recovery.Message.TooNew", Result.DatabaseVersion, Result.SupportedVersion),
        _ => _l["Recovery.Message.Failed"],
    };

    /// <summary>Technical detail, for IT or a bug report.</summary>
    public string Details => Result.Problem ?? "";

    /// <summary>Restoring an older backup over newer data would lose work: not offered for TooNew.</summary>
    public bool CanRestore => Result.Status is DatabaseStatus.Corrupt or DatabaseStatus.Failed;

    public bool CanStartFresh => Result.Status == DatabaseStatus.Corrupt;

    public bool CanRetry => Result.Status == DatabaseStatus.Failed;

    public bool HasUsableBackup => Backups.Any(b => b.IsUsable);

    public bool HasNoBackups => Backups.Count == 0;

    [RelayCommand(CanExecute = nameof(CanRestoreSelected))]
    private Task RestoreAsync() => RunAsync(() => _actions.Restore(SelectedBackup!.Choice.Backup), "Recovery.Error.Restore");

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RetryAsync() => RunAsync(_actions.Retry, "Recovery.Error.Retry");

    [RelayCommand]
    private void AskStartFresh() => IsConfirmingStartFresh = true;

    [RelayCommand]
    private void CancelStartFresh() => IsConfirmingStartFresh = false;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ConfirmStartFreshAsync() => RunAsync(_actions.StartFresh, "Recovery.Error.StartFresh");

    [RelayCommand]
    private void OpenDataFolder() => _actions.OpenDataFolder();

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private bool CanRestoreSelected() => !IsBusy && CanRestore && SelectedBackup is { IsUsable: true };

    private bool IsIdle() => !IsBusy;

    /// <param name="failureKey">String key of the message shown when the action throws.</param>
    private async Task RunAsync(Func<DatabaseStartupResult> action, string failureKey)
    {
        IsBusy = true;
        ActionError = null;
        try
        {
            var result = await Task.Run(action);
            if (result.Status == DatabaseStatus.Ready)
            {
                Recovered?.Invoke(this, result);
                return;
            }

            // Still not usable: show the new situation (e.g. the restored file is fine but now locked).
            Result = result;
            IsConfirmingStartFresh = false;
            LoadBackups();
        }
        catch (Exception ex) when (ex is BackupException or DatabaseTooNewException or IOException or UnauthorizedAccessException)
        {
            ActionError = _l[failureKey];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadBackups()
    {
        Backups = _actions.ListBackups()
            .Select(c => new BackupRow(c, When(c.Backup), Detail(c)))
            .ToList();
        SelectedBackup = Backups.FirstOrDefault(b => b.IsUsable);
    }

    private string When(BackupInfo backup) =>
        DisplayFormat.DateAndTime(TimeZoneInfo.ConvertTime(backup.CreatedUtc, _time.LocalTimeZone).DateTime, _l.Culture);

    private string Detail(BackupChoice choice)
    {
        var kind = _l[choice.Backup.Kind switch
        {
            BackupKind.Daily => "Backup.Kind.Daily",
            BackupKind.PreMigration => "Backup.Kind.PreMigration",
            BackupKind.PreRestore => "Backup.Kind.PreRestore",
            BackupKind.PreImport => "Backup.Kind.PreImport",
            BackupKind.Manual => "Backup.Kind.Manual",
            _ => "Backup.Kind.Other",
        }];
        var size = choice.Backup.SizeBytes >= 1024 * 1024
            ? string.Create(_l.Culture, $"{choice.Backup.SizeBytes / 1048576.0:0.0} MB")
            : $"{Math.Max(1, choice.Backup.SizeBytes / 1024)} KB";
        return $"{kind} · {(choice.IsUsable ? size : _l["Recovery.Backup.Unusable"])}";
    }
}

/// <summary><see cref="IRecoveryActions"/> over the real start-up and backup services.</summary>
public sealed class RecoveryActions(DatabaseStartup startup, BackupService backups) : IRecoveryActions
{
    public IReadOnlyList<BackupChoice> ListBackups() => backups.List()
        .Where(b => b.Kind != BackupKind.Damaged)
        .Select(b =>
        {
            var health = DatabaseInspector.Inspect(b.FilePath);
            var usable = health.IsHealthy && health.SchemaVersion <= startup.SupportedVersion;
            return new BackupChoice(b, usable, health.Problem ?? (usable ? "" : "newer schema"));
        })
        .ToList();

    public DatabaseStartupResult Retry() => startup.Run();

    public DatabaseStartupResult Restore(BackupInfo backup)
    {
        backups.Restore(backup, startup.SupportedVersion);
        return startup.Run();
    }

    public DatabaseStartupResult StartFresh()
    {
        backups.QuarantineCurrent();
        return startup.Run();
    }

    public void OpenDataFolder() => Services.DataMaintenance.OpenInExplorer(startup.Location.Root);
}

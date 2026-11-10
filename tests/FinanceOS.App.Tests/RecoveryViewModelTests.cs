using System.IO;
using FinanceOS.App.Recovery;
using FinanceOS.Data;
using FinanceOS.Data.Backup;
using Xunit;

namespace FinanceOS.App.Tests;

public sealed class RecoveryViewModelTests
{
    private static readonly FixedTimeProvider Bucharest = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(2)));
    private static readonly DateTimeOffset Morning = new(2026, 11, 5, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Corrupt_database_offers_restore_and_start_fresh_with_the_newest_good_backup_selected()
    {
        var actions = new FakeRecoveryActions(
            Choice(BackupKind.Daily, Morning, usable: false),
            Choice(BackupKind.Daily, Morning.AddDays(-1)),
            Choice(BackupKind.Manual, Morning.AddDays(-2)));

        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt, "database disk image is malformed"), actions, Bucharest, TestLoc.Ro());

        Assert.True(vm.CanRestore);
        Assert.True(vm.CanStartFresh);
        Assert.False(vm.CanRetry);
        Assert.Equal("Baza de date nu poate fi deschisă în siguranță", vm.Title);
        Assert.Equal("database disk image is malformed", vm.Details);
        Assert.Same(vm.Backups[1], vm.SelectedBackup); // the damaged newest one is skipped
        Assert.Equal("4 noiembrie 2026, 08:00", vm.SelectedBackup!.When); // 06:00 UTC = 08:00 local
        Assert.Equal("Zilnic · 84 KB", vm.SelectedBackup.Detail);
        Assert.Equal("Zilnic · nu poate fi folosit", vm.Backups[0].Detail);
        Assert.True(vm.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public void Unusable_backup_cannot_be_restored()
    {
        var actions = new FakeRecoveryActions(Choice(BackupKind.Daily, Morning, usable: false));
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt), actions, Bucharest, TestLoc.Ro());

        Assert.Null(vm.SelectedBackup);
        vm.SelectedBackup = vm.Backups[0];

        Assert.False(vm.RestoreCommand.CanExecute(null));
        Assert.False(vm.HasUsableBackup);
    }

    [Fact]
    public void Newer_database_offers_no_restore_and_explains_the_versions()
    {
        var actions = new FakeRecoveryActions(Choice(BackupKind.Daily, Morning));
        var vm = new RecoveryViewModel(
            new DatabaseStartupResult(DatabaseStatus.TooNew) { DatabaseVersion = 3, SupportedVersion = 1 }, actions, Bucharest, TestLoc.Ro());

        Assert.False(vm.CanRestore);
        Assert.False(vm.CanStartFresh);
        Assert.False(vm.CanRetry);
        Assert.False(vm.RestoreCommand.CanExecute(null));
        Assert.Contains("schema 3", vm.Message);
        Assert.Contains("schema 1", vm.Message);
    }

    [Fact]
    public void Locked_database_offers_retry()
    {
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Failed, "database is locked"), new FakeRecoveryActions(), Bucharest, TestLoc.Ro());

        Assert.True(vm.CanRetry);
        Assert.False(vm.CanStartFresh);
        Assert.True(vm.HasNoBackups);
    }

    [Fact]
    public async Task Successful_restore_raises_recovered()
    {
        var actions = new FakeRecoveryActions(Choice(BackupKind.Daily, Morning)) { Next = Ready() };
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt), actions, Bucharest, TestLoc.Ro());
        DatabaseStartupResult? recovered = null;
        vm.Recovered += (_, r) => recovered = r;

        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal(DatabaseStatus.Ready, recovered?.Status);
        Assert.Same(vm.Backups[0].Choice.Backup, actions.Restored);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Failed_restore_shows_an_error_and_stays()
    {
        var actions = new FakeRecoveryActions(Choice(BackupKind.Daily, Morning)) { Throw = new BackupException("bad file") };
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt), actions, Bucharest, TestLoc.Ro());
        var recovered = false;
        vm.Recovered += (_, _) => recovered = true;

        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.False(recovered);
        Assert.StartsWith("Restaurarea nu a reușit", vm.ActionError);
        Assert.True(vm.RestoreCommand.CanExecute(null)); // can try another backup
    }

    [Fact]
    public async Task Start_fresh_needs_confirmation_and_then_recovers()
    {
        var actions = new FakeRecoveryActions { Next = Ready() };
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt), actions, Bucharest, TestLoc.Ro());
        var recovered = false;
        vm.Recovered += (_, _) => recovered = true;

        vm.AskStartFreshCommand.Execute(null);
        Assert.True(vm.IsConfirmingStartFresh);
        vm.CancelStartFreshCommand.Execute(null);
        Assert.False(vm.IsConfirmingStartFresh);
        Assert.Equal(0, actions.StartedFresh);

        vm.AskStartFreshCommand.Execute(null);
        await vm.ConfirmStartFreshCommand.ExecuteAsync(null);

        Assert.Equal(1, actions.StartedFresh);
        Assert.True(recovered);
    }

    [Fact]
    public async Task Retry_that_still_fails_updates_the_screen()
    {
        var actions = new FakeRecoveryActions { Next = new DatabaseStartupResult(DatabaseStatus.Corrupt, "now corrupt") };
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Failed, "locked"), actions, Bucharest, TestLoc.Ro());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(DatabaseStatus.Corrupt, vm.Result.Status);
        Assert.Equal("now corrupt", vm.Details);
        Assert.True(vm.CanStartFresh);
        Assert.Contains(nameof(RecoveryViewModel.Title), changed);
        Assert.Contains(nameof(RecoveryViewModel.CanStartFresh), changed);
    }

    [Fact]
    public void Exit_and_open_folder_are_forwarded()
    {
        var actions = new FakeRecoveryActions();
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Failed), actions, Bucharest, TestLoc.Ro());
        var exited = false;
        vm.ExitRequested += (_, _) => exited = true;

        vm.OpenDataFolderCommand.Execute(null);
        vm.ExitCommand.Execute(null);

        Assert.Equal(1, actions.OpenedFolder);
        Assert.True(exited);
    }

    [Fact]
    public void Recovery_screen_speaks_the_saved_language()
    {
        var big = new BackupChoice(new BackupInfo(@"C:\b\x.db", BackupKind.PreMigration, Morning.AddDays(-1), 2 * 1048576 + 104858), true);
        var english = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt),
            new FakeRecoveryActions(big, Choice(BackupKind.Daily, Morning.AddDays(-2), usable: false)), Bucharest, TestLoc.En());
        var romanian = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt), new FakeRecoveryActions(big), Bucharest, TestLoc.Ro());

        Assert.Equal("The database cannot be opened safely", english.Title);
        Assert.Equal("4 November 2026, 08:00", english.SelectedBackup!.When);
        Assert.Equal("Before an update · 2.1 MB", english.SelectedBackup.Detail);
        Assert.Equal("Daily · cannot be used", english.Backups[1].Detail);
        Assert.Equal("Înainte de actualizare · 2,1 MB", romanian.SelectedBackup!.Detail);

        var tooNew = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.TooNew) { DatabaseVersion = 3, SupportedVersion = 1 },
            new FakeRecoveryActions(), Bucharest, TestLoc.En());
        Assert.Contains("(schema 3)", tooNew.Message);
        Assert.Contains("up to schema 1.", tooNew.Message);
    }

    [Fact]
    public async Task Action_errors_are_shown_in_the_saved_language()
    {
        var actions = new FakeRecoveryActions(Choice(BackupKind.Daily, Morning)) { Throw = new BackupException("bad file") };
        var vm = new RecoveryViewModel(new DatabaseStartupResult(DatabaseStatus.Corrupt), actions, Bucharest, TestLoc.En());

        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.StartsWith("The restore did not work", vm.ActionError);
    }

    private static DatabaseStartupResult Ready() => new(DatabaseStatus.Ready);

    private static BackupChoice Choice(BackupKind kind, DateTimeOffset at, bool usable = true) =>
        new(new BackupInfo($@"C:\b\{BackupInfo.FileNameFor(kind, at)}", kind, at, 86_016), usable);

    private sealed class FakeRecoveryActions(params BackupChoice[] backups) : IRecoveryActions
    {
        public DatabaseStartupResult Next { get; init; } = new(DatabaseStatus.Failed);

        public Exception? Throw { get; init; }

        public BackupInfo? Restored { get; private set; }

        public int StartedFresh { get; private set; }

        public int OpenedFolder { get; private set; }

        public IReadOnlyList<BackupChoice> ListBackups() => backups;

        public DatabaseStartupResult Retry() => Result();

        public DatabaseStartupResult Restore(BackupInfo backup)
        {
            Restored = backup;
            return Result();
        }

        public DatabaseStartupResult StartFresh()
        {
            StartedFresh++;
            return Result();
        }

        public void OpenDataFolder() => OpenedFolder++;

        private DatabaseStartupResult Result() => Throw is null ? Next : throw Throw;
    }
}

/// <summary>End to end over real files: RecoveryActions + DatabaseStartup + BackupService.</summary>
public sealed class RecoveryActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_file_is_recovered_from_its_daily_backup()
    {
        var location = new DataLocation(_root);
        var backups = new BackupService(location, TimeProvider.System);
        var startup = new DatabaseStartup(location, new Data.Migrations.MigrationRunner(), backups, TimeProvider.System);
        Assert.Equal(DatabaseStatus.Ready, startup.Run().Status);
        backups.Create(BackupKind.Daily);
        startup.MarkCleanShutdown();
        using (var file = new FileStream(location.DatabasePath, FileMode.Open, FileAccess.Write))
        {
            file.Write(new byte[100]);
        }

        var result = startup.Run();
        Assert.Equal(DatabaseStatus.Corrupt, result.Status);

        var actions = new RecoveryActions(startup, backups);
        var choice = Assert.Single(actions.ListBackups());
        Assert.True(choice.IsUsable);
        Assert.Equal(DatabaseStatus.Ready, actions.Restore(choice.Backup).Status);
    }
}

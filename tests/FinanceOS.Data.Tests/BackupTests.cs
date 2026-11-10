using Dapper;
using FinanceOS.Data.Backup;
using FinanceOS.Data.Migrations;
using Xunit;

namespace FinanceOS.Data.Tests;

public sealed class BackupTests
{
    [Fact]
    public void Backup_is_a_verified_single_file_snapshot()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.InsertTask("t1", "Reconciliere bancară");

        var backup = db.Backups.Create(BackupKind.Manual);

        Assert.True(File.Exists(backup.FilePath));
        Assert.False(File.Exists(backup.FilePath + "-wal"));
        Assert.False(File.Exists(backup.FilePath + ".tmp"));
        Assert.Equal(BackupKind.Manual, backup.Kind);
        Assert.Equal(db.Clock.GetUtcNow(), backup.CreatedUtc);
        var health = DatabaseInspector.Inspect(backup.FilePath);
        Assert.True(health.IsHealthy, health.Problem);
        Assert.Equal(MigrationCatalog.LatestVersion, health.SchemaVersion);

        using var copy = SqliteDatabase.OpenExisting(backup.FilePath, wal: false);
        Assert.Equal("delete", copy.ExecuteScalar<string>("PRAGMA journal_mode;"));
        Assert.Equal("Reconciliere bancară", copy.ExecuteScalar<string>("SELECT title FROM tasks;"));
    }

    [Fact]
    public void Backup_includes_commits_still_in_the_wal()
    {
        using var db = new TestDatabase();
        db.StartReady();
        using var open = db.Open(); // keeps the WAL from being checkpointed
        open.Execute("INSERT INTO tasks (id, title, status_id) VALUES ('t1', 'Doar în WAL', 'status-todo');");
        Assert.True(new FileInfo(db.Location.DatabasePath + "-wal").Length > 0);

        var backup = db.Backups.Create(BackupKind.Manual);

        using var copy = SqliteDatabase.OpenExisting(backup.FilePath, wal: false);
        Assert.Equal("Doar în WAL", copy.ExecuteScalar<string>("SELECT title FROM tasks;"));
    }

    [Fact]
    public void Two_backups_in_the_same_millisecond_get_different_files()
    {
        using var db = new TestDatabase();
        db.StartReady();

        var first = db.Backups.Create(BackupKind.Manual);
        var second = db.Backups.Create(BackupKind.Manual);

        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.Equal(2, db.Backups.List().Count);
    }

    [Fact]
    public void Daily_backup_is_made_once_per_local_day()
    {
        using var db = new TestDatabase(); // clock: 08:30 UTC = 10:30 local
        db.StartReady();

        Assert.NotNull(db.Backups.EnsureDaily());
        Assert.Null(db.Backups.EnsureDaily());

        db.Clock.Advance(TimeSpan.FromHours(13)); // 23:30 local, same day
        Assert.Null(db.Backups.EnsureDaily());

        db.Clock.Advance(TimeSpan.FromHours(1)); // 00:30 local next day (still 22:30 UTC the same day)
        Assert.NotNull(db.Backups.EnsureDaily());
        Assert.Equal(2, db.Backups.List().Count(b => b.Kind == BackupKind.Daily));
    }

    [Fact]
    public void Backup_without_a_database_fails_cleanly()
    {
        using var db = new TestDatabase();
        Assert.Throws<BackupException>(() => db.Backups.Create(BackupKind.Manual));
        Assert.Empty(Directory.EnumerateFiles(db.Location.BackupsDirectory));
    }

    [Fact]
    public void Listing_ignores_unrelated_files()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.Backups.Create(BackupKind.Manual);
        File.WriteAllText(Path.Combine(db.Location.BackupsDirectory, "notes.txt"), "");
        File.WriteAllText(Path.Combine(db.Location.BackupsDirectory, "financeos-garbage.db"), "");

        Assert.Single(db.Backups.List());
        db.Backups.Rotate();
        Assert.True(File.Exists(Path.Combine(db.Location.BackupsDirectory, "financeos-garbage.db")));
    }

    [Fact]
    public void Restore_brings_back_the_backup_and_keeps_the_replaced_state()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.InsertTask("t1", "Înainte");
        var backup = db.Backups.Create(BackupKind.Manual);
        db.InsertTask("t2", "După backup");
        db.Startup.MarkCleanShutdown();
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        db.Backups.Restore(backup, MigrationCatalog.LatestVersion);

        Assert.Equal(DatabaseStatus.Ready, db.Startup.Run().Status);
        Assert.Equal(["Înainte"], db.TaskTitles());
        var kept = Assert.Single(db.Backups.List(), b => b.Kind == BackupKind.PreRestore);
        using var copy = SqliteDatabase.OpenExisting(kept.FilePath, wal: false);
        Assert.Equal(2, copy.ExecuteScalar<int>("SELECT count(*) FROM tasks;"));
    }

    [Fact]
    public void Restore_does_not_replay_the_old_wal_onto_the_restored_file()
    {
        using var db = new TestDatabase();
        db.StartReady();
        var backup = db.Backups.Create(BackupKind.Manual);

        // Simulate a crash: commits sit in the WAL and the process dies without a checkpoint.
        File.WriteAllBytes(db.Location.DatabasePath + "-wal", [1, 2, 3, 4]);
        db.Backups.Restore(backup, MigrationCatalog.LatestVersion);

        Assert.False(File.Exists(db.Location.DatabasePath + "-wal"));
        Assert.Equal(DatabaseStatus.Ready, db.Startup.Run().Status);
    }

    [Fact]
    public void Restore_refuses_a_damaged_backup_and_changes_nothing()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.InsertTask("t1", "Rămâne");
        var backup = db.Backups.Create(BackupKind.Manual);
        Corruption.DestroyHeader(backup.FilePath);

        Assert.Throws<BackupException>(() => db.Backups.Restore(backup, MigrationCatalog.LatestVersion));
        Assert.Equal(["Rămâne"], db.TaskTitles());
        Assert.DoesNotContain(db.Backups.List(), b => b.Kind == BackupKind.PreRestore);
    }

    [Fact]
    public void Restore_refuses_a_backup_from_a_newer_version()
    {
        var future = MigrationCatalog.All
            .Append(new Migration(MigrationCatalog.LatestVersion + 1, "future", "CREATE TABLE future_feature (id TEXT);"))
            .ToList();
        using var db = new TestDatabase(future);
        db.StartReady();
        var backup = db.Backups.Create(BackupKind.Manual);

        Assert.Throws<DatabaseTooNewException>(() => db.Backups.Restore(backup, MigrationCatalog.LatestVersion));
    }

    [Fact]
    public void Older_backup_is_migrated_after_restore()
    {
        var v1 = MigrationCatalog.All;
        var v2 = v1.Append(new Migration(v1.Count + 1, "add_test_column", "ALTER TABLE tasks ADD COLUMN test_col TEXT;")).ToList();
        using var db = new TestDatabase(v1);
        db.StartReady();
        db.InsertTask("t1", "Din backup vechi");
        var oldBackup = db.Backups.Create(BackupKind.Manual);
        db.Startup.MarkCleanShutdown();
        db.StartupWith(v2);
        db.StartReady();
        db.Startup.MarkCleanShutdown();

        db.Backups.Restore(oldBackup, v2.Count);
        var result = db.StartReady();

        Assert.Equal([v2[^1]], result.Applied);
        Assert.Equal(["Din backup vechi"], db.TaskTitles());
    }
}

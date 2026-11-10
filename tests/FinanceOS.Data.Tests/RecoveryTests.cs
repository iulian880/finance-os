using Dapper;
using FinanceOS.Data.Backup;
using FinanceOS.Data.Migrations;
using Xunit;

namespace FinanceOS.Data.Tests;

/// <summary>Ways to damage a database file the way disks, crashes and copy tools do.</summary>
internal static class Corruption
{
    /// <summary>Overwrites the 100-byte header: SQLite no longer recognises the file ("not a database").</summary>
    public static void DestroyHeader(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        file.Write(new byte[100]);
    }

    /// <summary>Overwrites the middle of the file (b-tree pages) with garbage; the header stays valid.</summary>
    public static void ScrambleMiddle(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var pageSize = 4096;
        Xunit.Assert.True(file.Length > pageSize * 3, "database too small to corrupt its middle");
        file.Position = pageSize * 2;
        var garbage = new byte[pageSize * ((file.Length / pageSize) - 2)];
        new Random(42).NextBytes(garbage);
        file.Write(garbage);
    }
}

public sealed class RecoveryTests
{
    [Fact]
    public void File_that_is_not_a_database_is_reported_as_corrupt()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.Startup.MarkCleanShutdown();
        Corruption.DestroyHeader(db.Location.DatabasePath);

        var result = db.Startup.Run();

        Assert.Equal(DatabaseStatus.Corrupt, result.Status);
        Assert.False(File.Exists(db.Location.SessionMarkerPath));
    }

    [Fact]
    public void Damaged_pages_are_reported_as_corrupt()
    {
        using var db = new TestDatabase();
        db.StartReady();
        FillWithTasks(db, 400);
        db.Startup.MarkCleanShutdown();
        Corruption.ScrambleMiddle(db.Location.DatabasePath);

        Assert.Equal(DatabaseStatus.Corrupt, db.Startup.Run().Status);
    }

    [Fact]
    public void Corrupt_database_is_recovered_from_the_last_good_backup()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.InsertTask("t1", "Salvat în backup");
        var backup = db.Backups.Create(BackupKind.Daily);
        db.Startup.MarkCleanShutdown();
        Corruption.DestroyHeader(db.Location.DatabasePath);
        Assert.Equal(DatabaseStatus.Corrupt, db.Startup.Run().Status);

        db.Backups.Restore(backup, MigrationCatalog.LatestVersion);

        Assert.Equal(DatabaseStatus.Ready, db.Startup.Run().Status);
        Assert.Equal(["Salvat în backup"], db.TaskTitles());
        // The damaged file could not be snapshotted, so its raw bytes were kept instead.
        Assert.Single(db.Backups.List(), b => b.Kind == BackupKind.Damaged);
    }

    [Fact]
    public void Start_fresh_keeps_the_damaged_file_and_creates_a_new_database()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.Startup.MarkCleanShutdown();
        Corruption.DestroyHeader(db.Location.DatabasePath);
        var damagedBytes = File.ReadAllBytes(db.Location.DatabasePath);

        var copy = db.Backups.QuarantineCurrent();
        var result = db.Startup.Run();

        Assert.Equal(DatabaseStatus.Ready, result.Status);
        Assert.True(result.CreatedNew);
        Assert.NotNull(copy);
        Assert.Equal(damagedBytes, File.ReadAllBytes(copy.FilePath));
        Assert.Empty(db.TaskTitles());
    }

    [Fact]
    public void Unclean_shutdown_triggers_a_full_integrity_check()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.Startup.MarkCleanShutdown();

        db.StartReady(); // no MarkCleanShutdown: the session "crashes"
        var afterCrash = db.StartReady();

        Assert.True(afterCrash.RecoveredFromCrash);
        Assert.True(afterCrash.RanFullCheck);
    }

    [Fact]
    public void Clean_session_runs_only_the_quick_check_until_a_week_has_passed()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.Startup.MarkCleanShutdown();

        var nextDay = Restart(db, TimeSpan.FromDays(1));
        Assert.False(nextDay.RanFullCheck);
        Assert.False(nextDay.RecoveredFromCrash);

        var weekLater = Restart(db, TimeSpan.FromDays(7));
        Assert.True(weekLater.RanFullCheck);

        var dayAfter = Restart(db, TimeSpan.FromDays(1));
        Assert.False(dayAfter.RanFullCheck);
    }

    [Fact]
    public void Full_check_catches_foreign_key_violations()
    {
        using var db = new TestDatabase();
        db.StartReady();
        using (var connection = db.Open())
        {
            connection.Execute("PRAGMA foreign_keys = OFF;");
            connection.Execute("INSERT INTO tasks (id, title, status_id) VALUES ('t1', 'Orfan', 'status-missing');");
        }

        var result = db.Startup.Run(); // previous session never closed cleanly → full check

        Assert.Equal(DatabaseStatus.Corrupt, result.Status);
        Assert.Contains("foreign key", result.Problem);
    }

    [Fact]
    public void Locked_database_is_reported_as_failed_not_corrupt()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.Startup.MarkCleanShutdown();

        // Another process holding an exclusive lock for longer than the busy timeout.
        using var holder = db.Open();
        holder.Execute("PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE;");

        var result = db.Startup.Run();

        Assert.Equal(DatabaseStatus.Failed, result.Status);
        holder.Execute("ROLLBACK;");
    }

    private static DatabaseStartupResult Restart(TestDatabase db, TimeSpan after)
    {
        db.Clock.Advance(after);
        var result = db.StartReady();
        db.Startup.MarkCleanShutdown();
        return result;
    }

    private static void FillWithTasks(TestDatabase db, int count)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            connection.Execute(
                "INSERT INTO tasks (id, title, description, status_id) VALUES (@id, @title, @description, 'status-todo');",
                new { id = $"t{i}", title = $"Task {i}", description = new string('x', 300) },
                transaction);
        }

        transaction.Commit();
    }
}

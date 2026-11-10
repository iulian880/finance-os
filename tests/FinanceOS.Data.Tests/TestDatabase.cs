using Dapper;
using FinanceOS.Data.Backup;
using FinanceOS.Data.Migrations;
using Microsoft.Data.Sqlite;

namespace FinanceOS.Data.Tests;

/// <summary>A data root in a fresh temporary folder, deleted afterwards. One per test.</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase(IReadOnlyList<Migration>? migrations = null)
    {
        Location = new DataLocation(Path.Combine(Path.GetTempPath(), "FinanceOS.Data.Tests", Guid.NewGuid().ToString("N")));
        Clock = new ManualClock(new DateTimeOffset(2026, 11, 5, 8, 30, 0, TimeSpan.Zero));
        Migrations = new MigrationRunner(migrations ?? MigrationCatalog.All);
        Backups = new BackupService(Location, Clock);
        Startup = new DatabaseStartup(Location, Migrations, Backups, Clock);
    }

    public DataLocation Location { get; }

    public ManualClock Clock { get; }

    public MigrationRunner Migrations { get; }

    public BackupService Backups { get; }

    public DatabaseStartup Startup { get; private set; }

    /// <summary>Same files, different build of the app (other migration list).</summary>
    public DatabaseStartup StartupWith(IReadOnlyList<Migration> migrations) =>
        Startup = new DatabaseStartup(Location, new MigrationRunner(migrations), Backups, Clock);

    public SqliteConnection Open() => new SqliteDatabase(Location.DatabasePath).Open();

    /// <summary>Starts, asserts Ready, and closes cleanly (like a normal app session).</summary>
    public DatabaseStartupResult StartReady()
    {
        var result = Startup.Run();
        Xunit.Assert.True(result.Status == DatabaseStatus.Ready, $"{result.Status}: {result.Problem}");
        return result;
    }

    public void InsertTask(string id, string title, string? description = null)
    {
        using var connection = Open();
        connection.Execute(
            "INSERT INTO tasks (id, title, description, status_id) VALUES (@id, @title, @description, 'status-todo');",
            new { id, title, description });
    }

    public List<string> TaskTitles()
    {
        using var connection = Open();
        return connection.Query<string>("SELECT title FROM tasks ORDER BY title;").ToList();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Location.Root, recursive: true);
        }
        catch (IOException)
        {
            // A test that failed mid-way may still hold a file; the temp folder is cleaned by the OS later.
        }
    }
}

/// <summary>Clock moved by hand, in a fixed zone (UTC+2, no DST) so local-day logic is deterministic.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

using Dapper;
using FinanceOS.Data.Backup;
using FinanceOS.Data.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FinanceOS.Data.Tests;

public sealed class MigrationTests
{
    private static readonly string[] SchemaTables =
    [
        "task_statuses", "categories", "projects", "tags", "task_tags", "tasks", "checklist_items", "task_links",
        "custom_fields", "task_field_values", "recurrence_rules", "reminders", "reminder_occurrences", "notifications",
        "follow_ups", "follow_up_events", "workflow_templates", "workflow_stages", "stage_checklist_items",
        "workflow_instances", "automation_rules", "automation_runs", "time_entries", "calendar_blocks", "holidays",
        "saved_filters", "dashboard_layouts", "settings", "activity_log", "schema_version", "search_fts",
    ];

    [Fact]
    public void Embedded_catalog_starts_at_one_and_has_the_initial_schema()
    {
        Assert.Equal(1, MigrationCatalog.All[0].Version);
        Assert.Equal("initial", MigrationCatalog.All[0].Name);
        Assert.Equal(MigrationCatalog.All.Count, MigrationCatalog.LatestVersion);
    }

    [Fact]
    public void Fresh_start_creates_the_full_schema_and_records_the_version()
    {
        using var db = new TestDatabase();
        var result = db.StartReady();

        Assert.True(result.CreatedNew);
        Assert.Equal(MigrationCatalog.LatestVersion, result.Applied.Count);
        using var connection = db.Open();
        var tables = connection.Query<string>("SELECT name FROM sqlite_schema WHERE type = 'table';").ToHashSet();
        Assert.All(SchemaTables, t => Assert.Contains(t, tables));

        var applied = MigrationRunner.ReadApplied(connection);
        Assert.Equal(MigrationCatalog.All.Select(m => (long)m.Version), applied.Select(a => a.Version));
        Assert.Equal(MigrationCatalog.All.Select(m => m.Checksum), applied.Select(a => a.Checksum));
        Assert.Equal(MigrationCatalog.LatestVersion, connection.ExecuteScalar<int>("PRAGMA user_version;"));
        Assert.Empty(db.Backups.List()); // nothing to back up before the first migration
    }

    [Fact]
    public void Connections_use_wal_full_sync_and_foreign_keys()
    {
        using var db = new TestDatabase();
        db.StartReady();
        using var connection = db.Open();

        Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode;"));
        Assert.Equal(2, connection.ExecuteScalar<int>("PRAGMA synchronous;")); // 2 = FULL
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys;"));
        Assert.Equal(SqliteDatabase.BusyTimeoutMs, connection.ExecuteScalar<int>("PRAGMA busy_timeout;"));
    }

    [Fact]
    public void Bundled_sqlite_supports_what_the_schema_needs()
    {
        // STRICT tables need 3.37, VACUUM INTO 3.27; FTS5 must be compiled in.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var version = Version.Parse(connection.ExecuteScalar<string>("SELECT sqlite_version();")!);
        Assert.True(version >= new Version(3, 37), $"SQLite {version}");
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT sqlite_compileoption_used('ENABLE_FTS5');"));
    }

    [Fact]
    public void Second_start_applies_nothing()
    {
        using var db = new TestDatabase();
        db.StartReady();
        db.Startup.MarkCleanShutdown();

        var again = db.StartReady();
        Assert.False(again.CreatedNew);
        Assert.Empty(again.Applied);
    }

    [Fact]
    public void Upgrade_backs_up_first_and_keeps_data()
    {
        var v1 = MigrationCatalog.All;
        var v2 = v1.Append(new Migration(v1.Count + 1, "add_test_column", "ALTER TABLE tasks ADD COLUMN test_col TEXT;")).ToList();

        using var db = new TestDatabase(v1);
        db.StartReady();
        db.InsertTask("t1", "Raport TVA");
        db.Startup.MarkCleanShutdown();

        var result = db.StartupWith(v2).Run();

        Assert.Equal(DatabaseStatus.Ready, result.Status);
        Assert.Equal([v2[^1]], result.Applied);
        Assert.Equal(["Raport TVA"], db.TaskTitles());

        var backup = Assert.Single(db.Backups.List(), b => b.Kind == BackupKind.PreMigration);
        var health = DatabaseInspector.Inspect(backup.FilePath);
        Assert.True(health.IsHealthy, health.Problem);
        Assert.Equal(v1.Count, health.SchemaVersion); // the state before the migration
    }

    [Fact]
    public void Failed_migration_rolls_back_and_leaves_the_previous_version()
    {
        var v1 = MigrationCatalog.All;
        var broken = v1.Append(new Migration(v1.Count + 1, "broken",
            "ALTER TABLE tasks ADD COLUMN half_done TEXT; INSERT INTO no_such_table VALUES (1);")).ToList();

        using var db = new TestDatabase(v1);
        db.StartReady();
        db.InsertTask("t1", "Închidere lună");
        db.Startup.MarkCleanShutdown();

        var result = db.StartupWith(broken).Run();

        Assert.Equal(DatabaseStatus.Failed, result.Status);
        using var connection = db.Open();
        Assert.Equal(v1.Count, MigrationRunner.CurrentVersion(connection));
        var columns = connection.Query<string>("SELECT name FROM pragma_table_info('tasks');");
        Assert.DoesNotContain("half_done", columns); // the first statement was rolled back too
        Assert.Equal(["Închidere lună"], db.TaskTitles());
    }

    [Theory]
    [InlineData("SELECT 1; INSERT INTO no_such_table VALUES (1);")]
    [InlineData("PRAGMA table_info('tasks'); INSERT INTO no_such_table VALUES (1);")]
    [InlineData("ALTER TABLE tasks ADD COLUMN a TEXT; ALTER TABLE tasks ADD COLUMN b TEXT; INSERT INTO no_such_table VALUES (1);")]
    public void Error_after_a_statement_that_returns_rows_still_fails_the_migration(string sql)
    {
        // Regression: ExecuteNonQuery silently skips everything after a statement that yields a result.
        var v1 = MigrationCatalog.All;
        using var db = new TestDatabase(v1);
        db.StartReady();
        db.Startup.MarkCleanShutdown();

        var result = db.StartupWith([.. v1, new Migration(v1.Count + 1, "bad", sql)]).Run();

        Assert.Equal(DatabaseStatus.Failed, result.Status);
        using var connection = db.Open();
        Assert.Equal(v1.Count, MigrationRunner.CurrentVersion(connection));
    }

    [Fact]
    public void Every_statement_of_a_migration_runs()
    {
        var v1 = MigrationCatalog.All;
        using var db = new TestDatabase(v1);
        db.StartReady();
        db.Startup.MarkCleanShutdown();

        db.StartupWith([.. v1, new Migration(v1.Count + 1, "many", "SELECT 1; CREATE TABLE z1 (a); PRAGMA user_version; CREATE TABLE z2 (a);")]);
        db.StartReady();

        using var connection = db.Open();
        var tables = connection.Query<string>("SELECT name FROM sqlite_schema WHERE name IN ('z1', 'z2') ORDER BY name;");
        Assert.Equal(["z1", "z2"], tables);
    }

    [Fact]
    public void Newer_database_is_refused_and_left_untouched()
    {
        var future = MigrationCatalog.All
            .Append(new Migration(MigrationCatalog.LatestVersion + 1, "future", "CREATE TABLE future_feature (id TEXT);"))
            .ToList();

        using var db = new TestDatabase(future);
        db.StartReady();
        db.InsertTask("t1", "Din versiunea nouă");
        db.Startup.MarkCleanShutdown();

        var result = db.StartupWith(MigrationCatalog.All).Run();

        Assert.Equal(DatabaseStatus.TooNew, result.Status);
        Assert.Equal(MigrationCatalog.LatestVersion + 1, result.DatabaseVersion);
        Assert.Equal(MigrationCatalog.LatestVersion, result.SupportedVersion);
        Assert.False(File.Exists(db.Location.SessionMarkerPath));
        Assert.Empty(db.Backups.List());
        Assert.Equal(["Din versiunea nouă"], db.TaskTitles());
    }

    [Fact]
    public void Edited_applied_migration_is_detected()
    {
        using var db = new TestDatabase();
        db.StartReady();
        using (var connection = db.Open())
        {
            connection.Execute("UPDATE schema_version SET checksum = 'edited' WHERE version = 1;");
        }

        var result = db.Startup.Run();

        Assert.Equal(DatabaseStatus.Failed, result.Status);
        Assert.Contains("checksum", result.Problem);
    }

    [Fact]
    public void Checksum_ignores_line_endings_and_bom()
    {
        Assert.Equal(Migration.ComputeChecksum("A;\nB;\n"), Migration.ComputeChecksum("﻿A;\r\nB;\r\n"));
        Assert.NotEqual(Migration.ComputeChecksum("A;"), Migration.ComputeChecksum("B;"));
    }

    [Fact]
    public void Catalog_rejects_gaps_and_duplicates()
    {
        Assert.Throws<InvalidOperationException>(() => MigrationCatalog.Validate([new(1, "a", ""), new(3, "c", "")]));
        Assert.Throws<InvalidOperationException>(() => MigrationCatalog.Validate([new(1, "a", ""), new(1, "b", "")]));
        Assert.Throws<InvalidOperationException>(() => MigrationCatalog.Validate([]));
    }
}

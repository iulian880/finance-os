using System.Globalization;
using Dapper;
using FinanceOS.Data.Backup;
using FinanceOS.Data.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinanceOS.Data;

public enum DatabaseStatus
{
    /// <summary>Checked, migrated, ready to use.</summary>
    Ready,

    /// <summary>The file failed its integrity check or is not a database. Recovery: restore a backup or start fresh.</summary>
    Corrupt,

    /// <summary>Written by a newer Finance OS. Nothing was changed; the user needs the newer version.</summary>
    TooNew,

    /// <summary>Anything else that stopped start-up (migration failed, file locked, no permission). Recovery: retry.</summary>
    Failed,
}

/// <summary>What start-up found. <see cref="Problem"/> is technical detail for the log and "details" view.</summary>
public sealed record DatabaseStartupResult(DatabaseStatus Status, string? Problem = null)
{
    public bool CreatedNew { get; init; }

    /// <summary>The previous session did not close cleanly; a full integrity check ran.</summary>
    public bool RecoveredFromCrash { get; init; }

    public bool RanFullCheck { get; init; }

    public int DatabaseVersion { get; init; }

    public int SupportedVersion { get; init; }

    public IReadOnlyList<Migration> Applied { get; init; } = [];
}

/// <summary>
/// Start-up sequence for the database (ARCHITECTURE §7, steps 2–3): integrity check (full after a crash
/// or once a week, quick otherwise), refusal of newer databases, backup before migrating, migrations,
/// and a session marker that tells the next start whether this one ended cleanly.
/// Never throws for expected problems: they come back as a <see cref="DatabaseStartupResult"/>.
/// </summary>
public sealed class DatabaseStartup(DataLocation location, MigrationRunner migrations, BackupService backups, TimeProvider time, ILogger? logger = null)
{
    internal const string IntegrityCheckSetting = "maintenance.integrity_checked_at";
    internal static readonly TimeSpan IntegrityCheckInterval = TimeSpan.FromDays(7);

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public DataLocation Location { get; } = location;

    public int SupportedVersion => migrations.LatestVersion;

    public SqliteDatabase Database { get; } = new(location.DatabasePath);

    public DatabaseStartupResult Run()
    {
        try
        {
            Directory.CreateDirectory(Location.DataDirectory);
            var existed = File.Exists(Location.DatabasePath) && new FileInfo(Location.DatabasePath).Length > 0;
            var crashed = File.Exists(Location.SessionMarkerPath);
            var fullCheck = false;

            using var connection = Database.Open();

            if (existed)
            {
                // Refuse a newer database before anything else touches it.
                var version = MigrationRunner.CurrentVersion(connection);
                if (version > migrations.LatestVersion)
                {
                    throw new DatabaseTooNewException(version, migrations.LatestVersion);
                }

                fullCheck = crashed || IsFullCheckDue(connection);
                var problems = DatabaseInspector.Check(connection, fullCheck ? IntegrityLevel.Full : IntegrityLevel.Quick);
                if (problems.Count > 0)
                {
                    _logger.LogError("Database integrity check failed: {Problem}", string.Join("; ", problems));
                    return new DatabaseStartupResult(DatabaseStatus.Corrupt, string.Join("; ", problems)) { RecoveredFromCrash = crashed };
                }
            }

            var applied = migrations.Migrate(connection, current =>
            {
                if (current > 0)
                {
                    backups.Create(BackupKind.PreMigration);
                }
            });

            if (fullCheck || !existed)
            {
                // A new database counts as checked: the weekly clock starts now.
                RecordFullCheck(connection);
            }

            File.WriteAllText(Location.SessionMarkerPath, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            _logger.LogInformation(
                "Database ready: version {Version}, new={CreatedNew}, crash={Crashed}, fullCheck={FullCheck}, migrations={Count}",
                migrations.LatestVersion, !existed, crashed, fullCheck, applied.Count);

            return new DatabaseStartupResult(DatabaseStatus.Ready)
            {
                CreatedNew = !existed,
                RecoveredFromCrash = crashed,
                RanFullCheck = fullCheck,
                DatabaseVersion = migrations.LatestVersion,
                SupportedVersion = migrations.LatestVersion,
                Applied = applied,
            };
        }
        catch (DatabaseTooNewException ex)
        {
            _logger.LogError("Database version {DatabaseVersion} is newer than supported {SupportedVersion}", ex.DatabaseVersion, ex.SupportedVersion);
            return new DatabaseStartupResult(DatabaseStatus.TooNew, ex.Message)
            {
                DatabaseVersion = ex.DatabaseVersion,
                SupportedVersion = ex.SupportedVersion,
            };
        }
        catch (SqliteException ex) when (DatabaseInspector.IsCorruption(ex))
        {
            _logger.LogError(ex, "Database file is damaged or not a database");
            return new DatabaseStartupResult(DatabaseStatus.Corrupt, ex.Message);
        }
        catch (Exception ex) when (ex is SqliteException or MigrationFailedException or MigrationChecksumException
                                       or BackupException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Database start-up failed");
            return new DatabaseStartupResult(DatabaseStatus.Failed, ex.Message);
        }
    }

    /// <summary>On a clean exit: fold the WAL into the main file and remove the session marker.</summary>
    public void MarkCleanShutdown()
    {
        try
        {
            using (var connection = Database.Open())
            {
                connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
            }

            File.Delete(Location.SessionMarkerPath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // The next start treats this as a crash and runs a full check: safe, only slower.
            _logger.LogWarning(ex, "Clean shutdown could not be recorded");
        }
    }

    private bool IsFullCheckDue(SqliteConnection connection)
    {
        if (MigrationRunner.CurrentVersion(connection) == 0)
        {
            return false;
        }

        var json = connection.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @Key;", new { Key = IntegrityCheckSetting });
        var last = json is null ? (DateTimeOffset?)null : ParseJsonMoment(json);
        return last is null || time.GetUtcNow() - last.Value >= IntegrityCheckInterval;
    }

    private void RecordFullCheck(SqliteConnection connection) => connection.Execute(
        """
        INSERT INTO settings (key, value, updated_at) VALUES (@Key, json_quote(@At), @At)
        ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
        """,
        new { Key = IntegrityCheckSetting, At = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) });

    private static DateTimeOffset? ParseJsonMoment(string json) =>
        DateTimeOffset.TryParse(json.Trim('"'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinanceOS.Data.Migrations;

/// <summary>The database was written by a newer Finance OS. Downgrade is not supported (ARCHITECTURE §10).</summary>
public sealed class DatabaseTooNewException(int databaseVersion, int supportedVersion)
    : Exception($"Database schema version {databaseVersion} is newer than the supported version {supportedVersion}.")
{
    public int DatabaseVersion { get; } = databaseVersion;

    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>An applied migration no longer matches the script shipped in this build.</summary>
public sealed class MigrationChecksumException(int version, string expected, string actual)
    : Exception($"Migration {version} was applied with checksum {actual}, but this build ships {expected}.")
{
    public int Version { get; } = version;
}

/// <summary>A migration failed and was rolled back; the database is unchanged by it.</summary>
public sealed class MigrationFailedException(int version, Exception inner)
    : Exception($"Migration {version} failed and was rolled back.", inner)
{
    public int Version { get; } = version;
}

/// <summary>Applied schema version as recorded in <c>schema_version</c>.</summary>
public sealed record AppliedMigration(long Version, string Name, string Checksum, string AppliedAt);

/// <summary>
/// Applies numbered migrations in order, each in its own transaction together with its
/// <c>schema_version</c> row, so a failure leaves the database at the previous version.
/// Refuses a database newer than the app and a database whose applied scripts were edited.
/// </summary>
public sealed class MigrationRunner
{
    private const string CreateVersionTable = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version     INTEGER NOT NULL PRIMARY KEY,
            name        TEXT    NOT NULL,
            checksum    TEXT    NOT NULL,
            applied_at  TEXT    NOT NULL
        ) STRICT;
        """;

    private readonly ILogger _logger;

    public MigrationRunner(IReadOnlyList<Migration> migrations, ILogger? logger = null)
    {
        MigrationCatalog.Validate(migrations);
        Migrations = migrations;
        _logger = logger ?? NullLogger.Instance;
    }

    public MigrationRunner(ILogger? logger = null)
        : this(MigrationCatalog.All, logger)
    {
    }

    public IReadOnlyList<Migration> Migrations { get; }

    public int LatestVersion => Migrations[^1].Version;

    /// <summary>Schema version of the database, 0 for a new (empty) one. Does not modify anything.</summary>
    public static int CurrentVersion(SqliteConnection connection) => (int)(ReadApplied(connection).LastOrDefault()?.Version ?? 0);

    public static IReadOnlyList<AppliedMigration> ReadApplied(SqliteConnection connection)
    {
        var exists = connection.ExecuteScalar<long>(
            "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'schema_version';") > 0;
        if (!exists)
        {
            return [];
        }

        return connection.Query<AppliedMigration>(
            "SELECT version AS Version, name AS Name, checksum AS Checksum, applied_at AS AppliedAt FROM schema_version ORDER BY version;")
            .ToList();
    }

    /// <summary>
    /// Throws <see cref="DatabaseTooNewException"/> or <see cref="MigrationChecksumException"/> when the
    /// database cannot be used by this build. Returns the current version otherwise.
    /// </summary>
    public int Verify(SqliteConnection connection)
    {
        var applied = ReadApplied(connection);
        var current = (int)(applied.LastOrDefault()?.Version ?? 0);
        if (current > LatestVersion)
        {
            throw new DatabaseTooNewException(current, LatestVersion);
        }

        foreach (var row in applied)
        {
            var shipped = Migrations[(int)row.Version - 1];
            if (!string.Equals(shipped.Checksum, row.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationChecksumException((int)row.Version, shipped.Checksum, row.Checksum);
            }
        }

        return current;
    }

    /// <summary>
    /// Brings the database to <see cref="LatestVersion"/>. <paramref name="beforeFirstMigration"/> runs once,
    /// with the current version, before anything is changed (used for the pre-migration backup).
    /// Returns the migrations that were applied.
    /// </summary>
    public IReadOnlyList<Migration> Migrate(SqliteConnection connection, Action<int>? beforeFirstMigration = null)
    {
        var current = Verify(connection);
        var pending = Migrations.Where(m => m.Version > current).ToList();
        if (pending.Count == 0)
        {
            return [];
        }

        beforeFirstMigration?.Invoke(current);
        connection.Execute(CreateVersionTable);

        foreach (var migration in pending)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                SqlScript.Execute(connection, migration.Sql, transaction);
                connection.Execute(
                    "INSERT INTO schema_version (version, name, checksum, applied_at) VALUES (@Version, @Name, @Checksum, @At);",
                    new { migration.Version, migration.Name, migration.Checksum, At = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) },
                    transaction);
                connection.Execute($"PRAGMA user_version = {migration.Version};", transaction: transaction);
                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Migration {Version} failed, rolled back", migration.Version);
                throw new MigrationFailedException(migration.Version, ex);
            }

            _logger.LogInformation("Applied migration {Version} ({Kind})", migration.Version, migration.Name);
        }

        return pending;
    }
}

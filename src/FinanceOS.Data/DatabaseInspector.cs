using Dapper;
using FinanceOS.Data.Migrations;
using Microsoft.Data.Sqlite;

namespace FinanceOS.Data;

public enum IntegrityLevel
{
    /// <summary><c>PRAGMA quick_check</c>: page structure, fast enough for every start.</summary>
    Quick,

    /// <summary><c>PRAGMA integrity_check</c> + foreign keys: after a crash and weekly.</summary>
    Full,
}

/// <summary>Result of looking at a database file without changing it.</summary>
public sealed record DatabaseHealth(bool IsHealthy, int SchemaVersion, string? Problem)
{
    public static DatabaseHealth Broken(string problem) => new(false, 0, problem);
}

/// <summary>Integrity checks shared by start-up, backup verification and restore.</summary>
public static class DatabaseInspector
{
    // SQLite primary result codes that mean "the file is damaged or not a database".
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>Problems found, or an empty list when the database is healthy.</summary>
    public static IReadOnlyList<string> Check(SqliteConnection connection, IntegrityLevel level)
    {
        var pragma = level == IntegrityLevel.Quick ? "PRAGMA quick_check(20);" : "PRAGMA integrity_check(20);";
        var problems = connection.Query<string>(pragma).Where(r => r != "ok").ToList();

        if (level == IntegrityLevel.Full && problems.Count == 0)
        {
            var orphans = connection.ExecuteScalar<long>("SELECT count(*) FROM pragma_foreign_key_check;");
            if (orphans > 0)
            {
                problems.Add($"{orphans} foreign key violation(s)");
            }
        }

        return problems;
    }

    /// <summary>Opens <paramref name="path"/> (never creating it), runs a quick check and reads the schema version.</summary>
    public static DatabaseHealth Inspect(string path)
    {
        if (!File.Exists(path))
        {
            return DatabaseHealth.Broken("file not found");
        }

        try
        {
            using var connection = SqliteDatabase.OpenExisting(path, wal: false);
            var problems = Check(connection, IntegrityLevel.Quick);
            if (problems.Count > 0)
            {
                return DatabaseHealth.Broken(string.Join("; ", problems));
            }

            var version = MigrationRunner.CurrentVersion(connection);
            return version > 0 ? new DatabaseHealth(true, version, null) : DatabaseHealth.Broken("no Finance OS schema");
        }
        catch (SqliteException ex)
        {
            return DatabaseHealth.Broken($"SQLite error {ex.SqliteErrorCode}: {ex.Message}");
        }
    }

    public static bool IsCorruption(SqliteException ex) =>
        (ex.SqliteErrorCode & 0xFF) is SqliteCorrupt or SqliteNotADatabase;
}

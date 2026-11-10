using Dapper;
using Microsoft.Data.Sqlite;

namespace FinanceOS.Data;

/// <summary>
/// Opens connections to one SQLite file with the safety settings from ARCHITECTURE §10:
/// WAL journal, synchronous=FULL (an acknowledged commit survives a power cut), foreign keys on,
/// and a busy timeout instead of immediate "database is locked" errors.
/// </summary>
/// <remarks>
/// Connection pooling is off: a pooled connection keeps the file open after Dispose, which would block
/// restore and quarantine (they replace the file). Opening a local SQLite file costs well under 1 ms.
/// </remarks>
public sealed class SqliteDatabase(string path)
{
    public const int BusyTimeoutMs = 5000;

    public string FilePath { get; } = Path.GetFullPath(path);

    /// <summary>Opens a read-write connection, creating the file if needed.</summary>
    public SqliteConnection Open() => Open(FilePath, SqliteOpenMode.ReadWriteCreate, wal: true);

    /// <summary>Opens an existing file without creating it (inspection of the current database or a backup).</summary>
    internal static SqliteConnection OpenExisting(string path, bool wal) => Open(path, SqliteOpenMode.ReadWrite, wal);

    private static SqliteConnection Open(string path, SqliteOpenMode mode, bool wal)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = BusyTimeoutMs / 1000,
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            connection.Execute($"PRAGMA busy_timeout = {BusyTimeoutMs};");
            if (wal)
            {
                // journal_mode is persistent in the file; synchronous is per connection.
                connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
                connection.Execute("PRAGMA synchronous = FULL;");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

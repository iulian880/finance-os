using Microsoft.Data.Sqlite;

namespace FinanceOS.Data;

/// <summary>Runs a multi-statement SQL script so that every statement runs and every error surfaces.</summary>
/// <remarks>
/// <c>SqliteCommand.ExecuteNonQuery</c> (and Dapper's <c>Execute</c>) stops at the first statement that
/// produces a result (a SELECT, a PRAGMA, even an ALTER TABLE on a table with triggers) and silently skips
/// the rest of the script, errors included. For a migration that would mean "half applied, recorded as
/// done". Stepping through the results with <c>NextResult</c> runs and checks each statement in turn.
/// </remarks>
public static class SqlScript
{
    public static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }
}

using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FinanceOS.Data.Tests;

/// <summary>The constraints in 001_initial.sql actually hold (they are the last line of defence for the data).</summary>
public sealed class SchemaTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly SqliteConnection _connection;

    public SchemaTests()
    {
        _db.StartReady();
        _connection = _db.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Six_system_statuses_one_per_kind()
    {
        var kinds = _connection.Query<string>("SELECT kind FROM task_statuses WHERE is_system = 1 ORDER BY sort;");
        Assert.Equal(["todo", "active", "waiting", "blocked", "done", "cancelled"], kinds);
    }

    [Theory]
    [InlineData("INSERT INTO tasks (id, title, status_id) VALUES ('x', '   ', 'status-todo')")] // blank title
    [InlineData("INSERT INTO tasks (id, title, status_id, priority) VALUES ('x', 'T', 'status-todo', 5)")] // priority 0–4
    [InlineData("INSERT INTO tasks (id, title, status_id, due_date) VALUES ('x', 'T', 'status-todo', '2026-02-30')")] // no such day
    [InlineData("INSERT INTO tasks (id, title, status_id, due_date) VALUES ('x', 'T', 'status-todo', '05.11.2026')")] // wrong format
    [InlineData("INSERT INTO tasks (id, title, status_id, due_time) VALUES ('x', 'T', 'status-todo', '09:30')")] // time without date
    [InlineData("INSERT INTO tasks (id, title, status_id, due_date, due_time) VALUES ('x', 'T', 'status-todo', '2026-11-05', '25:00')")]
    [InlineData("INSERT INTO tasks (id, title, status_id) VALUES ('x', 'T', 'status-nope')")] // unknown status (FK)
    [InlineData("INSERT INTO tasks (id, title, status_id, estimate_min) VALUES ('x', 'T', 'status-todo', 'two hours')")] // STRICT type
    [InlineData("INSERT INTO settings (key, value) VALUES ('k', 'not json')")]
    [InlineData("INSERT INTO activity_log (id, actor, command, entity_type, entity_id) VALUES ('a', 'hacker', 'c', 't', 'x')")]
    public void Invalid_rows_are_rejected(string sql)
    {
        Assert.Throws<SqliteException>(() => _connection.Execute(sql));
    }

    [Fact]
    public void Valid_task_with_date_and_time_is_accepted()
    {
        _connection.Execute("INSERT INTO tasks (id, title, status_id, due_date, due_time, priority) VALUES ('x', 'T', 'status-todo', '2026-11-30', '09:30', 4);");
        Assert.Equal(1, _connection.ExecuteScalar<int>("SELECT count(*) FROM tasks;"));
    }

    [Fact]
    public void Deleting_a_task_removes_its_checklist_tags_and_reminders()
    {
        _db.InsertTask("t1", "Cu dependențe");
        _connection.Execute("""
            INSERT INTO tags (id, name) VALUES ('g1', 'urgent');
            INSERT INTO task_tags (task_id, tag_id) VALUES ('t1', 'g1');
            INSERT INTO checklist_items (id, task_id, text) VALUES ('c1', 't1', 'Pas');
            INSERT INTO reminders (id, task_id, kind, at_local) VALUES ('r1', 't1', 'absolute', '2026-11-05T09:00');
            INSERT INTO reminder_occurrences (id, reminder_id, due_utc) VALUES ('o1', 'r1', '2026-11-05T07:00:00.000Z');
            DELETE FROM tasks WHERE id = 't1';
            """);

        foreach (var table in new[] { "task_tags", "checklist_items", "reminders", "reminder_occurrences" })
        {
            Assert.Equal(0, _connection.ExecuteScalar<int>($"SELECT count(*) FROM {table};"));
        }

        Assert.Equal(1, _connection.ExecuteScalar<int>("SELECT count(*) FROM tags;")); // the tag itself stays
    }

    [Fact]
    public void Tag_names_are_unique_ignoring_case_among_live_tags()
    {
        _connection.Execute("INSERT INTO tags (id, name) VALUES ('g1', 'Urgent');");
        Assert.Throws<SqliteException>(() => _connection.Execute("INSERT INTO tags (id, name) VALUES ('g2', 'urgent');"));

        _connection.Execute("UPDATE tags SET deleted_at = '2026-11-05T08:00:00.000Z' WHERE id = 'g1';");
        _connection.Execute("INSERT INTO tags (id, name) VALUES ('g2', 'urgent');");
    }

    [Fact]
    public void Same_reminder_occurrence_cannot_be_created_twice()
    {
        _db.InsertTask("t1", "Reminder");
        _connection.Execute("INSERT INTO reminders (id, task_id, kind, at_local) VALUES ('r1', 't1', 'absolute', '2026-11-05T09:00');");
        _connection.Execute("INSERT INTO reminder_occurrences (id, reminder_id, due_utc) VALUES ('o1', 'r1', '2026-11-05T07:00:00.000Z');");

        Assert.Throws<SqliteException>(() =>
            _connection.Execute("INSERT INTO reminder_occurrences (id, reminder_id, due_utc) VALUES ('o2', 'r1', '2026-11-05T07:00:00.000Z');"));
    }

    [Theory]
    [InlineData("sedinta")]
    [InlineData("ședință")]
    [InlineData("ŞEDINŢĂ")] // cedilla forms
    [InlineData("pregat*")]
    public void Search_matches_without_diacritics_and_by_prefix(string query)
    {
        _db.InsertTask("t1", "Ședință săptămânală", "Pregătire raport");
        _db.InsertTask("t2", "Altceva");

        Assert.Equal(["t1"], Search(query));
    }

    [Fact]
    public void Search_follows_edits_tags_and_deletes()
    {
        _db.InsertTask("t1", "Factură furnizor");
        _connection.Execute("UPDATE tasks SET external_ref = 'FV-2026-0042', title = 'Factură Orion' WHERE id = 't1';");
        Assert.Equal(["t1"], Search("orion"));
        Assert.Empty(Search("furnizor"));
        Assert.Equal(["t1"], Search("\"FV-2026-0042\""));

        _connection.Execute("INSERT INTO tags (id, name) VALUES ('g1', 'trezorerie'); INSERT INTO task_tags VALUES ('t1', 'g1');");
        Assert.Equal(["t1"], Search("tags:trezorerie"));
        _connection.Execute("UPDATE tags SET name = 'cash' WHERE id = 'g1';");
        Assert.Equal(["t1"], Search("tags:cash"));
        _connection.Execute("DELETE FROM task_tags;");
        Assert.Empty(Search("tags:cash"));

        _connection.Execute("DELETE FROM tasks WHERE id = 't1';");
        Assert.Empty(Search("orion"));
    }

    private List<string> Search(string query) =>
        _connection.Query<string>("SELECT task_id FROM search_fts WHERE search_fts MATCH @query ORDER BY task_id;", new { query }).ToList();
}

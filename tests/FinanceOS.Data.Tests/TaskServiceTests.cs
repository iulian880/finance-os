using Dapper;
using FinanceOS.Core.Common;
using FinanceOS.Core.Events;
using FinanceOS.Core.Tasks;
using FinanceOS.Data.Tasks;
using Xunit;

namespace FinanceOS.Data.Tests;

/// <summary>A started database with the task service on top, recording every published event.</summary>
internal sealed class TaskFixture : IDisposable
{
    public TaskFixture()
    {
        Db.StartReady();
        Store = new SqliteTaskStore(new SqliteDatabase(Db.Location.DatabasePath));
        Bus.Subscribe<IDomainEvent>(Events.Add);
        Service = new TaskService(Store, Bus, Db.Clock);
    }

    public TestDatabase Db { get; } = new();

    public SqliteTaskStore Store { get; }

    public EventBus Bus { get; } = new();

    public TaskService Service { get; private set; }

    public List<IDomainEvent> Events { get; } = [];

    /// <summary>"Restart": new connection objects, store and service over the same file (undo history gone).</summary>
    public TaskService Restart()
    {
        Service = new TaskService(new SqliteTaskStore(new SqliteDatabase(Db.Location.DatabasePath)), Bus, Db.Clock);
        return Service;
    }

    public List<(string Command, string EntityType, string EntityId, string? Before, string? After, string? BatchId)> Activity()
    {
        using var connection = Db.Open();
        return connection.Query<(string, string, string, string?, string?, string?)>(
            "SELECT command, entity_type, entity_id, before, after, batch_id FROM activity_log ORDER BY rowid;").ToList();
    }

    public long Count(string sql)
    {
        using var connection = Db.Open();
        return connection.ExecuteScalar<long>(sql);
    }

    public void Dispose() => Db.Dispose();
}

public sealed class TaskServiceTests : IDisposable
{
    private readonly TaskFixture _f = new();

    private TaskService Tasks => _f.Service;

    public void Dispose() => _f.Dispose();

    // Create ------------------------------------------------------------------------------------------

    [Fact]
    public void Create_with_only_a_title_uses_the_defaults_and_is_logged_and_announced()
    {
        var task = Tasks.Create(new TaskDraft("  Reconcile bank accounts  "));

        Assert.Equal("Reconcile bank accounts", task.Title);
        Assert.Equal(SystemStatuses.Todo, task.StatusId);
        Assert.Equal(Priority.None, task.Priority);
        Assert.Equal(_f.Db.Clock.GetUtcNow(), task.CreatedAt);
        Assert.Null(task.CompletedAt);
        Assert.True(Snapshots.Same(task, _f.Store.GetTask(task.Id)));

        var log = Assert.Single(_f.Activity());
        Assert.Equal(("task.create", "task", task.Id), (log.Command, log.EntityType, log.EntityId));
        Assert.Null(log.Before);
        Assert.Contains("\"title\":\"Reconcile bank accounts\"", log.After);
        Assert.NotNull(log.BatchId);

        var created = Assert.IsType<TaskCreated>(Assert.Single(_f.Events));
        Assert.Equal(ChangeOrigin.Command, created.Origin);
        Assert.Equal(log.BatchId, created.BatchId);
    }

    [Fact]
    public void Full_task_round_trips_through_the_database_unchanged()
    {
        var category = Tasks.CreateTaxonomy(TaxonomyKind.Category, "Month-end", "#2563EB");
        var project = Tasks.CreateTaxonomy(TaxonomyKind.Project, "Audit 2026");
        var urgent = Tasks.GetOrCreateTag("urgent");
        var vat = Tasks.GetOrCreateTag("VAT");

        var task = Tasks.Create(new TaskDraft("VAT return")
        {
            Description = "Quarterly",
            Notes = "Ask Ana for the Q3 invoices",
            Priority = Priority.High,
            CategoryId = category.Id,
            ProjectId = project.Id,
            DueDate = new DateOnly(2026, 11, 25),
            DueTime = new TimeOnly(14, 30, 59),
            StartDate = new DateOnly(2026, 11, 20),
            EstimateMinutes = 90,
            ExternalRef = "INV-2026-117",
            ExternalUrl = "https://intranet.example/inv/117",
            Requester = "CFO",
            Responsible = "me",
            IsPinned = true,
            Tags = [vat.Id, urgent.Id, vat.Id],
            Checklist = ["Export ledger", "  Check totals "],
        });

        Assert.Equal(new TimeOnly(14, 30), task.DueTime);
        Assert.Equal(new[] { urgent.Id, vat.Id }.Order(StringComparer.Ordinal), task.Tags);
        Assert.Equal(["Export ledger", "Check totals"], task.Checklist.Select(c => c.Text));
        Assert.True(Snapshots.Same(task, _f.Store.GetTask(task.Id)));
        Assert.True(Snapshots.Same(task, _f.Restart().Get(task.Id)));
    }

    [Fact]
    public void Invalid_drafts_are_refused_with_codes_and_nothing_is_written()
    {
        var deletedCategory = Tasks.CreateTaxonomy(TaxonomyKind.Category, "Old");
        Tasks.DeleteTaxonomy(TaxonomyKind.Category, deletedCategory.Id);
        var before = _f.Activity().Count;

        var error = Assert.Throws<DomainValidationException>(() => Tasks.Create(new TaskDraft("   ")
        {
            DueTime = new TimeOnly(9, 0),
            StartDate = new DateOnly(2026, 12, 1),
            ExternalUrl = "file:///C:/Windows/System32/calc.exe",
            StatusId = "status-nope",
            CategoryId = deletedCategory.Id,
            ProjectId = "missing",
            EstimateMinutes = -5,
        }));

        Assert.True(error.Has(nameof(TaskItem.Title), ValidationCodes.Required));
        Assert.True(error.Has(nameof(TaskItem.DueTime), ValidationCodes.TimeWithoutDate));
        Assert.True(error.Has(nameof(TaskItem.ExternalUrl), ValidationCodes.InvalidUrl));
        Assert.True(error.Has(nameof(TaskItem.StatusId), ValidationCodes.NotFound));
        Assert.True(error.Has(nameof(TaskItem.CategoryId), ValidationCodes.Deleted));
        Assert.True(error.Has(nameof(TaskItem.ProjectId), ValidationCodes.NotFound));
        Assert.True(error.Has(nameof(TaskItem.EstimateMinutes), ValidationCodes.OutOfRange));
        Assert.Equal(0, _f.Count("SELECT count(*) FROM tasks;"));
        Assert.Equal(before, _f.Activity().Count);

        var dates = Assert.Throws<DomainValidationException>(() => Tasks.Create(new TaskDraft("x")
        {
            DueDate = new DateOnly(2026, 11, 1),
            StartDate = new DateOnly(2026, 11, 2),
        }));
        Assert.True(dates.Has(nameof(TaskItem.StartDate), ValidationCodes.StartAfterDue));
        Assert.Throws<DomainValidationException>(() => Tasks.Create(new TaskDraft(new string('x', 501))));
    }

    [Fact]
    public void Creating_directly_as_done_records_the_completion_time()
    {
        var task = Tasks.Create(new TaskDraft("Already sent") { StatusId = SystemStatuses.Done });
        Assert.Equal(_f.Db.Clock.GetUtcNow(), task.CompletedAt);
    }

    // Edit --------------------------------------------------------------------------------------------

    [Fact]
    public void Edit_changes_editable_fields_and_announces_due_changes()
    {
        var task = Tasks.Create(new TaskDraft("Payroll"));
        _f.Db.Clock.Advance(TimeSpan.FromMinutes(5));
        _f.Events.Clear();

        var edited = Tasks.Edit(task.Id, t => t with { Title = "Payroll November", DueDate = new DateOnly(2026, 11, 28), Priority = Priority.Urgent });

        Assert.Equal("Payroll November", edited.Title);
        Assert.Equal(_f.Db.Clock.GetUtcNow(), edited.UpdatedAt);
        Assert.Equal(task.CreatedAt, edited.CreatedAt);
        Assert.Contains(_f.Events, e => e is TaskChanged);
        var due = Assert.Single(_f.Events.OfType<TaskDueChanged>());
        Assert.Null(due.Old.DueDate);
        Assert.Empty(_f.Events.OfType<TaskStatusChanged>());
        Assert.True(Snapshots.Same(edited, Tasks.Get(task.Id)));
    }

    [Fact]
    public void Edit_that_changes_nothing_writes_nothing()
    {
        var task = Tasks.Create(new TaskDraft("Same"));
        var logged = _f.Activity().Count;
        _f.Events.Clear();

        var result = Tasks.Edit(task.Id, t => t with { Title = " Same ", Description = "   " });

        Assert.True(Snapshots.Same(task, result));
        Assert.Equal(logged, _f.Activity().Count);
        Assert.Empty(_f.Events);
        Assert.Equal("task.create", Tasks.UndoCommand);
    }

    [Fact]
    public void Edit_refuses_fields_that_have_their_own_commands()
    {
        var task = Tasks.Create(new TaskDraft("x"));
        Assert.Throws<InvalidOperationException>(() => Tasks.Edit(task.Id, t => t with { StatusId = SystemStatuses.Done }));
        Assert.Throws<InvalidOperationException>(() => Tasks.Edit(task.Id, t => t with { DeletedAt = DateTimeOffset.UnixEpoch }));
        Assert.Throws<EntityNotFoundException>(() => Tasks.Edit("missing", t => t));
    }

    // Status ------------------------------------------------------------------------------------------

    [Fact]
    public void Complete_reopen_and_status_changes_keep_the_completion_time_honest()
    {
        var task = Tasks.Create(new TaskDraft("Close the books"));
        _f.Db.Clock.Advance(TimeSpan.FromHours(1));

        var done = Tasks.Complete(task.Id);
        Assert.Equal(SystemStatuses.Done, done.StatusId);
        Assert.Equal(_f.Db.Clock.GetUtcNow(), done.CompletedAt);
        Assert.Contains(_f.Events, e => e is TaskStatusChanged);

        var logged = _f.Activity().Count;
        Assert.True(Snapshots.Same(done, Tasks.Complete(task.Id)));
        Assert.Equal(logged, _f.Activity().Count);

        var reopened = Tasks.Reopen(task.Id);
        Assert.Equal(SystemStatuses.Todo, reopened.StatusId);
        Assert.Null(reopened.CompletedAt);

        var waiting = Tasks.SetStatus(task.Id, SystemStatuses.Waiting);
        Assert.Equal(SystemStatuses.Waiting, waiting.StatusId);
        Assert.Equal(SystemStatuses.Waiting, Tasks.Reopen(task.Id).StatusId); // open tasks are left alone

        var cancelled = Tasks.SetStatus(task.Id, SystemStatuses.Cancelled);
        Assert.Null(cancelled.CompletedAt);
        Assert.Equal(SystemStatuses.Todo, Tasks.Reopen(task.Id).StatusId);

        Assert.Throws<DomainValidationException>(() => Tasks.SetStatus(task.Id, "status-nope"));
        Assert.Equal(6, Tasks.Statuses().Count);
    }

    // Trash and archive ----------------------------------------------------------------------------------

    [Fact]
    public void Delete_and_restore_carry_the_subtasks_that_went_together()
    {
        var parent = Tasks.Create(new TaskDraft("Month-end close"));
        var a = Tasks.Create(new TaskDraft("Accruals") { ParentId = parent.Id });
        var b = Tasks.Create(new TaskDraft("Bank rec") { ParentId = parent.Id });
        _f.Db.Clock.Advance(TimeSpan.FromMinutes(1));
        Tasks.Delete(b.Id); // trashed on its own, earlier
        _f.Db.Clock.Advance(TimeSpan.FromMinutes(1));

        Tasks.Delete(parent.Id);

        Assert.Empty(Tasks.List(TaskScope.Open));
        Assert.Equal(3, Tasks.List(TaskScope.Trash).Count);
        var lastBatch = _f.Activity()[^1].BatchId;
        Assert.Equal(2, _f.Activity().Count(r => r.Command == "task.delete" && r.BatchId == lastBatch));
        Assert.Throws<DomainValidationException>(() => Tasks.Edit(parent.Id, t => t with { Title = "x" }));

        var restoreChild = Assert.Throws<DomainValidationException>(() => Tasks.Restore(a.Id));
        Assert.True(restoreChild.Has(nameof(TaskItem.ParentId), ValidationCodes.ParentInTrash));

        Tasks.Restore(parent.Id);

        Assert.False(Tasks.Get(parent.Id)!.IsDeleted);
        Assert.False(Tasks.Get(a.Id)!.IsDeleted);
        Assert.True(Tasks.Get(b.Id)!.IsDeleted); // was trashed separately, stays in the trash
        Assert.Equal([a.Id], Tasks.Subtasks(parent.Id).Select(t => t.Id));
        Assert.Contains(_f.Events, e => e is TaskRestored);
    }

    [Fact]
    public void Archive_hides_the_task_and_its_subtasks_and_unarchive_brings_them_back()
    {
        var parent = Tasks.Create(new TaskDraft("Audit"));
        var child = Tasks.Create(new TaskDraft("Send PBC list") { ParentId = parent.Id });

        Tasks.Archive(parent.Id);

        Assert.Empty(Tasks.List());
        Assert.Equal(2, Tasks.List(TaskScope.Archived).Count);
        Assert.Contains(_f.Events, e => e is TaskArchived);

        Tasks.Unarchive(parent.Id);

        Assert.Equal([parent.Id, child.Id], Tasks.List().Select(t => t.Id));
        Assert.Contains(_f.Events, e => e is TaskUnarchived);
    }

    // Duplicate and subtasks ----------------------------------------------------------------------------

    [Fact]
    public void Duplicate_copies_fields_tags_open_checklist_and_subtasks()
    {
        var tag = Tasks.GetOrCreateTag("monthly");
        var source = Tasks.Create(new TaskDraft("Intercompany") { Tags = [tag.Id], Checklist = ["Match balances"], Priority = Priority.High, IsPinned = true });
        Tasks.SetChecklistItemDone(source.Id, source.Checklist[0].Id, true);
        Tasks.Complete(source.Id);
        Tasks.Create(new TaskDraft("Confirm with RO02") { ParentId = source.Id });

        var copy = Tasks.Duplicate(source.Id, "Intercompany (copy)");

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Intercompany (copy)", copy.Title);
        Assert.Equal(SystemStatuses.Todo, copy.StatusId);
        Assert.Null(copy.CompletedAt);
        Assert.False(copy.IsPinned);
        Assert.Equal(Priority.High, copy.Priority);
        Assert.Equal([tag.Id], copy.Tags);
        var item = Assert.Single(copy.Checklist);
        Assert.False(item.IsDone);
        Assert.NotEqual(source.Checklist[0].Id, item.Id);
        Assert.Equal(["Confirm with RO02"], Tasks.Subtasks(copy.Id).Select(t => t.Title));

        Assert.Equal("task.duplicate", Tasks.Undo());
        Assert.Null(Tasks.Get(copy.Id));
        Assert.Single(Tasks.Subtasks(source.Id));
    }

    [Fact]
    public void Subtasks_are_one_level_deep()
    {
        var parent = Tasks.Create(new TaskDraft("Parent"));
        var child = Tasks.Create(new TaskDraft("Child") { ParentId = parent.Id });
        var other = Tasks.Create(new TaskDraft("Other"));

        Assert.True(Assert.Throws<DomainValidationException>(() => Tasks.Create(new TaskDraft("Grandchild") { ParentId = child.Id }))
            .Has(nameof(TaskItem.ParentId), ValidationCodes.NestedSubtask));
        Assert.True(Assert.Throws<DomainValidationException>(() => Tasks.MoveTo(parent.Id, other.Id))
            .Has(nameof(TaskItem.ParentId), ValidationCodes.HasSubtasks));
        Assert.True(Assert.Throws<DomainValidationException>(() => Tasks.MoveTo(other.Id, other.Id))
            .Has(nameof(TaskItem.ParentId), ValidationCodes.SelfParent));

        Assert.Equal(parent.Id, Tasks.MoveTo(other.Id, parent.Id).ParentId);
        Assert.Null(Tasks.MoveTo(child.Id, null).ParentId);
    }

    // Checklist -------------------------------------------------------------------------------------------

    [Fact]
    public void Checklist_items_can_be_added_checked_renamed_and_removed()
    {
        var task = Tasks.Create(new TaskDraft("Close") { Checklist = ["First"] });

        var second = Tasks.AddChecklistItem(task.Id, " Second ");
        Assert.Equal("Second", second.Text);
        Assert.True(second.Sort > task.Checklist[0].Sort);

        Tasks.SetChecklistItemDone(task.Id, second.Id, true);
        Tasks.RenameChecklistItem(task.Id, second.Id, "Second, renamed");
        Assert.Throws<DomainValidationException>(() => Tasks.RenameChecklistItem(task.Id, second.Id, " "));
        Assert.Throws<DomainValidationException>(() => Tasks.AddChecklistItem(task.Id, ""));
        Assert.Throws<EntityNotFoundException>(() => Tasks.SetChecklistItemDone(task.Id, "missing", true));

        var current = Tasks.Get(task.Id)!;
        Assert.Equal([("First", false), ("Second, renamed", true)], current.Checklist.Select(c => (c.Text, c.IsDone)));

        Tasks.RemoveChecklistItem(task.Id, task.Checklist[0].Id);
        Assert.Equal(["Second, renamed"], Tasks.Get(task.Id)!.Checklist.Select(c => c.Text));
        Assert.Equal(1, _f.Count("SELECT count(*) FROM checklist_items;"));
    }

    // Taxonomies ------------------------------------------------------------------------------------------

    [Fact]
    public void Taxonomy_names_are_unique_per_kind_ignoring_case()
    {
        var tag = Tasks.CreateTaxonomy(TaxonomyKind.Tag, "Urgent", "#C42D5A");

        Assert.True(Assert.Throws<DomainValidationException>(() => Tasks.CreateTaxonomy(TaxonomyKind.Tag, "urgent"))
            .Has(nameof(TaxonomyItem.Name), ValidationCodes.Duplicate));
        Assert.Equal(tag.Id, Tasks.GetOrCreateTag(" URGENT ").Id);
        Assert.Equal("Urgent", Tasks.CreateTaxonomy(TaxonomyKind.Category, "Urgent").Name); // other kind: fine
        Assert.Throws<DomainValidationException>(() => Tasks.CreateTaxonomy(TaxonomyKind.Project, "P", "blue"));

        Tasks.RenameTaxonomy(TaxonomyKind.Tag, tag.Id, "Critical");
        Tasks.SetTaxonomyColor(TaxonomyKind.Tag, tag.Id, null);
        var renamed = Assert.Single(Tasks.Taxonomy(TaxonomyKind.Tag));
        Assert.Equal(("Critical", (string?)null), (renamed.Name, renamed.Color));
    }

    [Fact]
    public void Deleting_a_tag_removes_it_from_tasks_and_undo_puts_it_back_everywhere()
    {
        var tag = Tasks.GetOrCreateTag("q4");
        var category = Tasks.CreateTaxonomy(TaxonomyKind.Category, "Tax");
        var first = Tasks.Create(new TaskDraft("One") { Tags = [tag.Id], CategoryId = category.Id });
        var second = Tasks.Create(new TaskDraft("Two") { Tags = [tag.Id] });
        Assert.Equal(1, _f.Count($"SELECT count(*) FROM search_fts WHERE search_fts MATCH 'q4' AND task_id = '{first.Id}';"));

        Tasks.DeleteTaxonomy(TaxonomyKind.Tag, tag.Id);
        Tasks.DeleteTaxonomy(TaxonomyKind.Category, category.Id);

        Assert.Empty(Tasks.Get(first.Id)!.Tags);
        Assert.Null(Tasks.Get(first.Id)!.CategoryId);
        Assert.Empty(Tasks.Taxonomy(TaxonomyKind.Tag));
        Assert.Equal(0, _f.Count("SELECT count(*) FROM search_fts WHERE search_fts MATCH 'q4';"));

        Tasks.Undo();
        Tasks.Undo();

        Assert.Equal([tag.Id], Tasks.Get(first.Id)!.Tags);
        Assert.Equal([tag.Id], Tasks.Get(second.Id)!.Tags);
        Assert.Equal(category.Id, Tasks.Get(first.Id)!.CategoryId);
        Assert.Equal(2, _f.Count("SELECT count(*) FROM search_fts WHERE search_fts MATCH 'q4';"));
    }

    // Undo ------------------------------------------------------------------------------------------------

    [Fact]
    public void Undo_reverts_each_action_exactly_newest_first()
    {
        var task = Tasks.Create(new TaskDraft("Report"));
        Tasks.Edit(task.Id, t => t with { Title = "Report v2" });
        Tasks.Complete(task.Id);
        var changes = 0;
        Tasks.UndoChanged += (_, _) => changes++;
        _f.Events.Clear();

        Assert.Equal("task.complete", Tasks.Undo());
        Assert.Equal(SystemStatuses.Todo, Tasks.Get(task.Id)!.StatusId);
        Assert.Null(Tasks.Get(task.Id)!.CompletedAt);
        Assert.All(_f.Events, e => Assert.Equal(ChangeOrigin.Undo, e.Origin));
        Assert.Contains(_f.Events, e => e is TaskStatusChanged);

        Assert.Equal("task.edit", Tasks.Undo());
        Assert.Equal("Report", Tasks.Get(task.Id)!.Title);

        Assert.Equal("task.create", Tasks.Undo());
        Assert.Null(Tasks.Get(task.Id));
        Assert.Contains(_f.Events, e => e is TaskRemoved);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM search_fts;"));

        Assert.False(Tasks.CanUndo);
        Assert.Null(Tasks.Undo());
        Assert.Equal(3, changes);

        // History stays: three actions and three undos, each undo marked as such.
        var log = _f.Activity();
        Assert.Equal(["task.create", "task.edit", "task.complete", "undo:task.complete", "undo:task.edit", "undo:task.create"], log.Select(r => r.Command));
        Assert.Null(log[^1].After);
    }

    [Fact]
    public void Undo_of_a_delete_restores_the_whole_batch()
    {
        var parent = Tasks.Create(new TaskDraft("Parent"));
        var child = Tasks.Create(new TaskDraft("Child") { ParentId = parent.Id });
        Tasks.Delete(parent.Id);

        Assert.Equal("task.delete", Tasks.Undo());

        Assert.False(Tasks.Get(parent.Id)!.IsDeleted);
        Assert.False(Tasks.Get(child.Id)!.IsDeleted);
        var undo = _f.Activity().Where(r => r.Command == "undo:task.delete").ToList();
        Assert.Equal(2, undo.Count);
        Assert.Single(undo.Select(r => r.BatchId).Distinct());
    }

    [Fact]
    public void Undo_refuses_when_the_task_changed_behind_its_back()
    {
        var task = Tasks.Create(new TaskDraft("Shared"));
        Tasks.Edit(task.Id, t => t with { Title = "Mine" });

        // A change that did not go through this service's undo stack (e.g. a future automation).
        var current = _f.Store.GetTask(task.Id)!;
        _f.Store.Commit(new ChangeSet([new TaskWrite(current, current with { Title = "Theirs" })], []));

        Assert.Throws<UndoConflictException>(() => Tasks.Undo());
        Assert.Equal("Theirs", Tasks.Get(task.Id)!.Title);
        Assert.Equal("task.create", Tasks.UndoCommand); // the conflicting entry was dropped, not retried forever
    }

    [Fact]
    public void Undo_keeps_only_the_last_actions_of_the_session()
    {
        var task = Tasks.Create(new TaskDraft("Counter"));
        for (var i = 0; i < TaskService.UndoDepth + 5; i++)
        {
            Tasks.Edit(task.Id, t => t with { Title = $"Counter {i}" });
        }

        var undone = 0;
        while (Tasks.Undo() is not null)
        {
            undone++;
        }

        Assert.Equal(TaskService.UndoDepth, undone);
        Assert.Equal("Counter 4", Tasks.Get(task.Id)!.Title);
    }

    // Persistence, events, schema --------------------------------------------------------------------------

    [Fact]
    public void Everything_survives_a_restart_but_the_undo_stack_does_not()
    {
        var tag = Tasks.GetOrCreateTag("vat");
        var task = Tasks.Create(new TaskDraft("Persist me") { Tags = [tag.Id], Checklist = ["a", "b"], DueDate = new DateOnly(2026, 12, 31) });
        Tasks.Complete(task.Id);
        var before = Tasks.Get(task.Id);

        var restarted = _f.Restart();

        Assert.True(Snapshots.Same(before, restarted.Get(task.Id)));
        Assert.Equal(["vat"], restarted.Taxonomy(TaxonomyKind.Tag).Select(t => t.Name));
        Assert.False(restarted.CanUndo);
        Assert.Equal(3, _f.Activity().Count);
    }

    [Fact]
    public void Events_are_published_after_the_commit_and_a_failing_handler_does_not_undo_the_command()
    {
        TaskItem? seenInStore = null;
        _f.Bus.Subscribe<TaskCreated>(e => seenInStore = _f.Store.GetTask(e.Task.Id));
        _f.Bus.Subscribe<TaskCreated>(_ => throw new InvalidOperationException("broken subscriber"));
        var failures = new List<Exception>();
        _f.Bus.HandlerFailed += (_, ex) => failures.Add(ex);

        var task = Tasks.Create(new TaskDraft("Visible to handlers"));

        Assert.NotNull(seenInStore);
        Assert.Equal(task.Id, seenInStore!.Id);
        Assert.Single(failures);
        Assert.NotNull(Tasks.Get(task.Id));
    }

    [Fact]
    public void Columns_the_domain_does_not_model_yet_are_kept_on_update()
    {
        var task = Tasks.Create(new TaskDraft("Timed"));
        using (var connection = _f.Db.Open())
        {
            connection.Execute("UPDATE tasks SET actual_min = 42 WHERE id = @Id;", new { task.Id });
        }

        Tasks.Edit(task.Id, t => t with { Title = "Timed, renamed" });

        Assert.Equal(42, _f.Count($"SELECT actual_min FROM tasks WHERE id = '{task.Id}';"));
    }

    [Fact]
    public void Ids_are_time_ordered_uuid_v7()
    {
        var first = Tasks.Create(new TaskDraft("A"));
        _f.Db.Clock.Advance(TimeSpan.FromMilliseconds(5));
        var second = Tasks.Create(new TaskDraft("B"));

        Assert.Equal(7, Guid.Parse(first.Id).Version);
        Assert.True(string.CompareOrdinal(first.Id, second.Id) < 0);
        Assert.True(second.SortOrder > first.SortOrder);
    }
}

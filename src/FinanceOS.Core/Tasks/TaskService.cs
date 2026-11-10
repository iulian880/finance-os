using FinanceOS.Core.Common;
using FinanceOS.Core.Events;

namespace FinanceOS.Core.Tasks;

/// <summary>
/// Application service for tasks, checklists and taxonomies (ARCHITECTURE §4.4). Every command:
/// validates → writes everything it changes plus its <c>activity_log</c> rows in one transaction →
/// publishes domain events after the commit → can be undone (in-memory stack for the session).
/// A command that would change nothing writes nothing, logs nothing and is not undoable.
/// </summary>
/// <remarks>
/// Rules worth knowing:
/// <list type="bullet">
/// <item>Subtasks are one level deep. Trashing, restoring, archiving and unarchiving a task carries its
/// subtasks along (only the ones that went with it); completing does not.</item>
/// <item>A trashed task is read-only until restored. A subtask cannot be restored while its parent is in the trash.</item>
/// <item>Deleting a category, project or tag also removes it from every task, in the same undoable batch.</item>
/// </list>
/// </remarks>
public sealed class TaskService
{
    /// <summary>Undo keeps the last N user actions of the session (the log keeps everything).</summary>
    public const int UndoDepth = 50;

    private const string Actor = "user";

    private readonly ITaskStore _store;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<UndoEntry> _undo = [];

    public TaskService(ITaskStore store, IEventBus bus, TimeProvider time)
    {
        _store = store;
        _bus = bus;
        _time = time;
    }

    /// <summary>The undo stack changed (enable or disable the Undo command, update its label).</summary>
    public event EventHandler? UndoChanged;

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0;
            }
        }
    }

    /// <summary>Command name of the action Undo would revert, e.g. "task.complete"; null when nothing to undo.</summary>
    public string? UndoCommand
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0 ? _undo[^1].Command : null;
            }
        }
    }

    /// <summary>
    /// Batch id of the action Undo would revert. A toast's "Undo" button compares it with the batch of
    /// its own action, so it never reverts something the user did afterwards.
    /// </summary>
    public string? UndoBatchId
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0 ? _undo[^1].BatchId : null;
            }
        }
    }

    // Queries ---------------------------------------------------------------------------------------

    /// <summary>The task in any state (trashed and archived included), or null.</summary>
    public TaskItem? Get(string id) => _store.GetTask(id);

    public IReadOnlyList<TaskItem> List(TaskScope scope = TaskScope.Open) => _store.QueryTasks(scope);

    /// <summary>Subtasks that are not in the trash, in order.</summary>
    public IReadOnlyList<TaskItem> Subtasks(string parentId) => [.. _store.GetSubtasks(parentId).Where(t => !t.IsDeleted)];

    public IReadOnlyList<TaskStatusInfo> Statuses() => _store.GetStatuses();

    /// <summary>Categories, projects or tags that are not deleted, by sort order then name.</summary>
    public IReadOnlyList<TaxonomyItem> Taxonomy(TaxonomyKind kind) =>
        [.. _store.GetTaxonomy(kind).Where(t => !t.IsDeleted).OrderBy(t => t.Sort).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];

    // Task commands ---------------------------------------------------------------------------------

    public TaskItem Create(TaskDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Execute("task.create", batch =>
        {
            var id = Ids.New(_time);
            var checklist = draft.Checklist
                .Select((text, i) => new ChecklistItem(Ids.New(_time), text.Trim(), false, i + 1))
                .ToList();
            var task = Normalize(new TaskItem
            {
                Id = id,
                Title = draft.Title,
                Description = draft.Description,
                Notes = draft.Notes,
                StatusId = draft.StatusId,
                Priority = draft.Priority,
                CategoryId = draft.CategoryId,
                ProjectId = draft.ProjectId,
                ParentId = draft.ParentId,
                DueDate = draft.DueDate,
                DueTime = draft.DueTime,
                StartDate = draft.StartDate,
                EstimateMinutes = draft.EstimateMinutes,
                ExternalRef = draft.ExternalRef,
                ExternalUrl = draft.ExternalUrl,
                Requester = draft.Requester,
                Responsible = draft.Responsible,
                IsPinned = draft.IsPinned,
                Tags = draft.Tags,
                Checklist = checklist,
                IsDemo = draft.IsDemo,
                SortOrder = _store.MaxSortOrder() + 1,
                CreatedAt = batch.Now,
                UpdatedAt = batch.Now,
            });
            task = task with { CompletedAt = IsDone(task.StatusId) ? batch.Now : null };

            var errors = new List<ValidationError>();
            TaskRules.CheckFields(task, _store, errors);
            TaskRules.CheckParent(task, _store, errors);
            TaskRules.ThrowIfAny(errors);

            batch.Add(new TaskWrite(null, task));
            return task;
        });
    }

    /// <summary>
    /// Changes the editable fields: title, texts, priority, category, project, dates, estimate, references,
    /// pinned, tags, sort order. Status, parent, checklist, trash and archive have their own commands;
    /// changing them here is a programming error.
    /// </summary>
    public TaskItem Edit(string id, Func<TaskItem, TaskItem> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Execute("task.edit", batch =>
        {
            var current = RequireWritable(id);
            var proposed = change(current);
            var others = proposed with
            {
                Title = current.Title,
                Description = current.Description,
                Notes = current.Notes,
                Priority = current.Priority,
                CategoryId = current.CategoryId,
                ProjectId = current.ProjectId,
                DueDate = current.DueDate,
                DueTime = current.DueTime,
                StartDate = current.StartDate,
                EstimateMinutes = current.EstimateMinutes,
                ExternalRef = current.ExternalRef,
                ExternalUrl = current.ExternalUrl,
                Requester = current.Requester,
                Responsible = current.Responsible,
                IsPinned = current.IsPinned,
                Tags = current.Tags,
                SortOrder = current.SortOrder,
            };
            if (!Snapshots.Same(others, current))
            {
                throw new InvalidOperationException(
                    "Edit only changes editable fields; use SetStatus, MoveTo, the checklist commands, Delete or Archive for the rest.");
            }

            var updated = Normalize(proposed);
            var errors = new List<ValidationError>();
            TaskRules.CheckFields(updated, _store, errors);
            TaskRules.ThrowIfAny(errors);
            return batch.Update(current, updated);
        });
    }

    public TaskItem SetStatus(string id, string statusId) => Execute("task.status", batch =>
    {
        var current = RequireWritable(id);
        var status = _store.GetStatus(statusId)
            ?? throw new DomainValidationException([new(nameof(TaskItem.StatusId), ValidationCodes.NotFound)]);
        return batch.Update(current, WithStatus(current, status, batch.Now));
    });

    /// <summary>Marks the task done. Already done: nothing happens.</summary>
    public TaskItem Complete(string id) => Execute("task.complete", batch =>
    {
        var current = RequireWritable(id);
        return batch.Update(current, WithStatus(current, _store.GetStatus(SystemStatuses.Done)!, batch.Now));
    });

    /// <summary>Back to "to do" from done or cancelled. An open task stays as it is.</summary>
    public TaskItem Reopen(string id) => Execute("task.reopen", batch =>
    {
        var current = RequireWritable(id);
        if (_store.GetStatus(current.StatusId) is not { IsClosed: true })
        {
            return current;
        }

        return batch.Update(current, WithStatus(current, _store.GetStatus(SystemStatuses.Todo)!, batch.Now));
    });

    /// <summary>Moves the task, and its subtasks, to the trash.</summary>
    public TaskItem Delete(string id) => Execute("task.delete", batch =>
    {
        var current = Require(id);
        if (current.IsDeleted)
        {
            return current;
        }

        var deleted = batch.Update(current, current with { DeletedAt = batch.Now });
        foreach (var subtask in _store.GetSubtasks(id).Where(s => !s.IsDeleted))
        {
            batch.Update(subtask, subtask with { DeletedAt = batch.Now });
        }

        return deleted;
    });

    /// <summary>Brings the task back from the trash, with the subtasks that were trashed together with it.</summary>
    public TaskItem Restore(string id) => Execute("task.restore", batch =>
    {
        var current = Require(id);
        if (!current.IsDeleted)
        {
            return current;
        }

        if (current.ParentId is { } parentId && _store.GetTask(parentId) is { IsDeleted: true })
        {
            throw new DomainValidationException([new(nameof(TaskItem.ParentId), ValidationCodes.ParentInTrash)]);
        }

        var restored = batch.Update(current, current with { DeletedAt = null });
        foreach (var subtask in _store.GetSubtasks(id).Where(s => s.DeletedAt == current.DeletedAt))
        {
            batch.Update(subtask, subtask with { DeletedAt = null });
        }

        return restored;
    });

    /// <summary>Hides the task (and its subtasks) from the working lists; it stays searchable in Archive.</summary>
    public TaskItem Archive(string id) => Execute("task.archive", batch =>
    {
        var current = RequireWritable(id);
        if (current.IsArchived)
        {
            return current;
        }

        var archived = batch.Update(current, current with { ArchivedAt = batch.Now });
        foreach (var subtask in _store.GetSubtasks(id).Where(s => !s.IsDeleted && !s.IsArchived))
        {
            batch.Update(subtask, subtask with { ArchivedAt = batch.Now });
        }

        return archived;
    });

    public TaskItem Unarchive(string id) => Execute("task.unarchive", batch =>
    {
        var current = RequireWritable(id);
        if (!current.IsArchived)
        {
            return current;
        }

        var restored = batch.Update(current, current with { ArchivedAt = null });
        foreach (var subtask in _store.GetSubtasks(id).Where(s => !s.IsDeleted && s.ArchivedAt == current.ArchivedAt))
        {
            batch.Update(subtask, subtask with { ArchivedAt = null });
        }

        return restored;
    });

    /// <summary>
    /// A new open copy: same fields, tags and checklist (unchecked), with copies of its subtasks.
    /// <paramref name="title"/> lets the UI name it (e.g. "… (copy)" in the user's language).
    /// </summary>
    public TaskItem Duplicate(string id, string? title = null) => Execute("task.duplicate", batch =>
    {
        var source = RequireWritable(id);
        var sort = _store.MaxSortOrder();
        var copy = CopyOf(source, title ?? source.Title, source.ParentId, batch.Now, ++sort);

        var errors = new List<ValidationError>();
        TaskRules.CheckFields(copy, _store, errors);
        TaskRules.ThrowIfAny(errors);
        batch.Add(new TaskWrite(null, copy));

        foreach (var subtask in _store.GetSubtasks(id).Where(s => !s.IsDeleted))
        {
            batch.Add(new TaskWrite(null, CopyOf(subtask, subtask.Title, copy.Id, batch.Now, ++sort)));
        }

        return copy;
    });

    /// <summary>Makes the task a subtask of <paramref name="parentId"/>, or a top-level task when null.</summary>
    public TaskItem MoveTo(string id, string? parentId) => Execute("task.move", batch =>
    {
        var current = RequireWritable(id);
        var moved = current with { ParentId = parentId };
        var errors = new List<ValidationError>();
        TaskRules.CheckParent(moved, _store, errors);
        TaskRules.ThrowIfAny(errors);
        return batch.Update(current, moved);
    });

    // Checklist commands ----------------------------------------------------------------------------

    public ChecklistItem AddChecklistItem(string taskId, string text) => Execute("checklist.add", batch =>
    {
        var current = RequireWritable(taskId);
        var item = new ChecklistItem(Ids.New(_time), text?.Trim() ?? "", false, current.Checklist.Count == 0 ? 1 : current.Checklist.Max(c => c.Sort) + 1);
        var updated = current with { Checklist = [.. current.Checklist, item] };
        CheckChecklist(updated);
        batch.Update(current, updated);
        return item;
    });

    public TaskItem SetChecklistItemDone(string taskId, string itemId, bool isDone) =>
        ChangeChecklistItem("checklist.check", taskId, itemId, item => item with { IsDone = isDone });

    public TaskItem RenameChecklistItem(string taskId, string itemId, string text) =>
        ChangeChecklistItem("checklist.rename", taskId, itemId, item => item with { Text = text?.Trim() ?? "" });

    public TaskItem RemoveChecklistItem(string taskId, string itemId) => Execute("checklist.remove", batch =>
    {
        var current = RequireWritable(taskId);
        RequireChecklistItem(current, itemId);
        return batch.Update(current, current with { Checklist = [.. current.Checklist.Where(c => c.Id != itemId)] });
    });

    // Taxonomy commands -----------------------------------------------------------------------------

    public TaxonomyItem CreateTaxonomy(TaxonomyKind kind, string name, string? color = null, bool isDemo = false) => Execute($"{Prefix(kind)}.create", batch =>
    {
        var existing = _store.GetTaxonomy(kind);
        var item = new TaxonomyItem
        {
            Kind = kind,
            Id = Ids.New(_time),
            Name = name?.Trim() ?? "",
            Color = Blank(color),
            Sort = existing.Count == 0 ? 1 : existing.Max(t => t.Sort) + 1,
            IsDemo = isDemo,
            CreatedAt = batch.Now,
            UpdatedAt = batch.Now,
        };
        var errors = new List<ValidationError>();
        TaskRules.CheckTaxonomyName(item, _store, errors);
        TaskRules.ThrowIfAny(errors);
        batch.Add(new TaxonomyWrite(null, item));
        return item;
    });

    /// <summary>The live tag with this name (any letter case), created if there is none (quick tagging).</summary>
    public TaxonomyItem GetOrCreateTag(string name)
    {
        var trimmed = name?.Trim() ?? "";
        return _store.GetTaxonomy(TaxonomyKind.Tag)
                   .FirstOrDefault(t => !t.IsDeleted && string.Equals(t.Name, trimmed, StringComparison.CurrentCultureIgnoreCase))
               ?? CreateTaxonomy(TaxonomyKind.Tag, trimmed);
    }

    public TaxonomyItem RenameTaxonomy(TaxonomyKind kind, string id, string name) =>
        ChangeTaxonomy(kind, id, "rename", item => item with { Name = name?.Trim() ?? "" });

    public TaxonomyItem SetTaxonomyColor(TaxonomyKind kind, string id, string? color) =>
        ChangeTaxonomy(kind, id, "color", item => item with { Color = Blank(color) });

    /// <summary>Deletes the category, project or tag and removes it from every task, in one undoable step.</summary>
    public TaxonomyItem DeleteTaxonomy(TaxonomyKind kind, string id) => Execute($"{Prefix(kind)}.delete", batch =>
    {
        var current = RequireTaxonomy(kind, id);
        if (current.IsDeleted)
        {
            return current;
        }

        foreach (var task in _store.TasksUsing(kind, id))
        {
            var detached = kind switch
            {
                TaxonomyKind.Category => task with { CategoryId = null },
                TaxonomyKind.Project => task with { ProjectId = null },
                _ => task with { Tags = [.. task.Tags.Where(t => t != id)] },
            };
            batch.Update(task, detached);
        }

        return batch.Update(current, current with { DeletedAt = batch.Now });
    });

    // Sample data -----------------------------------------------------------------------------------

    /// <summary>True while sample tasks or names from onboarding are still there (trash included).</summary>
    public bool HasDemoData =>
        _store.QueryTasks(TaskScope.All).Any(t => t.IsDemo) || Enum.GetValues<TaxonomyKind>().Any(k => _store.GetTaxonomy(k).Any(t => t.IsDemo));

    /// <summary>
    /// Removes every sample task, category, project and tag for good, in one undoable step. Real tasks
    /// are never removed: one that used a sample name or sat under a sample task only loses that link.
    /// Returns how many sample tasks were removed.
    /// </summary>
    public int RemoveDemoData() => Execute("demo.remove", batch =>
    {
        var all = _store.QueryTasks(TaskScope.All);
        var demo = all.Where(t => t.IsDemo).ToDictionary(t => t.Id, StringComparer.Ordinal);
        var names = Enum.GetValues<TaxonomyKind>().SelectMany(_store.GetTaxonomy).Where(t => t.IsDemo).ToList();
        var nameIds = names.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var task in all.Where(t => !t.IsDemo))
        {
            batch.Update(task, task with
            {
                ParentId = task.ParentId is { } parent && demo.ContainsKey(parent) ? null : task.ParentId,
                CategoryId = task.CategoryId is { } category && nameIds.Contains(category) ? null : task.CategoryId,
                ProjectId = task.ProjectId is { } project && nameIds.Contains(project) ? null : task.ProjectId,
                Tags = [.. task.Tags.Where(tag => !nameIds.Contains(tag))],
            });
        }

        // Subtasks before their parents, so undo puts the parents back first.
        int Depth(TaskItem task) => task.ParentId is { } parent && demo.TryGetValue(parent, out var up) ? 1 + Depth(up) : 0;
        foreach (var task in demo.Values.OrderByDescending(Depth))
        {
            batch.Add(new TaskWrite(task, null));
        }

        foreach (var name in names)
        {
            batch.Add(new TaxonomyWrite(name, null));
        }

        return demo.Count;
    });

    // Undo ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reverts the last action of this session exactly (every entity it touched goes back to its
    /// previous snapshot; undoing a create removes the row). Returns the command name that was undone,
    /// or null when there is nothing to undo.
    /// </summary>
    /// <exception cref="UndoConflictException">Something changed the entities since; the action is dropped from the stack.</exception>
    public string? Undo()
    {
        List<IDomainEvent> events;
        string command;
        lock (_gate)
        {
            if (_undo.Count == 0)
            {
                return null;
            }

            var entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            command = entry.Command;

            foreach (var write in entry.Writes)
            {
                if (!MatchesStore(write))
                {
                    RaiseUndoChanged();
                    throw new UndoConflictException(write.EntityId);
                }
            }

            var now = Ids.Now(_time);
            var batchId = Ids.New(_time);
            var writes = entry.Writes.Reverse().Select(w => w.Invert()).ToList();
            _store.Commit(new ChangeSet(writes, Log($"undo:{command}", writes, now, batchId)));
            events = EventsFor(writes, batchId, ChangeOrigin.Undo);
        }

        Publish(events);
        RaiseUndoChanged();
        return command;
    }

    /// <summary>Forgets the undo history (e.g. after a restore from backup replaced the database).</summary>
    public void ClearUndo()
    {
        lock (_gate)
        {
            _undo.Clear();
        }

        RaiseUndoChanged();
    }

    // Plumbing --------------------------------------------------------------------------------------

    private T Execute<T>(string command, Func<Batch, T> build)
    {
        T result;
        List<IDomainEvent> events;
        lock (_gate)
        {
            var batch = new Batch(Ids.Now(_time), Ids.New(_time));
            result = build(batch);
            if (batch.Writes.Count == 0)
            {
                return result;
            }

            _store.Commit(new ChangeSet(batch.Writes, Log(command, batch.Writes, batch.Now, batch.Id)));
            _undo.Add(new UndoEntry(command, batch.Writes, batch.Id));
            if (_undo.Count > UndoDepth)
            {
                _undo.RemoveAt(0);
            }

            events = EventsFor(batch.Writes, batch.Id, ChangeOrigin.Command);
        }

        Publish(events);
        RaiseUndoChanged();
        return result;
    }

    private void Publish(List<IDomainEvent> events)
    {
        foreach (var domainEvent in events)
        {
            _bus.Publish(domainEvent);
        }
    }

    private void RaiseUndoChanged() => UndoChanged?.Invoke(this, EventArgs.Empty);

    private List<ActivityEntry> Log(string command, IReadOnlyList<EntityWrite> writes, DateTimeOffset at, string batchId) =>
        [.. writes.Select(w => new ActivityEntry(
            Ids.New(_time), at, Actor, command, w.EntityType, w.EntityId, BeforeJson(w), AfterJson(w), batchId))];

    private static string? BeforeJson(EntityWrite write) => write switch
    {
        TaskWrite t => Snapshots.Json(t.Before),
        TaxonomyWrite x => Snapshots.Json(x.Before),
        _ => null,
    };

    private static string? AfterJson(EntityWrite write) => write switch
    {
        TaskWrite t => Snapshots.Json(t.After),
        TaxonomyWrite x => Snapshots.Json(x.After),
        _ => null,
    };

    private bool MatchesStore(EntityWrite write) => write switch
    {
        TaskWrite t => Snapshots.Same(_store.GetTask(t.EntityId), t.After),
        TaxonomyWrite x => Snapshots.Same(_store.GetTaxonomyItem((x.After ?? x.Before)!.Kind, x.EntityId), x.After),
        _ => false,
    };

    private static List<IDomainEvent> EventsFor(IReadOnlyList<EntityWrite> writes, string batchId, ChangeOrigin origin)
    {
        var events = new List<IDomainEvent>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case TaskWrite { Before: null, After: { } created }:
                    events.Add(new TaskCreated(created, batchId, origin));
                    break;
                case TaskWrite { Before: { } removed, After: null }:
                    events.Add(new TaskRemoved(removed, batchId, origin));
                    break;
                case TaskWrite { Before: { } before, After: { } after }:
                    events.Add(new TaskChanged(before, after, batchId, origin));
                    if (before.StatusId != after.StatusId)
                    {
                        events.Add(new TaskStatusChanged(before, after, batchId, origin));
                    }

                    if (before.DueDate != after.DueDate || before.DueTime != after.DueTime)
                    {
                        events.Add(new TaskDueChanged(before, after, batchId, origin));
                    }

                    if (before.IsDeleted != after.IsDeleted)
                    {
                        events.Add(after.IsDeleted ? new TaskDeleted(before, after, batchId, origin) : new TaskRestored(before, after, batchId, origin));
                    }

                    if (before.IsArchived != after.IsArchived)
                    {
                        events.Add(after.IsArchived ? new TaskArchived(before, after, batchId, origin) : new TaskUnarchived(before, after, batchId, origin));
                    }

                    break;
                case TaxonomyWrite taxonomy:
                    events.Add(new TaxonomyChanged(taxonomy.Before, taxonomy.After, batchId, origin));
                    break;
            }
        }

        return events;
    }

    private TaskItem Require(string id) => _store.GetTask(id) ?? throw new EntityNotFoundException("task", id);

    /// <summary>A task that commands may change: exists and is not in the trash.</summary>
    private TaskItem RequireWritable(string id)
    {
        var task = Require(id);
        if (task.IsDeleted)
        {
            throw new DomainValidationException([new(nameof(TaskItem.DeletedAt), ValidationCodes.ReadOnly)]);
        }

        return task;
    }

    private TaxonomyItem RequireTaxonomy(TaxonomyKind kind, string id) =>
        _store.GetTaxonomyItem(kind, id) ?? throw new EntityNotFoundException(Prefix(kind), id);

    private static ChecklistItem RequireChecklistItem(TaskItem task, string itemId) =>
        task.Checklist.FirstOrDefault(c => c.Id == itemId) ?? throw new EntityNotFoundException("checklist_item", itemId);

    private TaskItem ChangeChecklistItem(string command, string taskId, string itemId, Func<ChecklistItem, ChecklistItem> change) =>
        Execute(command, batch =>
        {
            var current = RequireWritable(taskId);
            var item = RequireChecklistItem(current, itemId);
            var updated = current with { Checklist = [.. current.Checklist.Select(c => c.Id == itemId ? change(item) : c)] };
            CheckChecklist(updated);
            return batch.Update(current, updated);
        });

    private void CheckChecklist(TaskItem task)
    {
        var errors = new List<ValidationError>();
        TaskRules.CheckFields(task, _store, errors);
        TaskRules.ThrowIfAny([.. errors.Where(e => e.Field == nameof(TaskItem.Checklist))]);
    }

    private TaxonomyItem ChangeTaxonomy(TaxonomyKind kind, string id, string action, Func<TaxonomyItem, TaxonomyItem> change) =>
        Execute($"{Prefix(kind)}.{action}", batch =>
        {
            var current = RequireTaxonomy(kind, id);
            if (current.IsDeleted)
            {
                throw new DomainValidationException([new(nameof(TaxonomyItem.DeletedAt), ValidationCodes.ReadOnly)]);
            }

            var updated = change(current);
            var errors = new List<ValidationError>();
            TaskRules.CheckTaxonomyName(updated, _store, errors);
            TaskRules.ThrowIfAny(errors);
            return batch.Update(current, updated);
        });

    private bool IsDone(string statusId) => _store.GetStatus(statusId)?.Kind == TaskStatusKind.Done;

    /// <summary>Status plus completion time: set when entering a done status, cleared when leaving it.</summary>
    private static TaskItem WithStatus(TaskItem task, TaskStatusInfo status, DateTimeOffset now) => task with
    {
        StatusId = status.Id,
        CompletedAt = status.Kind == TaskStatusKind.Done ? task.CompletedAt ?? now : null,
    };

    private TaskItem CopyOf(TaskItem source, string title, string? parentId, DateTimeOffset now, double sort) => Normalize(source with
    {
        Id = Ids.New(_time),
        Title = title,
        ParentId = parentId,
        StatusId = SystemStatuses.Todo,
        IsPinned = false,
        SortOrder = sort,
        Checklist = [.. source.Checklist.Select(c => c with { Id = Ids.New(_time), IsDone = false })],
        CompletedAt = null,
        ArchivedAt = null,
        DeletedAt = null,
        IsDemo = false,
        CreatedAt = now,
        UpdatedAt = now,
    });

    /// <summary>Trims texts, turns blank optional texts into null, sorts and de-duplicates tags, keeps due time to the minute.</summary>
    private static TaskItem Normalize(TaskItem task) => task with
    {
        Title = task.Title?.Trim() ?? "",
        DueTime = task.DueTime is { } time ? new TimeOnly(time.Hour, time.Minute) : null,
        Description = Blank(task.Description),
        Notes = Blank(task.Notes),
        ExternalRef = Blank(task.ExternalRef),
        ExternalUrl = Blank(task.ExternalUrl),
        Requester = Blank(task.Requester),
        Responsible = Blank(task.Responsible),
        Tags = [.. (task.Tags ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        Checklist = task.Checklist ?? [],
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Prefix(TaxonomyKind kind) => kind.ToString().ToLowerInvariant();

    private sealed record UndoEntry(string Command, IReadOnlyList<EntityWrite> Writes, string BatchId);

    /// <summary>The writes one command collects before they are committed together.</summary>
    private sealed class Batch(DateTimeOffset now, string id)
    {
        public DateTimeOffset Now { get; } = now;

        public string Id { get; } = id;

        public List<EntityWrite> Writes { get; } = [];

        public void Add(EntityWrite write) => Writes.Add(write);

        /// <summary>Records the change unless nothing but the timestamp would differ. Returns what is now current.</summary>
        public TaskItem Update(TaskItem before, TaskItem after)
        {
            if (Snapshots.Same(before, after with { UpdatedAt = before.UpdatedAt }))
            {
                return before;
            }

            var stamped = after with { UpdatedAt = Now };
            Writes.Add(new TaskWrite(before, stamped));
            return stamped;
        }

        public TaxonomyItem Update(TaxonomyItem before, TaxonomyItem after)
        {
            if (Snapshots.Same(before, after with { UpdatedAt = before.UpdatedAt }))
            {
                return before;
            }

            var stamped = after with { UpdatedAt = Now };
            Writes.Add(new TaxonomyWrite(before, stamped));
            return stamped;
        }
    }
}

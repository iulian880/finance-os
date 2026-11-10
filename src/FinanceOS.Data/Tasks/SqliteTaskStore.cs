using System.Globalization;
using Dapper;
using FinanceOS.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace FinanceOS.Data.Tasks;

/// <summary>
/// <see cref="ITaskStore"/> over the SQLite schema of migration 001/002. A task is read and written as
/// one aggregate (row + tags + checklist). Columns the domain does not model yet (recurrence, workflow,
/// actual minutes, custom fields) are never touched by an update.
/// </summary>
public sealed class SqliteTaskStore(SqliteDatabase database) : ITaskStore
{
    private const string MomentFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private const string TaskColumns = """
        id AS Id, title AS Title, description AS Description, notes AS Notes, status_id AS StatusId,
        priority AS Priority, category_id AS CategoryId, project_id AS ProjectId, parent_task_id AS ParentId,
        due_date AS DueDate, due_time AS DueTime, start_date AS StartDate, estimate_min AS EstimateMinutes,
        external_ref AS ExternalRef, external_url AS ExternalUrl, requester AS Requester, responsible AS Responsible,
        is_pinned AS IsPinned, sort_order AS SortOrder, completed_at AS CompletedAt, archived_at AS ArchivedAt,
        deleted_at AS DeletedAt, is_demo AS IsDemo, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public TaskItem? GetTask(string id) => LoadTasks("WHERE id = @id", new { id }).SingleOrDefault();

    public IReadOnlyList<TaskItem> GetSubtasks(string parentId) =>
        LoadTasks("WHERE parent_task_id = @parentId ORDER BY sort_order, created_at", new { parentId });

    public IReadOnlyList<TaskItem> QueryTasks(TaskScope scope)
    {
        var filter = scope switch
        {
            TaskScope.Open => "WHERE deleted_at IS NULL AND archived_at IS NULL",
            TaskScope.Archived => "WHERE deleted_at IS NULL AND archived_at IS NOT NULL",
            TaskScope.Trash => "WHERE deleted_at IS NOT NULL",
            _ => "",
        };
        return LoadTasks($"{filter} ORDER BY is_pinned DESC, sort_order, created_at", null);
    }

    public IReadOnlyList<TaskItem> TasksUsing(TaxonomyKind kind, string id) => kind switch
    {
        TaxonomyKind.Category => LoadTasks("WHERE category_id = @id", new { id }),
        TaxonomyKind.Project => LoadTasks("WHERE project_id = @id", new { id }),
        _ => LoadTasks("WHERE id IN (SELECT task_id FROM task_tags WHERE tag_id = @id)", new { id }),
    };

    public double MaxSortOrder()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<double?>("SELECT max(sort_order) FROM tasks;") ?? 0;
    }

    public TaskStatusInfo? GetStatus(string id) => GetStatuses().FirstOrDefault(s => s.Id == id);

    public IReadOnlyList<TaskStatusInfo> GetStatuses()
    {
        using var connection = database.Open();
        return connection.Query<StatusRow>(
                "SELECT id AS Id, name AS Name, kind AS Kind, sort AS Sort, is_system AS IsSystem FROM task_statuses WHERE deleted_at IS NULL ORDER BY sort;")
            .Select(r => new TaskStatusInfo(r.Id, r.Name, Enum.Parse<TaskStatusKind>(r.Kind, ignoreCase: true), (int)r.Sort, r.IsSystem != 0))
            .ToList();
    }

    public TaxonomyItem? GetTaxonomyItem(TaxonomyKind kind, string id) =>
        LoadTaxonomy(kind, "WHERE id = @id", new { id }).SingleOrDefault();

    public IReadOnlyList<TaxonomyItem> GetTaxonomy(TaxonomyKind kind) => LoadTaxonomy(kind, "ORDER BY sort, name", null);

    public void Commit(ChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var write in changes.Writes)
        {
            switch (write)
            {
                case TaskWrite { After: { } task }:
                    SaveTask(connection, transaction, task);
                    break;
                case TaskWrite { Before: { } removed }:
                    connection.Execute("DELETE FROM tasks WHERE id = @Id;", new { removed.Id }, transaction);
                    break;
                case TaxonomyWrite { After: { } item }:
                    SaveTaxonomy(connection, transaction, item);
                    break;
                case TaxonomyWrite { Before: { } removed }:
                    connection.Execute($"DELETE FROM {Table(removed.Kind)} WHERE id = @Id;", new { removed.Id }, transaction);
                    break;
            }
        }

        foreach (var entry in changes.Activity)
        {
            connection.Execute(
                """
                INSERT INTO activity_log (id, at, actor, command, entity_type, entity_id, before, after, batch_id)
                VALUES (@Id, @At, @Actor, @Command, @EntityType, @EntityId, @Before, @After, @BatchId);
                """,
                new { entry.Id, At = Moment(entry.At), entry.Actor, entry.Command, entry.EntityType, entry.EntityId, entry.Before, entry.After, entry.BatchId },
                transaction);
        }

        transaction.Commit();
    }

    private static void SaveTask(SqliteConnection connection, SqliteTransaction transaction, TaskItem task)
    {
        connection.Execute(
            """
            INSERT INTO tasks (id, title, description, notes, status_id, priority, category_id, project_id, parent_task_id,
                               due_date, due_time, start_date, estimate_min, external_ref, external_url, requester, responsible,
                               is_pinned, sort_order, completed_at, archived_at, deleted_at, is_demo, created_at, updated_at)
            VALUES (@Id, @Title, @Description, @Notes, @StatusId, @Priority, @CategoryId, @ProjectId, @ParentId,
                    @DueDate, @DueTime, @StartDate, @EstimateMinutes, @ExternalRef, @ExternalUrl, @Requester, @Responsible,
                    @IsPinned, @SortOrder, @CompletedAt, @ArchivedAt, @DeletedAt, @IsDemo, @CreatedAt, @UpdatedAt)
            ON CONFLICT (id) DO UPDATE SET
                title = excluded.title, description = excluded.description, notes = excluded.notes,
                status_id = excluded.status_id, priority = excluded.priority, category_id = excluded.category_id,
                project_id = excluded.project_id, parent_task_id = excluded.parent_task_id, due_date = excluded.due_date,
                due_time = excluded.due_time, start_date = excluded.start_date, estimate_min = excluded.estimate_min,
                external_ref = excluded.external_ref, external_url = excluded.external_url, requester = excluded.requester,
                responsible = excluded.responsible, is_pinned = excluded.is_pinned, sort_order = excluded.sort_order,
                completed_at = excluded.completed_at, archived_at = excluded.archived_at, deleted_at = excluded.deleted_at,
                is_demo = excluded.is_demo, created_at = excluded.created_at, updated_at = excluded.updated_at;
            """,
            new
            {
                task.Id,
                task.Title,
                task.Description,
                task.Notes,
                task.StatusId,
                Priority = (int)task.Priority,
                task.CategoryId,
                task.ProjectId,
                task.ParentId,
                DueDate = task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DueTime = task.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
                StartDate = task.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                task.EstimateMinutes,
                task.ExternalRef,
                task.ExternalUrl,
                task.Requester,
                task.Responsible,
                IsPinned = task.IsPinned ? 1 : 0,
                task.SortOrder,
                CompletedAt = Moment(task.CompletedAt),
                ArchivedAt = Moment(task.ArchivedAt),
                DeletedAt = Moment(task.DeletedAt),
                IsDemo = task.IsDemo ? 1 : 0,
                CreatedAt = Moment(task.CreatedAt),
                UpdatedAt = Moment(task.UpdatedAt),
            },
            transaction);

        connection.Execute("DELETE FROM task_tags WHERE task_id = @Id;", new { task.Id }, transaction);
        foreach (var tag in task.Tags)
        {
            connection.Execute("INSERT INTO task_tags (task_id, tag_id) VALUES (@Id, @tag);", new { task.Id, tag }, transaction);
        }

        connection.Execute(
            "DELETE FROM checklist_items WHERE task_id = @Id AND id NOT IN @Keep;",
            new { task.Id, Keep = task.Checklist.Select(c => c.Id).ToList() },
            transaction);
        foreach (var item in task.Checklist)
        {
            connection.Execute(
                """
                INSERT INTO checklist_items (id, task_id, text, is_done, sort) VALUES (@Id, @TaskId, @Text, @IsDone, @Sort)
                ON CONFLICT (id) DO UPDATE SET text = excluded.text, is_done = excluded.is_done, sort = excluded.sort,
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
                """,
                new { item.Id, TaskId = task.Id, item.Text, IsDone = item.IsDone ? 1 : 0, item.Sort },
                transaction);
        }
    }

    private static void SaveTaxonomy(SqliteConnection connection, SqliteTransaction transaction, TaxonomyItem item) =>
        connection.Execute(
            $"""
            INSERT INTO {Table(item.Kind)} (id, name, color, sort, is_demo, created_at, updated_at, deleted_at)
            VALUES (@Id, @Name, @Color, @Sort, @IsDemo, @CreatedAt, @UpdatedAt, @DeletedAt)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, color = excluded.color, sort = excluded.sort,
                is_demo = excluded.is_demo, created_at = excluded.created_at, updated_at = excluded.updated_at,
                deleted_at = excluded.deleted_at;
            """,
            new
            {
                item.Id,
                item.Name,
                item.Color,
                item.Sort,
                IsDemo = item.IsDemo ? 1 : 0,
                CreatedAt = Moment(item.CreatedAt),
                UpdatedAt = Moment(item.UpdatedAt),
                DeletedAt = Moment(item.DeletedAt),
            },
            transaction);

    private List<TaskItem> LoadTasks(string clause, object? parameters)
    {
        using var connection = database.Open();
        var rows = connection.Query<TaskRow>($"SELECT {TaskColumns} FROM tasks {clause};", parameters).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        // Tags and checklists of exactly these tasks, in two queries (not one per task).
        var ids = rows.Select(r => r.Id).ToList();
        var tags = connection.Query<(string TaskId, string TagId)>(
                "SELECT task_id, tag_id FROM task_tags WHERE task_id IN @ids ORDER BY tag_id;", new { ids })
            .ToLookup(t => t.TaskId, t => t.TagId);
        var checklist = connection.Query<ChecklistRow>(
                "SELECT id AS Id, task_id AS TaskId, text AS Text, is_done AS IsDone, sort AS Sort FROM checklist_items WHERE task_id IN @ids ORDER BY sort, id;",
                new { ids })
            .ToLookup(c => c.TaskId, c => new ChecklistItem(c.Id, c.Text, c.IsDone != 0, c.Sort));

        return [.. rows.Select(r => r.ToTask([.. tags[r.Id].Order(StringComparer.Ordinal)], [.. checklist[r.Id]]))];
    }

    private List<TaxonomyItem> LoadTaxonomy(TaxonomyKind kind, string clause, object? parameters)
    {
        using var connection = database.Open();
        return connection.Query<TaxonomyRow>(
                $"""
                SELECT id AS Id, name AS Name, color AS Color, sort AS Sort, is_demo AS IsDemo,
                       created_at AS CreatedAt, updated_at AS UpdatedAt, deleted_at AS DeletedAt
                FROM {Table(kind)} {clause};
                """,
                parameters)
            .Select(r => new TaxonomyItem
            {
                Kind = kind,
                Id = r.Id,
                Name = r.Name,
                Color = r.Color,
                Sort = (int)r.Sort,
                IsDemo = r.IsDemo != 0,
                CreatedAt = ParseMoment(r.CreatedAt)!.Value,
                UpdatedAt = ParseMoment(r.UpdatedAt)!.Value,
                DeletedAt = ParseMoment(r.DeletedAt),
            })
            .ToList();
    }

    private static string Table(TaxonomyKind kind) => kind switch
    {
        TaxonomyKind.Category => "categories",
        TaxonomyKind.Project => "projects",
        TaxonomyKind.Tag => "tags",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string? Moment(DateTimeOffset? at) => at?.UtcDateTime.ToString(MomentFormat, CultureInfo.InvariantCulture);

    internal static DateTimeOffset? ParseMoment(string? text) => text is null
        ? null
        : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed class StatusRow
    {
        public string Id { get; init; } = "";

        public string Name { get; init; } = "";

        public string Kind { get; init; } = "";

        public long Sort { get; init; }

        public long IsSystem { get; init; }
    }

    private sealed class ChecklistRow
    {
        public string Id { get; init; } = "";

        public string TaskId { get; init; } = "";

        public string Text { get; init; } = "";

        public long IsDone { get; init; }

        public double Sort { get; init; }
    }

    private sealed class TaxonomyRow
    {
        public string Id { get; init; } = "";

        public string Name { get; init; } = "";

        public string? Color { get; init; }

        public long Sort { get; init; }

        public long IsDemo { get; init; }

        public string CreatedAt { get; init; } = "";

        public string UpdatedAt { get; init; } = "";

        public string? DeletedAt { get; init; }
    }

    private sealed class TaskRow
    {
        public string Id { get; init; } = "";

        public string Title { get; init; } = "";

        public string? Description { get; init; }

        public string? Notes { get; init; }

        public string StatusId { get; init; } = "";

        public long Priority { get; init; }

        public string? CategoryId { get; init; }

        public string? ProjectId { get; init; }

        public string? ParentId { get; init; }

        public string? DueDate { get; init; }

        public string? DueTime { get; init; }

        public string? StartDate { get; init; }

        public long? EstimateMinutes { get; init; }

        public string? ExternalRef { get; init; }

        public string? ExternalUrl { get; init; }

        public string? Requester { get; init; }

        public string? Responsible { get; init; }

        public long IsPinned { get; init; }

        public double SortOrder { get; init; }

        public string? CompletedAt { get; init; }

        public string? ArchivedAt { get; init; }

        public string? DeletedAt { get; init; }

        public long IsDemo { get; init; }

        public string CreatedAt { get; init; } = "";

        public string UpdatedAt { get; init; } = "";

        public TaskItem ToTask(IReadOnlyList<string> tags, IReadOnlyList<ChecklistItem> checklist) => new()
        {
            Id = Id,
            Title = Title,
            Description = Description,
            Notes = Notes,
            StatusId = StatusId,
            Priority = (Priority)Priority,
            CategoryId = CategoryId,
            ProjectId = ProjectId,
            ParentId = ParentId,
            DueDate = DueDate is null ? null : DateOnly.ParseExact(DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DueTime = DueTime is null ? null : TimeOnly.ParseExact(DueTime, "HH:mm", CultureInfo.InvariantCulture),
            StartDate = StartDate is null ? null : DateOnly.ParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            EstimateMinutes = EstimateMinutes is null ? null : (int)EstimateMinutes.Value,
            ExternalRef = ExternalRef,
            ExternalUrl = ExternalUrl,
            Requester = Requester,
            Responsible = Responsible,
            IsPinned = IsPinned != 0,
            SortOrder = SortOrder,
            Tags = tags,
            Checklist = checklist,
            CompletedAt = ParseMoment(CompletedAt),
            ArchivedAt = ParseMoment(ArchivedAt),
            DeletedAt = ParseMoment(DeletedAt),
            IsDemo = IsDemo != 0,
            CreatedAt = ParseMoment(CreatedAt)!.Value,
            UpdatedAt = ParseMoment(UpdatedAt)!.Value,
        };
    }
}

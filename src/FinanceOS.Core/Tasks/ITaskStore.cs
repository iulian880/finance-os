namespace FinanceOS.Core.Tasks;

/// <summary>Which tasks a query returns.</summary>
public enum TaskScope
{
    /// <summary>Not deleted and not archived: what lists and views work with.</summary>
    Open,

    /// <summary>Archived, not deleted.</summary>
    Archived,

    /// <summary>In the trash.</summary>
    Trash,

    All,
}

/// <summary>One entity going from <see cref="Before"/> to <see cref="After"/>; null = does not exist.</summary>
public abstract record EntityWrite
{
    public abstract string EntityType { get; }

    public abstract string EntityId { get; }

    /// <summary>The same change in the other direction (undo).</summary>
    public abstract EntityWrite Invert();
}

public sealed record TaskWrite(TaskItem? Before, TaskItem? After) : EntityWrite
{
    public override string EntityType => "task";

    public override string EntityId => (After ?? Before)!.Id;

    public override EntityWrite Invert() => new TaskWrite(After, Before);
}

public sealed record TaxonomyWrite(TaxonomyItem? Before, TaxonomyItem? After) : EntityWrite
{
    public override string EntityType => (After ?? Before)!.Kind.ToString().ToLowerInvariant();

    public override string EntityId => (After ?? Before)!.Id;

    public override EntityWrite Invert() => new TaxonomyWrite(After, Before);
}

/// <summary>One <c>activity_log</c> row. Before/after are JSON snapshots (<see cref="Common.Snapshots"/>).</summary>
public sealed record ActivityEntry(
    string Id, DateTimeOffset At, string Actor, string Command, string EntityType, string EntityId,
    string? Before, string? After, string BatchId);

/// <summary>Everything one command writes. The store applies it in a single transaction, in order.</summary>
public sealed record ChangeSet(IReadOnlyList<EntityWrite> Writes, IReadOnlyList<ActivityEntry> Activity);

/// <summary>
/// Persistence for tasks and taxonomies (implemented in FinanceOS.Data). Reads return deleted entities
/// too unless the method says otherwise; the service decides what is visible.
/// </summary>
public interface ITaskStore
{
    TaskItem? GetTask(string id);

    /// <summary>Subtasks of <paramref name="parentId"/>, deleted ones included, in sort order.</summary>
    IReadOnlyList<TaskItem> GetSubtasks(string parentId);

    /// <summary>Tasks in <paramref name="scope"/>, pinned first, then by sort order.</summary>
    IReadOnlyList<TaskItem> QueryTasks(TaskScope scope);

    /// <summary>Every task (any state) that refers to the category, project or tag.</summary>
    IReadOnlyList<TaskItem> TasksUsing(TaxonomyKind kind, string id);

    /// <summary>Highest task sort order, 0 when there are no tasks.</summary>
    double MaxSortOrder();

    TaskStatusInfo? GetStatus(string id);

    IReadOnlyList<TaskStatusInfo> GetStatuses();

    TaxonomyItem? GetTaxonomyItem(TaxonomyKind kind, string id);

    /// <summary>All items of a kind, deleted ones included.</summary>
    IReadOnlyList<TaxonomyItem> GetTaxonomy(TaxonomyKind kind);

    /// <summary>Applies the writes (null After = remove the row) and appends the activity rows, atomically.</summary>
    void Commit(ChangeSet changes);
}

namespace FinanceOS.Core.Tasks;

/// <summary>The fixed views of the task list (M7). Saved filters come in M18.</summary>
public enum TaskView
{
    /// <summary>Every open task (not done, not cancelled).</summary>
    All,

    /// <summary>Open, due today.</summary>
    Today,

    /// <summary>Open, due after today.</summary>
    Upcoming,

    /// <summary>Open, and the due moment has passed (an earlier day, or today at a time already gone).</summary>
    Overdue,

    /// <summary>Done or cancelled, most recently finished first.</summary>
    Completed,
}

public enum TaskSort
{
    /// <summary>Pinned, then overdue, then by due date, then by priority (Completed: most recently finished first).</summary>
    Smart,
    DueDate,
    Priority,
    Title,
    Created,
}

/// <summary>
/// Which tasks a view shows and in what order. Pure functions over task snapshots and the local "now"
/// (calendar dates are local, never converted), so they are tested without a database or a clock.
/// Archived and trashed tasks never appear in a view.
/// </summary>
public static class TaskViews
{
    public static IReadOnlyList<TaskItem> Apply(
        IEnumerable<TaskItem> tasks, TaskView view, TaskSort sort, IReadOnlyCollection<TaskStatusInfo> statuses, DateTime localNow)
    {
        var closed = ClosedStatusIds(statuses);
        var filtered = tasks.Where(t => In(t, view, closed, localNow));
        return [.. Sort(filtered, view, sort, localNow)];
    }

    /// <summary>How many tasks each view would show (for the counts on the view tabs).</summary>
    public static IReadOnlyDictionary<TaskView, int> Counts(IReadOnlyCollection<TaskItem> tasks, IReadOnlyCollection<TaskStatusInfo> statuses, DateTime localNow)
    {
        var closed = ClosedStatusIds(statuses);
        return Enum.GetValues<TaskView>().ToDictionary(v => v, v => tasks.Count(t => In(t, v, closed, localNow)));
    }

    public static bool IsOverdue(TaskItem task, DateTime localNow)
    {
        if (task.DueDate is not { } due)
        {
            return false;
        }

        var today = DateOnly.FromDateTime(localNow);
        return due < today || (due == today && task.DueTime is { } time && time < TimeOnly.FromDateTime(localNow));
    }

    public static bool In(TaskItem task, TaskView view, IReadOnlySet<string> closedStatusIds, DateTime localNow)
    {
        if (task.IsDeleted || task.IsArchived)
        {
            return false;
        }

        var isClosed = closedStatusIds.Contains(task.StatusId);
        var today = DateOnly.FromDateTime(localNow);
        return view switch
        {
            TaskView.All => !isClosed,
            TaskView.Today => !isClosed && task.DueDate == today,
            TaskView.Upcoming => !isClosed && task.DueDate > today,
            TaskView.Overdue => !isClosed && IsOverdue(task, localNow),
            TaskView.Completed => isClosed,
            _ => false,
        };
    }

    public static HashSet<string> ClosedStatusIds(IEnumerable<TaskStatusInfo> statuses) =>
        [.. statuses.Where(s => s.IsClosed).Select(s => s.Id)];

    private static IOrderedEnumerable<TaskItem> Sort(IEnumerable<TaskItem> tasks, TaskView view, TaskSort sort, DateTime localNow) => sort switch
    {
        TaskSort.Smart when view == TaskView.Completed => tasks
            .OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt)
            .ThenBy(t => t.SortOrder),
        TaskSort.Smart => tasks
            .OrderByDescending(t => t.IsPinned)
            .ThenByDescending(t => IsOverdue(t, localNow))
            .ThenBy(t => t.DueDate is null)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.DueTime is null)
            .ThenBy(t => t.DueTime)
            .ThenByDescending(t => t.Priority)
            .ThenBy(t => t.SortOrder),
        TaskSort.DueDate => tasks
            .OrderBy(t => t.DueDate is null)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.DueTime is null)
            .ThenBy(t => t.DueTime)
            .ThenByDescending(t => t.Priority)
            .ThenBy(t => t.SortOrder),
        TaskSort.Priority => tasks
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.DueDate is null)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.SortOrder),
        TaskSort.Title => tasks
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.SortOrder),
        _ => tasks
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.SortOrder),
    };
}

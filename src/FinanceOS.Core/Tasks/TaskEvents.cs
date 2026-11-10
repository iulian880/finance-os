using FinanceOS.Core.Events;

namespace FinanceOS.Core.Tasks;

/// <summary>Base for task events: the task before and after the change (null = did not exist / no longer exists).</summary>
public abstract record TaskEvent(TaskItem? Before, TaskItem? After, string BatchId, ChangeOrigin Origin) : IDomainEvent
{
    public string TaskId => (After ?? Before)!.Id;
}

public sealed record TaskCreated(TaskItem Task, string BatchId, ChangeOrigin Origin) : TaskEvent(null, Task, BatchId, Origin);

/// <summary>Any change to an existing task (raised alongside the more specific events below).</summary>
public sealed record TaskChanged(TaskItem Old, TaskItem New, string BatchId, ChangeOrigin Origin) : TaskEvent(Old, New, BatchId, Origin);

public sealed record TaskStatusChanged(TaskItem Old, TaskItem New, string BatchId, ChangeOrigin Origin) : TaskEvent(Old, New, BatchId, Origin);

/// <summary>Due date or due time changed (reminders relative to the due date must be rescheduled).</summary>
public sealed record TaskDueChanged(TaskItem Old, TaskItem New, string BatchId, ChangeOrigin Origin) : TaskEvent(Old, New, BatchId, Origin);

/// <summary>Moved to the trash.</summary>
public sealed record TaskDeleted(TaskItem Old, TaskItem New, string BatchId, ChangeOrigin Origin) : TaskEvent(Old, New, BatchId, Origin);

/// <summary>Brought back from the trash.</summary>
public sealed record TaskRestored(TaskItem Old, TaskItem New, string BatchId, ChangeOrigin Origin) : TaskEvent(Old, New, BatchId, Origin);

public sealed record TaskArchived(TaskItem Old, TaskItem New, string BatchId, ChangeOrigin Origin) : TaskEvent(Old, New, BatchId, Origin);

public sealed record TaskUnarchived(TaskItem Old, TaskItem New, string BatchId, ChangeOrigin Origin) : TaskEvent(Old, New, BatchId, Origin);

/// <summary>The row is gone (only undoing a create removes a task for good).</summary>
public sealed record TaskRemoved(TaskItem Task, string BatchId, ChangeOrigin Origin) : TaskEvent(Task, null, BatchId, Origin);

/// <summary>A category, project or tag was created, changed, deleted or removed.</summary>
public sealed record TaxonomyChanged(TaxonomyItem? Before, TaxonomyItem? After, string BatchId, ChangeOrigin Origin) : IDomainEvent
{
    public TaxonomyItem Item => (After ?? Before)!;
}

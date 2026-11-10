using System.Text.Json.Serialization;

namespace FinanceOS.Core.Tasks;

/// <summary>Task priority (stored as 0–4). <see cref="None"/> is the default.</summary>
public enum Priority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>What a status means, whatever its name. Custom statuses (M19) map onto one of these.</summary>
public enum TaskStatusKind
{
    Todo,
    Active,
    Waiting,
    Blocked,
    Done,
    Cancelled,
}

/// <param name="Name">Display default; the UI shows system statuses by id in the chosen language.</param>
public sealed record TaskStatusInfo(string Id, string Name, TaskStatusKind Kind, int Sort, bool IsSystem)
{
    /// <summary>Done or cancelled: nothing left to do.</summary>
    public bool IsClosed => Kind is TaskStatusKind.Done or TaskStatusKind.Cancelled;
}

/// <summary>The six system statuses created by the first migration (one per kind).</summary>
public static class SystemStatuses
{
    public const string Todo = "status-todo";
    public const string Active = "status-active";
    public const string Waiting = "status-waiting";
    public const string Blocked = "status-blocked";
    public const string Done = "status-done";
    public const string Cancelled = "status-cancelled";
}

public sealed record ChecklistItem(string Id, string Text, bool IsDone, double Sort);

/// <summary>
/// A task with everything that belongs to it (tags, checklist): the unit that commands change, the log
/// records and undo restores. Immutable; commands produce a new snapshot. Times are UTC, truncated to
/// milliseconds (what the database keeps). Due and start dates are local calendar dates, never converted.
/// </summary>
public sealed record TaskItem
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? Notes { get; init; }

    public string StatusId { get; init; } = SystemStatuses.Todo;

    public Priority Priority { get; init; }

    public string? CategoryId { get; init; }

    public string? ProjectId { get; init; }

    /// <summary>Set for a subtask. Subtasks are one level deep.</summary>
    public string? ParentId { get; init; }

    public DateOnly? DueDate { get; init; }

    /// <summary>Only with a <see cref="DueDate"/>.</summary>
    public TimeOnly? DueTime { get; init; }

    public DateOnly? StartDate { get; init; }

    public int? EstimateMinutes { get; init; }

    /// <summary>Reference in another system, e.g. an invoice or ticket number.</summary>
    public string? ExternalRef { get; init; }

    /// <summary>http(s) link only.</summary>
    public string? ExternalUrl { get; init; }

    public string? Requester { get; init; }

    public string? Responsible { get; init; }

    public bool IsPinned { get; init; }

    public double SortOrder { get; init; }

    /// <summary>Tag ids, sorted.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Checklist, in display order.</summary>
    public IReadOnlyList<ChecklistItem> Checklist { get; init; } = [];

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>In the trash since this moment (soft delete).</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    public bool IsDemo { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    public bool IsDeleted => DeletedAt is not null;

    [JsonIgnore]
    public bool IsArchived => ArchivedAt is not null;
}

/// <summary>What the user fills in to create a task. Only <see cref="Title"/> is required.</summary>
public sealed record TaskDraft(string Title)
{
    public string? Description { get; init; }

    public string? Notes { get; init; }

    public string StatusId { get; init; } = SystemStatuses.Todo;

    public Priority Priority { get; init; }

    public string? CategoryId { get; init; }

    public string? ProjectId { get; init; }

    public string? ParentId { get; init; }

    public DateOnly? DueDate { get; init; }

    public TimeOnly? DueTime { get; init; }

    public DateOnly? StartDate { get; init; }

    public int? EstimateMinutes { get; init; }

    public string? ExternalRef { get; init; }

    public string? ExternalUrl { get; init; }

    public string? Requester { get; init; }

    public string? Responsible { get; init; }

    public bool IsPinned { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Checklist item texts, in order.</summary>
    public IReadOnlyList<string> Checklist { get; init; } = [];

    /// <summary>Sample data (onboarding): removed together by <see cref="TaskService.RemoveDemoData"/>.</summary>
    public bool IsDemo { get; init; }
}

public enum TaxonomyKind
{
    Category,
    Project,
    Tag,
}

/// <summary>A category, project or tag: a name the user groups tasks by. Projects get more fields in later milestones.</summary>
public sealed record TaxonomyItem
{
    public required TaxonomyKind Kind { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>"#RRGGBB" or null.</summary>
    public string? Color { get; init; }

    public int Sort { get; init; }

    public bool IsDemo { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    [JsonIgnore]
    public bool IsDeleted => DeletedAt is not null;
}

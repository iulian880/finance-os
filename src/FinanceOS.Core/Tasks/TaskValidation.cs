namespace FinanceOS.Core.Tasks;

/// <summary>
/// Why a command was refused. <see cref="Code"/> is stable (the UI turns it into a sentence in the
/// chosen language); <see cref="Field"/> names the property, so the form can mark the right input.
/// </summary>
public sealed record ValidationError(string Field, string Code);

/// <summary>Stable codes for <see cref="ValidationError.Code"/>.</summary>
public static class ValidationCodes
{
    public const string Required = "required";
    public const string TooLong = "too_long";
    public const string NotFound = "not_found";
    public const string Deleted = "deleted";
    public const string OutOfRange = "out_of_range";
    public const string TimeWithoutDate = "time_without_date";
    public const string StartAfterDue = "start_after_due";
    public const string InvalidUrl = "invalid_url";
    public const string InvalidColor = "invalid_color";
    public const string Duplicate = "duplicate";
    public const string SelfParent = "self_parent";
    public const string NestedSubtask = "nested_subtask";
    public const string HasSubtasks = "has_subtasks";
    public const string ParentInTrash = "parent_in_trash";
    public const string ReadOnly = "read_only";
}

/// <summary>A command was refused; nothing was written.</summary>
public sealed class DomainValidationException(IReadOnlyList<ValidationError> errors)
    : Exception("Validation failed: " + string.Join(", ", errors.Select(e => $"{e.Field}:{e.Code}")))
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors;

    public bool Has(string field, string code) => Errors.Any(e => e.Field == field && e.Code == code);
}

/// <summary>The entity does not exist (or is not visible to this command).</summary>
public sealed class EntityNotFoundException(string entityType, string id)
    : Exception($"{entityType} '{id}' was not found.")
{
    public string EntityType { get; } = entityType;

    public string Id { get; } = id;
}

/// <summary>
/// Undo refused: the entity changed after the command being undone (e.g. by an automation), so
/// restoring the old snapshot would silently throw that change away.
/// </summary>
public sealed class UndoConflictException(string entityId)
    : Exception($"'{entityId}' changed after this action; it cannot be undone safely.")
{
    public string EntityId { get; } = entityId;
}

/// <summary>Field rules shared by create and edit.</summary>
internal static class TaskRules
{
    public const int TitleMax = 500;
    public const int TextMax = 20_000;
    public const int ShortTextMax = 200;
    public const int EstimateMaxMinutes = 60 * 24 * 365;

    public static void CheckFields(TaskItem task, ITaskStore store, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            errors.Add(new(nameof(TaskItem.Title), ValidationCodes.Required));
        }
        else if (task.Title.Length > TitleMax)
        {
            errors.Add(new(nameof(TaskItem.Title), ValidationCodes.TooLong));
        }

        CheckLength(task.Description, TextMax, nameof(TaskItem.Description), errors);
        CheckLength(task.Notes, TextMax, nameof(TaskItem.Notes), errors);
        CheckLength(task.ExternalRef, ShortTextMax, nameof(TaskItem.ExternalRef), errors);
        CheckLength(task.Requester, ShortTextMax, nameof(TaskItem.Requester), errors);
        CheckLength(task.Responsible, ShortTextMax, nameof(TaskItem.Responsible), errors);

        if (!Enum.IsDefined(task.Priority))
        {
            errors.Add(new(nameof(TaskItem.Priority), ValidationCodes.OutOfRange));
        }

        if (task.DueTime is not null && task.DueDate is null)
        {
            errors.Add(new(nameof(TaskItem.DueTime), ValidationCodes.TimeWithoutDate));
        }

        if (task.StartDate is { } start && task.DueDate is { } due && start > due)
        {
            errors.Add(new(nameof(TaskItem.StartDate), ValidationCodes.StartAfterDue));
        }

        if (task.EstimateMinutes is < 0 or > EstimateMaxMinutes)
        {
            errors.Add(new(nameof(TaskItem.EstimateMinutes), ValidationCodes.OutOfRange));
        }

        if (task.ExternalUrl is { } url && !IsWebLink(url))
        {
            errors.Add(new(nameof(TaskItem.ExternalUrl), ValidationCodes.InvalidUrl));
        }

        if (store.GetStatus(task.StatusId) is null)
        {
            errors.Add(new(nameof(TaskItem.StatusId), ValidationCodes.NotFound));
        }

        CheckReference(store, TaxonomyKind.Category, task.CategoryId, nameof(TaskItem.CategoryId), errors);
        CheckReference(store, TaxonomyKind.Project, task.ProjectId, nameof(TaskItem.ProjectId), errors);
        foreach (var tag in task.Tags)
        {
            CheckReference(store, TaxonomyKind.Tag, tag, nameof(TaskItem.Tags), errors);
        }

        foreach (var item in task.Checklist)
        {
            if (string.IsNullOrWhiteSpace(item.Text))
            {
                errors.Add(new(nameof(TaskItem.Checklist), ValidationCodes.Required));
            }
            else if (item.Text.Length > TitleMax)
            {
                errors.Add(new(nameof(TaskItem.Checklist), ValidationCodes.TooLong));
            }
        }
    }

    /// <summary>
    /// Subtasks are one level deep: the parent must be a live top-level task, and a task that has
    /// subtasks cannot itself become one.
    /// </summary>
    public static void CheckParent(TaskItem task, ITaskStore store, List<ValidationError> errors)
    {
        if (task.ParentId is not { } parentId)
        {
            return;
        }

        if (parentId == task.Id)
        {
            errors.Add(new(nameof(TaskItem.ParentId), ValidationCodes.SelfParent));
            return;
        }

        var parent = store.GetTask(parentId);
        if (parent is null)
        {
            errors.Add(new(nameof(TaskItem.ParentId), ValidationCodes.NotFound));
        }
        else if (parent.IsDeleted)
        {
            errors.Add(new(nameof(TaskItem.ParentId), ValidationCodes.Deleted));
        }
        else if (parent.ParentId is not null)
        {
            errors.Add(new(nameof(TaskItem.ParentId), ValidationCodes.NestedSubtask));
        }

        if (store.GetSubtasks(task.Id).Any(s => !s.IsDeleted))
        {
            errors.Add(new(nameof(TaskItem.ParentId), ValidationCodes.HasSubtasks));
        }
    }

    public static void CheckTaxonomyName(TaxonomyItem item, ITaskStore store, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(item.Name))
        {
            errors.Add(new(nameof(TaxonomyItem.Name), ValidationCodes.Required));
        }
        else if (item.Name.Length > ShortTextMax)
        {
            errors.Add(new(nameof(TaxonomyItem.Name), ValidationCodes.TooLong));
        }
        else if (store.GetTaxonomy(item.Kind).Any(t =>
            !t.IsDeleted && t.Id != item.Id && string.Equals(t.Name, item.Name, StringComparison.CurrentCultureIgnoreCase)))
        {
            errors.Add(new(nameof(TaxonomyItem.Name), ValidationCodes.Duplicate));
        }

        if (item.Color is { } color && !IsHexColor(color))
        {
            errors.Add(new(nameof(TaxonomyItem.Color), ValidationCodes.InvalidColor));
        }
    }

    public static void ThrowIfAny(List<ValidationError> errors)
    {
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

    /// <summary>Only http and https: a task link must never start a program or open a local file.</summary>
    public static bool IsWebLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsHexColor(string color) =>
        color.Length == 7 && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit);

    private static void CheckLength(string? text, int max, string field, List<ValidationError> errors)
    {
        if (text?.Length > max)
        {
            errors.Add(new(field, ValidationCodes.TooLong));
        }
    }

    private static void CheckReference(ITaskStore store, TaxonomyKind kind, string? id, string field, List<ValidationError> errors)
    {
        if (id is null)
        {
            return;
        }

        var item = store.GetTaxonomyItem(kind, id);
        if (item is null)
        {
            errors.Add(new(field, ValidationCodes.NotFound));
        }
        else if (item.IsDeleted)
        {
            errors.Add(new(field, ValidationCodes.Deleted));
        }
    }
}

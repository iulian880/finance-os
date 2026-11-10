using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Services;
using FinanceOS.App.Reminders;
using FinanceOS.App.Shell;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Controls;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Tasks;

/// <summary>Validation messages per field, for <c>ui:FieldAssist.ErrorText="{Binding Errors[Title]}"</c>.</summary>
public sealed class FieldErrors : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> _errors = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? this[string field] => _errors.GetValueOrDefault(field);

    public bool HasAny => _errors.Count > 0;

    public void Set(string field, string? message)
    {
        if (message is null ? !_errors.Remove(field) : _errors.TryGetValue(field, out var old) && old == message)
        {
            return;
        }

        if (message is not null)
        {
            _errors[field] = message;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAny)));
    }

    public void Clear()
    {
        if (_errors.Count > 0)
        {
            _errors.Clear();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAny)));
        }
    }
}

public sealed record TagChip(string Id, string Name, string RemoveLabel);

/// <summary>A checklist step; editing the text or the check box saves right away.</summary>
public sealed class ChecklistRowViewModel(ChecklistItem item, Action<string, string> rename, Action<string, bool> check) : ObservableObject
{
    private string _text = item.Text;
    private bool _isDone = item.IsDone;

    public string Id { get; } = item.Id;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                rename(Id, value);
            }
        }
    }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (SetProperty(ref _isDone, value))
            {
                check(Id, value);
            }
        }
    }
}

/// <summary>A reminder on the task: what it is, when it fires next, remove.</summary>
public sealed record ReminderRowViewModel(string Id, string Label, string Next, string RemoveLabel);

/// <summary>A subtask line in the details panel: check it off, or open it.</summary>
public sealed class SubtaskRowViewModel(TaskItem task, bool isClosed, string openLabel, Action<string, bool> setDone, Action<string> open) : ObservableObject
{
    private bool _isClosed = isClosed;

    public string Id { get; } = task.Id;

    public string Title { get; } = task.Title;

    public string OpenLabel { get; } = openLabel;

    public bool IsClosed
    {
        get => _isClosed;
        set
        {
            if (SetProperty(ref _isClosed, value))
            {
                setDone(Id, value);
            }
        }
    }

    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => open(task.Id));
}

/// <summary>
/// The selected task in the right panel. Every field saves on its own (text fields when they lose
/// focus, everything else immediately), each save is one undoable step, and a refused value stays in
/// the field with the reason under it. <see cref="Load"/> brings the panel in line with the stored task
/// after any change, including undo.
/// </summary>
public sealed partial class TaskDetailsViewModel : ObservableObject
{
    private readonly TaskService _tasks;
    private readonly ReminderService _reminders;
    private readonly TimeProvider _time;
    private readonly Localizer _l;
    private readonly IInAppToasts _toasts;
    private readonly ILauncher _launcher;
    private readonly Action<string?> _select;
    private TaskItem _task = null!;
    private bool _loading;

    private string _title = "";
    private string? _description;
    private string? _notes;
    private string? _requester;
    private string? _responsible;
    private string? _externalRef;
    private string? _externalUrl;

    [ObservableProperty]
    private ChoiceOption<string>? _selectedStatus;

    [ObservableProperty]
    private ChoiceOption<Priority>? _selectedPriority;

    [ObservableProperty]
    private ChoiceOption<string?>? _selectedCategory;

    [ObservableProperty]
    private ChoiceOption<string?>? _selectedProject;

    [ObservableProperty]
    private DateTime? _dueDate;

    [ObservableProperty]
    private TimeSpan? _dueTime;

    [ObservableProperty]
    private string _newTagText = "";

    [ObservableProperty]
    private string _newChecklistText = "";

    [ObservableProperty]
    private string _newSubtaskText = "";

    /// <summary>Picking a preset adds that reminder; the box then goes back to its placeholder.</summary>
    [ObservableProperty]
    private ChoiceOption<int>? _selectedReminderPreset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAbsoluteReminderCommand))]
    private DateTime? _reminderDate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddAbsoluteReminderCommand))]
    private TimeSpan? _reminderTime;

    [ObservableProperty]
    private IReadOnlyList<ChoiceOption<int>> _reminderPresets = [];

    [ObservableProperty]
    private IReadOnlyList<ChoiceOption<string>> _statusOptions = [];

    [ObservableProperty]
    private IReadOnlyList<ChoiceOption<Priority>> _priorityOptions = [];

    [ObservableProperty]
    private IReadOnlyList<ChoiceOption<string?>> _categoryOptions = [];

    [ObservableProperty]
    private IReadOnlyList<ChoiceOption<string?>> _projectOptions = [];

    public TaskDetailsViewModel(TaskService tasks, ReminderService reminders, string taskId, TimeProvider time, Localizer localizer, IInAppToasts toasts, ILauncher launcher, Action<string?> select)
    {
        _tasks = tasks;
        _reminders = reminders;
        _time = time;
        _l = localizer;
        _toasts = toasts;
        _launcher = launcher;
        _select = select;
        TaskId = taskId;
        Load(tasks.Get(taskId) ?? throw new EntityNotFoundException("task", taskId));
    }

    public string TaskId { get; }

    public FieldErrors Errors { get; } = new();

    public ObservableCollection<TagChip> Tags { get; } = [];

    public ObservableCollection<ChecklistRowViewModel> Checklist { get; } = [];

    public ObservableCollection<SubtaskRowViewModel> Subtasks { get; } = [];

    public ObservableCollection<ReminderRowViewModel> ReminderRows { get; } = [];

    public string Title
    {
        get => _title;
        set => SetRequiredText(ref _title, value, nameof(Title), t => t with { Title = value });
    }

    public string? Description
    {
        get => _description;
        set => SetText(ref _description, value, nameof(Description), t => t with { Description = value });
    }

    public string? Notes
    {
        get => _notes;
        set => SetText(ref _notes, value, nameof(Notes), t => t with { Notes = value });
    }

    private bool _advancedFields = true;

    /// <summary>
    /// Advanced mode: project and references can be edited. In Simple they show only when they already
    /// hold something, read-only, so switching modes never seems to lose information.
    /// </summary>
    public bool AdvancedFields
    {
        get => _advancedFields;
        set
        {
            if (SetProperty(ref _advancedFields, value))
            {
                OnPropertyChanged(nameof(ShowProject));
                OnPropertyChanged(nameof(ShowReferences));
                OnPropertyChanged(nameof(ReferencesReadOnly));
            }
        }
    }

    public bool ShowProject => AdvancedFields || SelectedProject?.Value is not null;

    public bool ShowReferences => AdvancedFields || new[] { _requester, _responsible, _externalRef, _externalUrl }.Any(v => !string.IsNullOrWhiteSpace(v));

    public bool ReferencesReadOnly => !AdvancedFields;

    public string? Requester
    {
        get => _requester;
        set => SetText(ref _requester, value, nameof(Requester), t => t with { Requester = value });
    }

    public string? Responsible
    {
        get => _responsible;
        set => SetText(ref _responsible, value, nameof(Responsible), t => t with { Responsible = value });
    }

    public string? ExternalRef
    {
        get => _externalRef;
        set => SetText(ref _externalRef, value, nameof(ExternalRef), t => t with { ExternalRef = value });
    }

    public string? ExternalUrl
    {
        get => _externalUrl;
        set
        {
            SetText(ref _externalUrl, value, nameof(ExternalUrl), t => t with { ExternalUrl = value });
            OpenLinkCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsClosed => _tasks.Statuses().FirstOrDefault(s => s.Id == _task.StatusId)?.IsClosed == true;

    public bool IsPinned => _task.IsPinned;

    public bool IsArchived => _task.IsArchived;

    public bool IsSubtask => _task.ParentId is not null;

    /// <summary>Subtasks are one level deep: only a top-level task gets the subtask list.</summary>
    public bool CanHaveSubtasks => !IsSubtask;

    public string? ParentTitle => _task.ParentId is { } parent ? _tasks.Get(parent)?.Title : null;

    public string CompleteLabel => _l[IsClosed ? "Details.Reopen" : "Details.Complete"];

    public string PinLabel => _l[IsPinned ? "Details.Unpin" : "Details.Pin"];

    public string ArchiveLabel => _l[IsArchived ? "Details.Unarchive" : "Details.Archive"];

    /// <summary>"Creat 5 noiembrie 2026, 10:30 · modificat …".</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string> { _l.Format("Details.Created", Moment(_task.CreatedAt)) };
            if (_task.UpdatedAt > _task.CreatedAt)
            {
                parts.Add(_l.Format("Details.Updated", Moment(_task.UpdatedAt)));
            }

            if (_task.CompletedAt is { } done)
            {
                parts.Add(_l.Format("Details.CompletedAt", Moment(done)));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Shows <paramref name="task"/> as stored: options, fields, tags, checklist, subtasks. Clears errors.</summary>
    public void Load(TaskItem task)
    {
        _loading = true;
        try
        {
            _task = task;
            Errors.Clear();
            StatusOptions = [.. _tasks.Statuses().Select(s => new ChoiceOption<string>(s.Id, () => TaskText.Status(s, _l)))];
            PriorityOptions = [.. Enum.GetValues<Priority>().Select(p => new ChoiceOption<Priority>(p, () => TaskText.Priority(p, _l)))];
            CategoryOptions = Options(TaxonomyKind.Category, "Details.NoCategory", task.CategoryId);
            ProjectOptions = Options(TaxonomyKind.Project, "Details.NoProject", task.ProjectId);

            _title = task.Title;
            _description = task.Description;
            _notes = task.Notes;
            _requester = task.Requester;
            _responsible = task.Responsible;
            _externalRef = task.ExternalRef;
            _externalUrl = task.ExternalUrl;
            SelectedStatus = StatusOptions.FirstOrDefault(o => o.Value == task.StatusId);
            SelectedPriority = PriorityOptions.First(o => o.Value == task.Priority);
            SelectedCategory = CategoryOptions.First(o => o.Value == task.CategoryId);
            SelectedProject = ProjectOptions.First(o => o.Value == task.ProjectId);
            DueDate = task.DueDate?.ToDateTime(TimeOnly.MinValue);
            DueTime = task.DueTime?.ToTimeSpan();

            var tagNames = _tasks.Taxonomy(TaxonomyKind.Tag).ToDictionary(t => t.Id, t => t.Name);
            Tags.Clear();
            foreach (var tag in task.Tags.Where(tagNames.ContainsKey).OrderBy(id => tagNames[id], StringComparer.CurrentCultureIgnoreCase))
            {
                Tags.Add(new TagChip(tag, tagNames[tag], _l.Format("Details.RemoveTag", tagNames[tag])));
            }

            Checklist.Clear();
            foreach (var item in task.Checklist)
            {
                Checklist.Add(new ChecklistRowViewModel(item, RenameChecklistItem, CheckChecklistItem));
            }

            ReminderPresets = [.. ReminderText.Presets.Select(m => new ChoiceOption<int>(m, () => _l[$"Reminder.Preset.{m}"]))];
            SelectedReminderPreset = null;
            ReminderRows.Clear();
            foreach (var reminder in _reminders.For(task.Id))
            {
                ReminderRows.Add(new ReminderRowViewModel(reminder.Id, ReminderText.Describe(reminder, _l), NextText(reminder, task), _l["Details.RemoveReminder"]));
            }

            var closed = TaskViews.ClosedStatusIds(_tasks.Statuses());
            Subtasks.Clear();
            foreach (var subtask in _tasks.Subtasks(task.Id))
            {
                Subtasks.Add(new SubtaskRowViewModel(subtask, closed.Contains(subtask.StatusId), _l.Format("Details.OpenTask", subtask.Title), SetSubtaskDone, id => _select(id)));
            }
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(string.Empty);
        OpenLinkCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleComplete() => Run(nameof(SelectedStatus), () =>
    {
        if (IsClosed)
        {
            _tasks.Reopen(TaskId);
        }
        else
        {
            _tasks.Complete(TaskId);
        }
    });

    [RelayCommand]
    private void TogglePin() => Commit(nameof(IsPinned), t => t with { IsPinned = !t.IsPinned });

    [RelayCommand]
    private void ToggleArchive() => Run(string.Empty, () =>
    {
        if (IsArchived)
        {
            _tasks.Unarchive(TaskId);
        }
        else
        {
            _tasks.Archive(TaskId);
            _toasts.Show(new ToastMessage { Title = _l["Toast.Archived.Title"], Message = _task.Title });
        }
    });

    [RelayCommand]
    private void Duplicate() => Run(string.Empty, () =>
    {
        var copy = _tasks.Duplicate(TaskId, _l.Format("Details.CopyTitle", _task.Title));
        _toasts.Show(new ToastMessage { Title = _l["Toast.Duplicated.Title"], Message = copy.Title });
        _select(copy.Id);
    });

    [RelayCommand]
    private void Delete() => Run(string.Empty, () => TaskListPageViewModel.MoveToTrash(_tasks, TaskId, _toasts, _l));

    [RelayCommand]
    private void Close() => _select(null);

    [RelayCommand]
    private void OpenParent()
    {
        if (_task.ParentId is { } parent)
        {
            _select(parent);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenLink))]
    private void OpenLink()
    {
        if (CanOpenLink())
        {
            _launcher.Open(new Uri(_task.ExternalUrl!));
        }
    }

    /// <summary>Only the stored, validated link opens (http/https), never what is half-typed in the field.</summary>
    private bool CanOpenLink() => _task.ExternalUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    [RelayCommand]
    private void AddTag()
    {
        var name = NewTagText.Trim();
        if (name.Length == 0)
        {
            return;
        }

        Run("Tags", () =>
        {
            var tag = _tasks.GetOrCreateTag(name);
            _tasks.Edit(TaskId, t => t with { Tags = [.. t.Tags, tag.Id] });
            NewTagText = "";
        });
    }

    [RelayCommand]
    private void RemoveTag(TagChip? chip)
    {
        if (chip is not null)
        {
            Commit("Tags", t => t with { Tags = [.. t.Tags.Where(id => id != chip.Id)] });
        }
    }

    [RelayCommand]
    private void AddChecklistItem()
    {
        if (string.IsNullOrWhiteSpace(NewChecklistText))
        {
            return;
        }

        Run(nameof(Checklist), () =>
        {
            _tasks.AddChecklistItem(TaskId, NewChecklistText);
            NewChecklistText = "";
        });
    }

    [RelayCommand]
    private void RemoveChecklistItem(ChecklistRowViewModel? item)
    {
        if (item is not null)
        {
            Run(nameof(Checklist), () => _tasks.RemoveChecklistItem(TaskId, item.Id));
        }
    }

    [RelayCommand]
    private void AddSubtask()
    {
        if (string.IsNullOrWhiteSpace(NewSubtaskText))
        {
            return;
        }

        Run(nameof(Subtasks), () =>
        {
            _tasks.Create(new TaskDraft(NewSubtaskText) { ParentId = TaskId });
            NewSubtaskText = "";
        });
    }

    [RelayCommand(CanExecute = nameof(CanAddAbsoluteReminder))]
    private void AddAbsoluteReminder()
    {
        if (ReminderDate is { } date && ReminderTime is { } time)
        {
            Run("Reminders", () =>
            {
                _reminders.AddAbsolute(TaskId, date.Date + time);
                ReminderDate = null;
                ReminderTime = null;
            });
        }
    }

    private bool CanAddAbsoluteReminder() => ReminderDate is not null && ReminderTime is not null;

    [RelayCommand]
    private void RemoveReminder(ReminderRowViewModel? row)
    {
        if (row is not null)
        {
            _reminders.Remove(row.Id);
        }
    }

    partial void OnSelectedReminderPresetChanged(ChoiceOption<int>? value)
    {
        if (!_loading && value is not null)
        {
            Run("Reminders", () => _reminders.AddRelative(TaskId, value.Value));
        }
    }

    /// <summary>"Următorul: 10 noiembrie 2026, 09:30", or why it will not fire.</summary>
    private string NextText(Reminder reminder, TaskItem task)
    {
        if (_reminders.PlannedUtc(reminder) is { } next)
        {
            var local = TimeZoneInfo.ConvertTime(next, _time.LocalTimeZone).DateTime;
            return next < _time.GetUtcNow() ? _l["Reminder.Passed"] : _l.Format("Reminder.Next", DisplayFormat.DateAndTime(local, _l.Culture));
        }

        return reminder.Kind == ReminderKind.RelativeToDue && task.DueDate is null ? _l["Reminder.NeedsDue"] : _l["Reminder.Inactive"];
    }

    partial void OnSelectedStatusChanged(ChoiceOption<string>? value)
    {
        if (!_loading && value is not null)
        {
            Run(nameof(SelectedStatus), () => _tasks.SetStatus(TaskId, value.Value));
        }
    }

    partial void OnSelectedPriorityChanged(ChoiceOption<Priority>? value)
    {
        if (!_loading && value is not null)
        {
            Commit(nameof(SelectedPriority), t => t with { Priority = value.Value });
        }
    }

    partial void OnSelectedCategoryChanged(ChoiceOption<string?>? value)
    {
        if (!_loading && value is not null)
        {
            Commit(nameof(SelectedCategory), t => t with { CategoryId = value.Value });
        }
    }

    partial void OnSelectedProjectChanged(ChoiceOption<string?>? value)
    {
        if (!_loading && value is not null)
        {
            Commit(nameof(SelectedProject), t => t with { ProjectId = value.Value });
        }
    }

    partial void OnDueDateChanged(DateTime? value)
    {
        if (!_loading)
        {
            // Clearing the date clears the time too: a time without a date means nothing.
            Commit(nameof(DueDate), t => t with
            {
                DueDate = value is { } d ? DateOnly.FromDateTime(d) : null,
                DueTime = value is null ? null : t.DueTime,
            });
        }
    }

    partial void OnDueTimeChanged(TimeSpan? value)
    {
        if (!_loading)
        {
            Commit(nameof(DueTime), t => t with { DueTime = value is { } time ? TimeOnly.FromTimeSpan(time) : null });
        }
    }

    private void RenameChecklistItem(string id, string text) => Run(nameof(Checklist), () => _tasks.RenameChecklistItem(TaskId, id, text));

    private void CheckChecklistItem(string id, bool isDone) => Run(nameof(Checklist), () => _tasks.SetChecklistItemDone(TaskId, id, isDone));

    private void SetSubtaskDone(string id, bool isDone) => Run(nameof(Subtasks), () =>
    {
        if (isDone)
        {
            _tasks.Complete(id);
        }
        else
        {
            _tasks.Reopen(id);
        }
    });

    private void SetText(ref string? field, string? value, string name, Func<TaskItem, TaskItem> change)
    {
        if (SetProperty(ref field, value, name) && !_loading)
        {
            Commit(name, change);
        }
    }

    private void SetRequiredText(ref string field, string value, string name, Func<TaskItem, TaskItem> change)
    {
        if (SetProperty(ref field, value, name) && !_loading)
        {
            Commit(name, change);
        }
    }

    private void Commit(string field, Func<TaskItem, TaskItem> change) => Run(field, () => _tasks.Edit(TaskId, change));

    /// <summary>
    /// Runs a command; a refusal shows next to <paramref name="field"/> (or as a toast for whole-task
    /// actions). On success the page refreshes this panel through the task events.
    /// </summary>
    private void Run(string field, Action action)
    {
        try
        {
            action();
            Errors.Set(field, null);
        }
        catch (DomainValidationException ex)
        {
            var message = TaskText.Validation(ex.Errors[0], _l);
            if (field.Length == 0)
            {
                _toasts.Show(new ToastMessage { Title = _l["Toast.Failed.Title"], Message = message, Tone = ToastTone.Danger });
            }
            else
            {
                Errors.Set(field, message);
            }
        }
        catch (EntityNotFoundException)
        {
            // Deleted meanwhile (e.g. undo of its creation): the page closes the panel on the same event.
        }
    }

    private IReadOnlyList<ChoiceOption<string?>> Options(TaxonomyKind kind, string noneKey, string? current)
    {
        var items = _tasks.Taxonomy(kind).ToList();
        if (current is not null && items.All(i => i.Id != current) && _tasks.Get(TaskId) is not null)
        {
            // Deleted meanwhile but still on this task: keep it visible instead of silently showing "none".
            items.Add(new TaxonomyItem { Kind = kind, Id = current, Name = current });
        }

        return [new ChoiceOption<string?>(null, () => _l[noneKey]), .. items.Select(i => new ChoiceOption<string?>(i.Id, () => i.Name))];
    }

    private string Moment(DateTimeOffset utc) =>
        DisplayFormat.DateAndTime(TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).DateTime, _l.Culture);
}

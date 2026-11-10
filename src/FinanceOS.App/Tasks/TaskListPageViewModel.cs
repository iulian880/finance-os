using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.Core.Events;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Controls;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Tasks;

/// <summary>
/// What one refresh knows: the tasks that are not trashed or archived, the statuses, names of
/// categories / projects / tags, and the local "now". Fills list rows consistently on every page.
/// </summary>
public sealed class TaskSnapshot
{
    private readonly Dictionary<string, string> _names;
    private readonly Dictionary<string, string> _titles;
    private readonly TaskService _tasks;
    private readonly TimeProvider _time;
    private readonly Localizer _l;

    public TaskSnapshot(TaskService tasks, TimeProvider time, Localizer localizer)
    {
        _tasks = tasks;
        _time = time;
        _l = localizer;
        Now = time.GetLocalNow().DateTime;
        Statuses = tasks.Statuses();
        Closed = TaskViews.ClosedStatusIds(Statuses);
        Tasks = tasks.List(TaskScope.Open);
        _names = tasks.Taxonomy(TaxonomyKind.Category).Concat(tasks.Taxonomy(TaxonomyKind.Project)).Concat(tasks.Taxonomy(TaxonomyKind.Tag))
            .ToDictionary(t => t.Id, t => t.Name);
        _titles = Tasks.ToDictionary(t => t.Id, t => t.Title);
    }

    public DateTime Now { get; }

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public IReadOnlyList<TaskStatusInfo> Statuses { get; }

    public HashSet<string> Closed { get; }

    /// <summary>Open and closed tasks, not trashed, not archived.</summary>
    public IReadOnlyList<TaskItem> Tasks { get; }

    public IReadOnlyList<TaskItem> View(TaskView view, TaskSort sort = TaskSort.Smart) => TaskViews.Apply(Tasks, view, sort, Statuses, Now);

    public bool IsClosed(TaskItem task) => Closed.Contains(task.StatusId);

    /// <summary>Tasks finished (status done) since local midnight.</summary>
    public int DoneToday() => Tasks.Count(t => t.CompletedAt is { } at && KindOf(t) == TaskStatusKind.Done
        && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).DateTime) == Today);

    public TaskStatusKind? KindOf(TaskItem task) => Statuses.FirstOrDefault(s => s.Id == task.StatusId)?.Kind;

    /// <summary>Copies what a list row shows from <paramref name="task"/> onto <paramref name="row"/>.</summary>
    public void Fill(TaskRowViewModel row, TaskItem task)
    {
        var isClosed = IsClosed(task);
        row.Title = task.Title;
        row.IsClosed = isClosed;
        row.IsPinned = task.IsPinned;
        row.IsDemo = task.IsDemo;
        row.DueLabel = TaskText.DueLabel(task, Now, _l);
        row.Due = TaskText.Due(task, isClosed, Now);
        row.PriorityLabel = task.Priority >= Priority.High ? TaskText.Priority(task.Priority, _l) : null;
        row.IsUrgent = task.Priority == Priority.Urgent;
        row.Context = task.ProjectId is { } project && _names.TryGetValue(project, out var p) ? p
            : task.CategoryId is { } category && _names.TryGetValue(category, out var c) ? c : null;
        row.ParentTitle = task.ParentId is { } parent ? _titles.GetValueOrDefault(parent) ?? _tasks.Get(parent)?.Title : null;
        row.ChecklistProgress = task.Checklist.Count == 0 ? null : $"{task.Checklist.Count(i => i.IsDone)}/{task.Checklist.Count}";
        row.Tags = [.. task.Tags.Select(id => _names.GetValueOrDefault(id)).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).Take(3)];
    }

    /// <summary>
    /// Makes <paramref name="rows"/> match <paramref name="tasks"/> by moving, inserting and removing rows,
    /// never replacing them, so selection and scroll position survive. <paramref name="group"/> labels
    /// each row for grouped lists (My Day); a row that changes group is re-inserted so the list regroups.
    /// </summary>
    public void Sync(ObservableCollection<TaskRowViewModel> rows, IReadOnlyList<TaskItem> tasks, Func<TaskItem, string?>? group = null)
    {
        var existing = rows.ToDictionary(r => r.Id);
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var label = group?.Invoke(task);
            if (existing.TryGetValue(task.Id, out var row) && row.Group != label)
            {
                rows.Remove(row);
                row = null;
            }

            if (row is null)
            {
                row = new TaskRowViewModel(task.Id) { Group = label };
                rows.Insert(i, row);
                existing[task.Id] = row;
            }
            else if (rows.IndexOf(row) is var at && at != i)
            {
                rows.Move(at, i);
            }

            Fill(row, task);
        }

        while (rows.Count > tasks.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }
}

/// <summary>
/// Base for pages that list tasks (Tasks, My Day, Overview): refreshes on task events (once per
/// command), opens the selected task in the details panel, completes from the check box, and moves
/// to the trash with an exact "Undo" on the toast.
/// </summary>
public abstract partial class TaskListPageViewModel : PageViewModel
{
    private readonly SynchronizationContext? _ui;
    private bool _refreshQueued;
    private TaskDetailsViewModel? _details;

    [ObservableProperty]
    private TaskRowViewModel? _selectedRow;

    protected TaskListPageViewModel(SectionInfo info, TaskService tasks, IEventBus bus, TimeProvider time, Localizer localizer, IInAppToasts toasts, ILauncher launcher, ReminderService reminders)
        : base(info, localizer)
    {
        TaskService = tasks;
        Reminders = reminders;
        Time = time;
        Toasts = toasts;
        Launcher = launcher;
        _ui = SynchronizationContext.Current;
        bus.Subscribe<IDomainEvent>(OnDomainEvent);
        reminders.Changed += (_, _) => QueueRefresh();
    }

    private UserSettings? _settings;

    /// <summary>Saved settings (the real app): the mode decides whether advanced task fields can be edited.</summary>
    public UserSettings? Settings
    {
        get => _settings;
        init
        {
            _settings = value;
            if (value is not null)
            {
                value.Changed += (_, _) =>
                {
                    if (_details is not null)
                    {
                        _details.AdvancedFields = AdvancedFields;
                    }
                };
            }
        }
    }

    private bool AdvancedFields => _settings?.Capabilities.EditAdvancedFields ?? true;

    /// <summary>The selected task, shown in the right panel (null: the panel shows its empty state).</summary>
    public override object? Details => _details;

    protected TaskService TaskService { get; }

    protected ReminderService Reminders { get; }

    protected TimeProvider Time { get; }

    protected IInAppToasts Toasts { get; }

    protected ILauncher Launcher { get; }

    /// <summary>Shows a task in the details panel (null closes it). Selects its row when the page lists it.</summary>
    public void Select(string? taskId)
    {
        if (taskId is null || TaskService.Get(taskId) is not { IsDeleted: false } task)
        {
            SetDetails(null);
            SelectedRow = null;
            return;
        }

        if (_details?.TaskId != task.Id)
        {
            SetDetails(new TaskDetailsViewModel(TaskService, Reminders, task.Id, Time, L, Toasts, Launcher, Select) { AdvancedFields = AdvancedFields });
        }

        SelectedRow = FindRow(task.Id);
    }

    /// <summary>Re-reads the tasks, updates the page and keeps the details panel in line with the stored task.</summary>
    public void Refresh()
    {
        _refreshQueued = false;
        RefreshCore(new TaskSnapshot(TaskService, Time, L));

        // The details panel follows the task, even when it leaves this page's lists (e.g. completed).
        if (_details is not null)
        {
            if (TaskService.Get(_details.TaskId) is { IsDeleted: false } shown)
            {
                _details.Load(shown);
                SelectedRow = FindRow(shown.Id);
            }
            else
            {
                SetDetails(null);
            }
        }
    }

    /// <summary>"Today" moves: re-read when the user comes back to the page.</summary>
    public override void OnActivated() => Refresh();

    protected abstract void RefreshCore(TaskSnapshot snapshot);

    /// <summary>The row showing <paramref name="taskId"/>, if this page lists it.</summary>
    protected abstract TaskRowViewModel? FindRow(string taskId);

    protected override void OnLanguageChanged() => Refresh();

    [RelayCommand]
    private void ToggleComplete(TaskRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        Run(() =>
        {
            if (row.IsClosed)
            {
                TaskService.Reopen(row.Id);
            }
            else
            {
                TaskService.Complete(row.Id);
            }
        });
    }

    [RelayCommand]
    private void ToggleCompleteSelected() => ToggleComplete(SelectedRow);

    /// <summary>Delete key: to the trash, with "Undo" on the toast.</summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedRow is { } row)
        {
            MoveToTrash(TaskService, row.Id, Toasts, L);
        }
    }

    [RelayCommand]
    private void ClearSelection() => Select(null);

    /// <summary>Opens a task in the details panel (rows shown as buttons, e.g. on Overview).</summary>
    [RelayCommand]
    private void Open(TaskRowViewModel? row) => Select(row?.Id);

    /// <summary>Moves a task to the trash and offers to undo exactly that (shared with the details panel).</summary>
    internal static void MoveToTrash(TaskService tasks, string id, IInAppToasts toasts, Localizer l)
    {
        var title = tasks.Get(id)?.Title;
        tasks.Delete(id);
        var batch = tasks.UndoBatchId;
        toasts.Show(new ToastMessage
        {
            Title = l["Toast.Deleted.Title"],
            Message = title,
            ActionText = l["Toast.Undo"],
            Action = () =>
            {
                // Only revert this delete: if the user did something else since, restore the task directly.
                if (tasks.UndoBatchId == batch)
                {
                    tasks.Undo();
                }
                else
                {
                    tasks.Restore(id);
                }
            },
        });
    }

    protected void Run(Action action)
    {
        try
        {
            action();
        }
        catch (DomainValidationException ex)
        {
            Toasts.Show(new ToastMessage
            {
                Title = L["Toast.Failed.Title"],
                Message = TaskText.Validation(ex.Errors[0], L),
                Tone = ToastTone.Danger,
            });
        }
    }

    partial void OnSelectedRowChanged(TaskRowViewModel? value)
    {
        if (value is not null && _details?.TaskId != value.Id)
        {
            Select(value.Id);
        }
    }

    private void SetDetails(TaskDetailsViewModel? details)
    {
        if (ReferenceEquals(_details, details))
        {
            return;
        }

        _details = details;
        OnPropertyChanged(nameof(Details));
    }

    private void OnDomainEvent(IDomainEvent domainEvent) => QueueRefresh();

    private void QueueRefresh()
    {
        // A command can raise several events (a task and its subtasks): refresh once, after the command.
        if (_ui is null)
        {
            Refresh();
            return;
        }

        if (!_refreshQueued)
        {
            _refreshQueued = true;
            _ui.Post(_ => Refresh(), null);
        }
    }
}

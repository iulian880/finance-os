using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.Core.Events;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Tasks;

/// <summary>One view tab: label and how many tasks it holds right now.</summary>
public sealed partial class TaskViewTab(TaskView view) : ObservableObject
{
    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private int _count;

    public TaskView View { get; } = view;
}

/// <summary>
/// One row of a task list. Kept per task id and updated in place, so selection and scroll position
/// survive every refresh.
/// </summary>
public sealed partial class TaskRowViewModel(string id) : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private bool _isClosed;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Sample task from onboarding: marked, so it never passes for real work.</summary>
    [ObservableProperty]
    private bool _isDemo;

    [ObservableProperty]
    private string? _dueLabel;

    [ObservableProperty]
    private DueState _due;

    /// <summary>Shown only for high and urgent tasks, so the list stays calm.</summary>
    [ObservableProperty]
    private string? _priorityLabel;

    [ObservableProperty]
    private bool _isUrgent;

    /// <summary>Project, else category.</summary>
    [ObservableProperty]
    private string? _context;

    [ObservableProperty]
    private string? _parentTitle;

    /// <summary>"1/3" when the task has a checklist.</summary>
    [ObservableProperty]
    private string? _checklistProgress;

    [ObservableProperty]
    private IReadOnlyList<string> _tags = [];

    public string Id { get; } = id;

    /// <summary>Group header in grouped lists (My Day: "Overdue", "Today", "Tomorrow"); null elsewhere.</summary>
    public string? Group { get; init; }
}

/// <summary>
/// The Tasks page (M7): quick add, views with counts, sort, the list, and the selected task's details in
/// the right panel. Every change goes through <see cref="TaskService"/>; the list follows its events, so
/// it also reflects undo and changes made from the details panel.
/// </summary>
public sealed partial class TasksPageViewModel : TaskListPageViewModel
{
    [ObservableProperty]
    private TaskViewTab _selectedTab;

    [ObservableProperty]
    private ChoiceOption<TaskSort> _selectedSort;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    private string _quickAddText = "";

    /// <summary>Changes every time the quick-add box should take focus (Ctrl+N).</summary>
    [ObservableProperty]
    private int _quickAddFocusToken;

    public TasksPageViewModel(TaskService tasks, IEventBus bus, TimeProvider time, Localizer localizer, IInAppToasts toasts, ILauncher launcher, ReminderService reminders)
        : base(SectionCatalog.Get(Section.Tasks), tasks, bus, time, localizer, toasts, launcher, reminders)
    {
        Tabs = [.. Enum.GetValues<TaskView>().Select(v => new TaskViewTab(v))];
        _selectedTab = Tabs[0];
        SortOptions = [.. Enum.GetValues<TaskSort>().Select(s => new ChoiceOption<TaskSort>(s, () => TaskText.Sort(s, L)))];
        _selectedSort = SortOptions[0];
        Refresh();
    }

    public IReadOnlyList<TaskViewTab> Tabs { get; }

    public IReadOnlyList<ChoiceOption<TaskSort>> SortOptions { get; }

    public ObservableCollection<TaskRowViewModel> Rows { get; } = [];

    /// <summary>The list virtualizes, so it needs a fixed height and its own scroll bar.</summary>
    public override bool ScrollsItself => true;

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyTitle => L[$"Tasks.Empty.{SelectedTab.View}.Title"];

    public string EmptyDescription => L[$"Tasks.Empty.{SelectedTab.View}.Description"];

    /// <summary>Moves keyboard focus to the quick-add box (Ctrl+N from anywhere).</summary>
    public void RequestQuickAdd() => QuickAddFocusToken++;

    protected override TaskRowViewModel? FindRow(string taskId) => Rows.FirstOrDefault(r => r.Id == taskId);

    protected override void RefreshCore(TaskSnapshot snapshot)
    {
        foreach (var tab in Tabs)
        {
            tab.Label = TaskText.View(tab.View, L);
        }

        foreach (var option in SortOptions)
        {
            option.Refresh();
        }

        var counts = TaskViews.Counts(snapshot.Tasks, snapshot.Statuses, snapshot.Now);
        foreach (var tab in Tabs)
        {
            tab.Count = counts[tab.View];
        }

        snapshot.Sync(Rows, snapshot.View(SelectedTab.View, SelectedSort.Value));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    [RelayCommand(CanExecute = nameof(CanAddTask))]
    private void AddTask()
    {
        var title = QuickAddText.Trim();
        if (title.Length == 0)
        {
            return;
        }

        // A task added in "Today" is due today, so it appears where the user just typed it.
        var today = DateOnly.FromDateTime(Time.GetLocalNow().DateTime);
        Run(() =>
        {
            TaskService.Create(new TaskDraft(title) { DueDate = SelectedTab.View == TaskView.Today ? today : null });
            QuickAddText = "";
        });
    }

    private bool CanAddTask() => !string.IsNullOrWhiteSpace(QuickAddText);

    partial void OnSelectedTabChanged(TaskViewTab value) => Refresh();

    partial void OnSelectedSortChanged(ChoiceOption<TaskSort> value) => Refresh();
}

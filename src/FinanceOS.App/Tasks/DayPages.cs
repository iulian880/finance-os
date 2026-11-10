using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.Core.Events;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Tasks;

/// <summary>
/// My Day (M8): what is overdue, what is due today, what comes tomorrow, in one list grouped in that
/// order. Same rows, check boxes, keys and details panel as the Tasks page.
/// </summary>
public sealed partial class MyDayPageViewModel : TaskListPageViewModel
{
    [ObservableProperty]
    private string? _doneTodayText;

    public MyDayPageViewModel(TaskService tasks, IEventBus bus, TimeProvider time, Localizer localizer, IInAppToasts toasts, ILauncher launcher, ReminderService reminders)
        : base(SectionCatalog.Get(Section.MyDay), tasks, bus, time, localizer, toasts, launcher, reminders)
    {
        Refresh();
    }

    /// <summary>Rows in group order; the view groups them by <see cref="TaskRowViewModel.Group"/>.</summary>
    public ObservableCollection<TaskRowViewModel> Rows { get; } = [];

    public override bool ScrollsItself => true;

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyTitle => Info.EmptyTitle(L);

    public string EmptyDescription => Info.EmptyDescription(L);

    protected override TaskRowViewModel? FindRow(string taskId) => Rows.FirstOrDefault(r => r.Id == taskId);

    protected override void RefreshCore(TaskSnapshot snapshot)
    {
        var overdue = snapshot.View(TaskView.Overdue);
        var overdueIds = overdue.Select(t => t.Id).ToHashSet();
        var today = snapshot.View(TaskView.Today).Where(t => !overdueIds.Contains(t.Id)).ToList();
        var tomorrow = snapshot.View(TaskView.Upcoming).Where(t => t.DueDate == snapshot.Today.AddDays(1)).ToList();

        var groups = new Dictionary<string, string>();
        foreach (var task in overdue)
        {
            groups[task.Id] = L["MyDay.Group.Overdue"];
        }

        foreach (var task in today)
        {
            groups[task.Id] = L["MyDay.Group.Today"];
        }

        foreach (var task in tomorrow)
        {
            groups[task.Id] = L["MyDay.Group.Tomorrow"];
        }

        snapshot.Sync(Rows, [.. overdue, .. today, .. tomorrow], t => groups[t.Id]);

        var done = snapshot.DoneToday();
        DoneTodayText = done > 0 ? L.Format("MyDay.DoneToday", done) : null;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }
}

/// <summary>One number on the Overview summary card. <see cref="IsAlert"/> colors it (overdue &gt; 0).</summary>
public sealed record StatTile(string Label, string Value, string Hint, bool IsAlert = false);

/// <summary>One line of today's timeline: the time ("14:30" or "All day") and the task.</summary>
public sealed record TimelineEntry(string Time, TaskRowViewModel Row);

/// <summary>
/// Overview (M8): greeting, today's date, the four numbers that matter (open, done today, overdue,
/// waiting), the three tasks that matter most and today's timeline. Fixed widgets for now (M23 makes
/// the dashboard configurable). Clicking a task opens it in the details panel.
/// </summary>
public sealed partial class OverviewPageViewModel : TaskListPageViewModel
{
    public const int TopCount = 3;

    [ObservableProperty]
    private string _greeting = "";

    [ObservableProperty]
    private string _dateText = "";

    [ObservableProperty]
    private IReadOnlyList<StatTile> _stats = [];

    [ObservableProperty]
    private IReadOnlyList<TimelineEntry> _timeline = [];

    public OverviewPageViewModel(TaskService tasks, IEventBus bus, TimeProvider time, Localizer localizer, IInAppToasts toasts, ILauncher launcher, ReminderService reminders)
        : base(SectionCatalog.Get(Section.Overview), tasks, bus, time, localizer, toasts, launcher, reminders)
    {
        Refresh();
    }

    /// <summary>The three most important open tasks: pinned, then overdue, then by due date and priority.</summary>
    public ObservableCollection<TaskRowViewModel> Top { get; } = [];

    public bool IsTopEmpty => Top.Count == 0;

    public bool IsTimelineEmpty => Timeline.Count == 0;

    public static string GreetingKey(int hour) => hour switch
    {
        >= 5 and < 12 => "Overview.Greeting.Morning",
        >= 12 and < 18 => "Overview.Greeting.Afternoon",
        _ => "Overview.Greeting.Evening",
    };

    protected override TaskRowViewModel? FindRow(string taskId) =>
        Top.FirstOrDefault(r => r.Id == taskId) ?? Timeline.Select(e => e.Row).FirstOrDefault(r => r.Id == taskId);

    protected override void RefreshCore(TaskSnapshot snapshot)
    {
        Greeting = L[GreetingKey(snapshot.Now.Hour)];
        DateText = DisplayFormat.LongDate(snapshot.Now, L.Culture);

        var open = snapshot.View(TaskView.All);
        var overdue = snapshot.View(TaskView.Overdue).Count;
        var waiting = open.Count(t => snapshot.KindOf(t) == TaskStatusKind.Waiting);
        Stats =
        [
            new(L["Overview.Stat.InProgress"], Number(open.Count), L["Overview.Stat.InProgress.Hint"]),
            new(L["Overview.Stat.DoneToday"], Number(snapshot.DoneToday()), L["Overview.Stat.DoneToday.Hint"]),
            new(L["Overview.Stat.Overdue"], Number(overdue), L["Overview.Stat.Overdue.Hint"], overdue > 0),
            new(L["Overview.Stat.Waiting"], Number(waiting), L["Overview.Stat.Waiting.Hint"]),
        ];

        snapshot.Sync(Top, [.. open.Take(TopCount)]);

        // Today's tasks by time; the ones without a time are due "all day" and come first.
        Timeline = [.. snapshot.View(TaskView.Today, TaskSort.DueDate)
            .OrderBy(t => t.DueTime is not null)
            .ThenBy(t => t.DueTime)
            .Select(t =>
            {
                var row = new TaskRowViewModel(t.Id);
                snapshot.Fill(row, t);
                return new TimelineEntry(t.DueTime?.ToString("HH:mm", L.Culture) ?? L["Overview.Today.AllDay"], row);
            })];

        OnPropertyChanged(nameof(IsTopEmpty));
        OnPropertyChanged(nameof(IsTimelineEmpty));
    }

    private string Number(int value) => value.ToString(L.Culture);
}

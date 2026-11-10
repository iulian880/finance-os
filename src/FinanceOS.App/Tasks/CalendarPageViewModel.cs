using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.Core.Calendar;
using FinanceOS.Core.Events;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Tasks;

public enum CalendarMode
{
    Month,
    Week,
    Day,
    Agenda,
}

/// <summary>A task with its time label ("09:30" or "Toată ziua") in the week and day views.</summary>
public sealed record TimedRow(string Time, TaskRowViewModel Row);

/// <summary>One column of the week view.</summary>
public sealed record WeekDayColumn(DateOnly Date, string Header, bool IsToday, bool IsWeekend, string? Holiday, IReadOnlyList<TimedRow> Rows);

/// <summary>One hour of the day view; <see cref="IsWorking"/> shades the hours outside the working day.</summary>
public sealed record HourSlot(string Label, bool IsWorking, IReadOnlyList<TaskRowViewModel> Rows);

/// <summary>A drop of a task on a day (drag and drop in the month view).</summary>
public sealed record MoveRequest(string TaskId, DateOnly To);

/// <summary>One cell of the month view. <see cref="Name"/> is what a screen reader says.</summary>
public sealed record CalendarDayViewModel(
    DateOnly Date, string DayNumber, bool IsCurrentMonth, bool IsToday, bool IsWeekend, string? Holiday,
    IReadOnlyList<TaskRowViewModel> Tasks, string? More, string Name);

/// <summary>One day in the agenda: heading and its tasks.</summary>
public sealed record AgendaDay(string Label, bool IsToday, string? Holiday, IReadOnlyList<TaskRowViewModel> Rows);

/// <summary>
/// Calendar (M11a): month view with holidays and drag-and-drop rescheduling, agenda of the next 30 days.
/// Moving a task to another day changes its due date only after an explicit confirmation (PLAN M11).
/// Week and day views with working hours come next (M11b).
/// </summary>
public sealed partial class CalendarPageViewModel : TaskListPageViewModel
{
    public const int MaxChipsPerDay = 3;
    public const int AgendaDays = 30;

    /// <summary>The day view shows these hours; earlier or later tasks sit in the first or last one.</summary>
    public const int FirstHour = 7;
    public const int LastHour = 21;

    private readonly UserSettings? _settings;

    private MoveRequest? _pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonth), nameof(IsWeek), nameof(IsDay), nameof(IsAgenda), nameof(HasPeriod))]
    private ChoiceOption<CalendarMode> _selectedMode;

    [ObservableProperty]
    private DateOnly _month;

    /// <summary>The day the week and day views show (and navigate from).</summary>
    [ObservableProperty]
    private DateOnly _anchor;

    [ObservableProperty]
    private IReadOnlyList<WeekDayColumn> _weekDays = [];

    [ObservableProperty]
    private string _allDayLabel = "";

    [ObservableProperty]
    private IReadOnlyList<TimedRow> _dayAllDay = [];

    [ObservableProperty]
    private IReadOnlyList<HourSlot> _dayHours = [];

    [ObservableProperty]
    private string _periodTitle = "";

    [ObservableProperty]
    private IReadOnlyList<string> _weekdayNames = [];

    [ObservableProperty]
    private IReadOnlyList<CalendarDayViewModel> _days = [];

    [ObservableProperty]
    private IReadOnlyList<AgendaDay> _agenda = [];

    /// <summary>"Muți „X” de pe 10 nov. pe 12 nov.?" while a drop waits for confirmation.</summary>
    [ObservableProperty]
    private string? _moveQuestion;

    public CalendarPageViewModel(TaskService tasks, IEventBus bus, TimeProvider time, Localizer localizer, IInAppToasts toasts, ILauncher launcher, ReminderService reminders,
        UserSettings? settings = null)
        : base(SectionCatalog.Get(Section.Calendar), tasks, bus, time, localizer, toasts, launcher, reminders)
    {
        _settings = settings;
        Settings = settings;
        if (settings is not null)
        {
            settings.Changed += (_, _) => Refresh();
        }

        ModeOptions = [.. Enum.GetValues<CalendarMode>().Select(m => new ChoiceOption<CalendarMode>(m, () => L[$"Calendar.View.{m}"]))];
        _selectedMode = ModeOptions[0];
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        _month = new DateOnly(today.Year, today.Month, 1);
        _anchor = today;
        Refresh();
    }

    public IReadOnlyList<ChoiceOption<CalendarMode>> ModeOptions { get; }

    public bool IsMonth => SelectedMode.Value == CalendarMode.Month;

    public bool IsAgenda => SelectedMode.Value == CalendarMode.Agenda;

    public bool IsWeek => SelectedMode.Value == CalendarMode.Week;

    public bool IsDay => SelectedMode.Value == CalendarMode.Day;

    /// <summary>Month, week and day have a period to move through; the agenda always starts today.</summary>
    public bool HasPeriod => !IsAgenda;

    /// <summary>Working day shown in the day view (Settings → working hours).</summary>
    public TimeOnly WorkStart => _settings?.WorkStart ?? UserSettings.DefaultWorkStart;

    public TimeOnly WorkEnd => _settings?.WorkEnd ?? UserSettings.DefaultWorkEnd;

    public string WorkingHoursText => L.Format("Calendar.WorkingHours", WorkStart.ToString("HH:mm", L.Culture), WorkEnd.ToString("HH:mm", L.Culture));

    public bool IsAgendaEmpty => Agenda.Count == 0;

    public override bool ScrollsItself => true;

    protected override TaskRowViewModel? FindRow(string taskId) =>
        Days.SelectMany(d => d.Tasks).Concat(Agenda.SelectMany(a => a.Rows)).Concat(WeekDays.SelectMany(w => w.Rows).Select(r => r.Row))
            .Concat(DayAllDay.Select(r => r.Row)).Concat(DayHours.SelectMany(h => h.Rows)).FirstOrDefault(r => r.Id == taskId);

    protected override void RefreshCore(TaskSnapshot snapshot)
    {
        foreach (var option in ModeOptions)
        {
            option.Refresh();
        }

        var culture = L.Culture;
        var weekStart = CalendarMath.StartOfWeek(Anchor);
        PeriodTitle = SelectedMode.Value switch
        {
            CalendarMode.Week => $"{weekStart.ToString("d MMM", culture)} – {weekStart.AddDays(6).ToString("d MMM yyyy", culture)}",
            CalendarMode.Day => DisplayFormat.LongDate(Anchor.ToDateTime(TimeOnly.MinValue), culture),
            _ => Capitalize(Month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", culture), culture),
        };
        AllDayLabel = L["Overview.Today.AllDay"];
        WeekdayNames = [.. Enumerable.Range(0, 7).Select(i => culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((i + 1) % 7)))];

        var holidays = new[] { Month.Year - 1, Month.Year, Month.Year + 1, snapshot.Today.Year, Anchor.Year }.Distinct()
            .SelectMany(CalendarMath.RomanianHolidays).GroupBy(h => h.Date).ToDictionary(g => g.Key, g => L[$"Holiday.{g.First().Key}"]);
        var byDate = snapshot.Tasks.Where(t => t.DueDate is not null)
            .GroupBy(t => t.DueDate!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(snapshot.IsClosed).ThenBy(t => t.DueTime is null).ThenBy(t => t.DueTime).ThenByDescending(t => t.Priority).ToList());

        Days = [.. CalendarMath.MonthGrid(Month).Select(date =>
        {
            var tasks = byDate.GetValueOrDefault(date) ?? [];
            var holiday = holidays.GetValueOrDefault(date);
            var name = string.Join(", ", new[]
            {
                DisplayFormat.LongDate(date.ToDateTime(TimeOnly.MinValue), culture), holiday, tasks.Count > 0 ? L.Format("Calendar.TaskCount", tasks.Count) : null,
            }.OfType<string>());
            return new CalendarDayViewModel(date, date.Day.ToString(culture), date.Month == Month.Month, date == snapshot.Today,
                CalendarMath.IsWeekend(date), holiday, [.. tasks.Take(MaxChipsPerDay).Select(t => Row(snapshot, t))],
                tasks.Count > MaxChipsPerDay ? L.Format("Calendar.More", tasks.Count - MaxChipsPerDay) : null, name);
        })];

        // Agenda: open tasks from today on, day by day (overdue ones live in My Day and the Overdue view).
        Agenda = [.. Enumerable.Range(0, AgendaDays).Select(snapshot.Today.AddDays)
            .Where(byDate.ContainsKey)
            .Select(d => new AgendaDay(
                (d == snapshot.Today ? L["Due.Today"] + " · " : "") + DisplayFormat.DayAndMonth(d.ToDateTime(TimeOnly.MinValue), culture),
                d == snapshot.Today, holidays.GetValueOrDefault(d),
                [.. byDate[d].Where(t => !snapshot.IsClosed(t)).Select(t => Row(snapshot, t))]))
            .Where(a => a.Rows.Count > 0)];
        // Week: seven columns, tasks by time (all-day ones first).
        TimedRow Timed(TaskItem t) => new(t.DueTime?.ToString("HH:mm", culture) ?? AllDayLabel, Row(snapshot, t));
        WeekDays = [.. Enumerable.Range(0, 7).Select(weekStart.AddDays).Select(d => new WeekDayColumn(
            d, Capitalize(d.ToString("ddd d", culture), culture), d == snapshot.Today, CalendarMath.IsWeekend(d), holidays.GetValueOrDefault(d),
            [.. (byDate.GetValueOrDefault(d) ?? []).OrderBy(t => t.DueTime is not null).ThenBy(t => t.DueTime).Select(Timed)]))];

        // Day: all-day tasks on top, then one slot per hour; hours outside the working day are shaded.
        var dayTasks = byDate.GetValueOrDefault(Anchor) ?? [];
        DayAllDay = [.. dayTasks.Where(t => t.DueTime is null).Select(Timed)];
        DayHours = [.. Enumerable.Range(FirstHour, LastHour - FirstHour + 1).Select(h => new HourSlot(
            new TimeOnly(h, 0).ToString("HH:mm", culture),
            h >= WorkStart.Hour && h < WorkEnd.Hour,
            [.. dayTasks.Where(t => t.DueTime is { } time && Math.Clamp(time.Hour, FirstHour, LastHour) == h).Select(t => Row(snapshot, t))]))];

        OnPropertyChanged(nameof(IsAgendaEmpty));
        OnPropertyChanged(nameof(WorkingHoursText));
    }

    [RelayCommand]
    private void Previous() => Move(-1);

    [RelayCommand]
    private void Next() => Move(1);

    [RelayCommand]
    private void GoToToday()
    {
        var today = DateOnly.FromDateTime(Time.GetLocalNow().DateTime);
        Anchor = today;
        Month = new DateOnly(today.Year, today.Month, 1);
        Refresh();
    }

    /// <summary>Opens the day view on a day (click on a day heading in the week view).</summary>
    [RelayCommand]
    private void OpenDay(DateOnly? date)
    {
        if (date is { } day)
        {
            Anchor = day;
            SelectedMode = ModeOptions.Single(m => m.Value == CalendarMode.Day);
        }
    }

    /// <summary>Previous / next month, week or day, depending on the view.</summary>
    private void Move(int step)
    {
        switch (SelectedMode.Value)
        {
            case CalendarMode.Week:
                Anchor = Anchor.AddDays(7 * step);
                break;
            case CalendarMode.Day:
                Anchor = Anchor.AddDays(step);
                break;
            default:
                Month = Month.AddMonths(step);
                break;
        }
    }

    /// <summary>A task was dropped on a day: ask before changing its due date.</summary>
    [RelayCommand]
    private void RequestMove(MoveRequest? request)
    {
        if (request is null || TaskService.Get(request.TaskId) is not { IsDeleted: false } task || task.DueDate == request.To)
        {
            return;
        }

        _pending = request;
        var culture = L.Culture;
        var from = task.DueDate is { } due ? due.ToString("d MMM", culture) : L["Calendar.Move.NoDue"];
        MoveQuestion = L.Format("Calendar.Move.Question", task.Title, from, request.To.ToString("d MMM", culture));
    }

    /// <summary>Confirmed: the due date moves (time of day kept); one undoable step.</summary>
    [RelayCommand]
    private void ConfirmMove()
    {
        if (_pending is { } move)
        {
            Run(() => TaskService.Edit(move.TaskId, t => t with { DueDate = move.To }));
        }

        CancelMove();
    }

    [RelayCommand]
    private void CancelMove()
    {
        _pending = null;
        MoveQuestion = null;
    }

    partial void OnMonthChanged(DateOnly value) => Refresh();

    /// <summary>Week and day navigation keeps the month view on the same month.</summary>
    partial void OnAnchorChanged(DateOnly value)
    {
        var month = new DateOnly(value.Year, value.Month, 1);
        if (month != Month)
        {
            Month = month;
        }
        else
        {
            Refresh();
        }
    }

    partial void OnSelectedModeChanged(ChoiceOption<CalendarMode> value) => Refresh();

    private static TaskRowViewModel Row(TaskSnapshot snapshot, TaskItem task)
    {
        var row = new TaskRowViewModel(task.Id);
        snapshot.Fill(row, task);
        return row;
    }

    private static string Capitalize(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
}

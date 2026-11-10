using FinanceOS.App.Tasks;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;
using Xunit;

namespace FinanceOS.App.Tests;

/// <summary>M11a: month view, holidays, agenda and moving a task with confirmation.</summary>
public sealed class CalendarPageTests : IDisposable
{
    // Monday 9 November 2026, 17:00 in Bucharest.
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 9, 17, 0, 0, TimeSpan.FromHours(2)));
    private static readonly DateOnly Today = new(2026, 11, 9);

    private readonly TaskKit _kit = new(Clock);
    private readonly Localizer _loc = TestLoc.Ro();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void Month_view_places_tasks_on_their_day_and_shows_holidays()
    {
        var task = _kit.Tasks.Create(new TaskDraft("Close books") { DueDate = Today.AddDays(2) });
        for (var i = 0; i < 5; i++)
        {
            _kit.Tasks.Create(new TaskDraft($"Busy {i}") { DueDate = new DateOnly(2026, 11, 20) });
        }

        var page = Create();

        Assert.Equal("Noiembrie 2026", page.PeriodTitle);
        Assert.Equal(42, page.Days.Count);
        var day = page.Days.Single(d => d.Date == Today.AddDays(2));
        Assert.Equal(task.Id, Assert.Single(day.Tasks).Id);
        Assert.True(page.Days.Single(d => d.Date == Today).IsToday);

        var busy = page.Days.Single(d => d.Date == new DateOnly(2026, 11, 20));
        Assert.Equal(CalendarPageViewModel.MaxChipsPerDay, busy.Tasks.Count);
        Assert.Equal("încă 2", busy.More);
        Assert.Equal("Sf. Andrei", page.Days.Single(d => d.Date == new DateOnly(2026, 11, 30)).Holiday);

        page.NextCommand.Execute(null);
        Assert.Equal("Decembrie 2026", page.PeriodTitle);
        Assert.Equal("Ziua Națională", page.Days.Single(d => d.Date == new DateOnly(2026, 12, 1)).Holiday);
        page.GoToTodayCommand.Execute(null);
        Assert.Equal("Noiembrie 2026", page.PeriodTitle);
    }

    [Fact]
    public void Dropping_a_task_on_another_day_asks_first_and_moves_only_on_confirm()
    {
        var task = _kit.Tasks.Create(new TaskDraft("VAT") { DueDate = Today.AddDays(1), DueTime = new TimeOnly(14, 30) });
        var page = Create();

        page.RequestMoveCommand.Execute(new MoveRequest(task.Id, Today.AddDays(3)));
        Assert.Equal("Muți „VAT” de pe 10 nov. pe 12 nov.?", page.MoveQuestion);
        Assert.Equal(Today.AddDays(1), _kit.Tasks.Get(task.Id)!.DueDate);

        page.CancelMoveCommand.Execute(null);
        Assert.Null(page.MoveQuestion);
        Assert.Equal(Today.AddDays(1), _kit.Tasks.Get(task.Id)!.DueDate);

        page.RequestMoveCommand.Execute(new MoveRequest(task.Id, Today.AddDays(3)));
        page.ConfirmMoveCommand.Execute(null);
        var moved = _kit.Tasks.Get(task.Id)!;
        Assert.Equal((Today.AddDays(3), new TimeOnly(14, 30)), (moved.DueDate!.Value, moved.DueTime!.Value)); // time of day kept
        Assert.Equal(["VAT"], page.Days.Single(d => d.Date == Today.AddDays(3)).Tasks.Select(t => t.Title));
        Assert.Equal("task.edit", _kit.Tasks.UndoCommand); // one undoable step

        page.RequestMoveCommand.Execute(new MoveRequest(task.Id, Today.AddDays(3))); // same day: nothing to ask
        Assert.Null(page.MoveQuestion);
    }

    [Fact]
    public void Agenda_lists_the_next_thirty_days_of_open_tasks()
    {
        _kit.Tasks.Create(new TaskDraft("Today's") { DueDate = Today });
        _kit.Tasks.Create(new TaskDraft("Later") { DueDate = Today.AddDays(10) });
        _kit.Tasks.Create(new TaskDraft("Too far") { DueDate = Today.AddDays(40) });
        var done = _kit.Tasks.Create(new TaskDraft("Done one") { DueDate = Today.AddDays(1) });
        _kit.Tasks.Complete(done.Id);
        var page = Create();

        page.SelectedMode = page.ModeOptions.Single(m => m.Value == CalendarMode.Agenda);

        Assert.True(page.IsAgenda);
        Assert.Equal(["Azi · Luni, 9 noiembrie", "Joi, 19 noiembrie"], page.Agenda.Select(a => a.Label));
        Assert.Equal(["Today's", "Later"], page.Agenda.SelectMany(a => a.Rows).Select(r => r.Title));
    }

    [Fact]
    public void Week_view_shows_seven_days_with_times_and_navigates_by_week()
    {
        _kit.Tasks.Create(new TaskDraft("Morning call") { DueDate = Today.AddDays(1), DueTime = new TimeOnly(9, 30) });
        _kit.Tasks.Create(new TaskDraft("All day") { DueDate = Today.AddDays(1) });
        var page = Create();

        page.SelectedMode = page.ModeOptions.Single(m => m.Value == CalendarMode.Week);

        Assert.Equal("9 nov. – 15 nov. 2026", page.PeriodTitle);
        Assert.Equal(Today, page.WeekDays[0].Date);
        Assert.True(page.WeekDays[0].IsToday);
        Assert.Equal([("Toată ziua", "All day"), ("09:30", "Morning call")], page.WeekDays[1].Rows.Select(r => (r.Time, r.Row.Title)));

        page.NextCommand.Execute(null);
        Assert.Equal(Today.AddDays(7), page.WeekDays[0].Date);
        page.NextCommand.Execute(null);
        page.NextCommand.Execute(null);
        page.NextCommand.Execute(null); // week of 7 December: the month view follows
        Assert.Equal("Decembrie 2026", MonthTitle(page));
    }

    [Fact]
    public void Day_view_puts_tasks_in_their_hour_and_marks_working_hours()
    {
        _kit.Tasks.Create(new TaskDraft("Early") { DueDate = Today.AddDays(2), DueTime = new TimeOnly(6, 15) });
        _kit.Tasks.Create(new TaskDraft("Lunch review") { DueDate = Today.AddDays(2), DueTime = new TimeOnly(12, 45) });
        _kit.Tasks.Create(new TaskDraft("Anytime") { DueDate = Today.AddDays(2) });
        var page = Create();

        page.OpenDayCommand.Execute(Today.AddDays(2));

        Assert.True(page.IsDay);
        Assert.Equal("Miercuri, 11 noiembrie 2026", page.PeriodTitle);
        Assert.Equal(["Anytime"], page.DayAllDay.Select(r => r.Row.Title));
        Assert.Equal(["Early"], page.DayHours.Single(h => h.Label == "07:00").Rows.Select(r => r.Title)); // before 07:00: first slot
        Assert.Equal(["Lunch review"], page.DayHours.Single(h => h.Label == "12:00").Rows.Select(r => r.Title));
        Assert.False(page.DayHours.Single(h => h.Label == "08:00").IsWorking);
        Assert.True(page.DayHours.Single(h => h.Label == "09:00").IsWorking);
        Assert.False(page.DayHours.Single(h => h.Label == "18:00").IsWorking);
        Assert.Equal("Program 09:00–18:00", page.WorkingHoursText);

        page.PreviousCommand.Execute(null);
        Assert.Equal("Marți, 10 noiembrie 2026", page.PeriodTitle);
    }

    private static string MonthTitle(CalendarPageViewModel page)
    {
        page.SelectedMode = page.ModeOptions.Single(m => m.Value == CalendarMode.Month);
        return page.PeriodTitle;
    }

    private CalendarPageViewModel Create() => new(_kit.Tasks, _kit.Bus, Clock, _loc, new FakeToasts(), new FakeLauncher(), _kit.Reminders);
}

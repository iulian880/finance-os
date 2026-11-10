using FinanceOS.App.Reminders;
using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;
using Xunit;

namespace FinanceOS.App.Tests;

/// <summary>M9 in the UI: the reminders section of the details panel and the notification center.</summary>
public sealed class RemindersUiTests : IDisposable
{
    // Monday 9 November 2026, 17:00 in Bucharest (UTC+2).
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 9, 17, 0, 0, TimeSpan.FromHours(2)));
    private static readonly DateOnly Today = new(2026, 11, 9);

    private readonly TaskKit _kit = new(Clock);
    private readonly Localizer _loc = TestLoc.Ro();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void Details_add_list_and_remove_reminders()
    {
        var task = _kit.Tasks.Create(new TaskDraft("VAT") { DueDate = Today.AddDays(1), DueTime = new TimeOnly(10, 0) });
        var details = OpenDetails(task.Id);

        details.SelectedReminderPreset = details.ReminderPresets.Single(p => p.Value == 30);
        details.ReminderDate = new DateTime(2026, 11, 12);
        details.ReminderTime = new TimeSpan(8, 15, 0);
        details.AddAbsoluteReminderCommand.Execute(null);

        Assert.Equal(["Cu 30 min înainte", "12 noiembrie 2026, 08:15"], details.ReminderRows.Select(r => r.Label));
        Assert.Equal("Următorul: 10 noiembrie 2026, 09:30", details.ReminderRows[0].Next);
        Assert.Null(details.SelectedReminderPreset);
        Assert.Null(details.ReminderDate);
        Assert.Equal(2, _kit.Reminders.For(task.Id).Count);

        details.RemoveReminderCommand.Execute(details.ReminderRows[0]);
        Assert.Single(_kit.Reminders.For(task.Id));
        Assert.Single(details.ReminderRows);
    }

    [Fact]
    public void A_relative_reminder_on_a_task_without_due_date_says_why_it_will_not_fire()
    {
        var task = _kit.Tasks.Create(new TaskDraft("Someday"));
        var details = OpenDetails(task.Id);

        details.SelectedReminderPreset = details.ReminderPresets.Single(p => p.Value == 0);

        var row = Assert.Single(details.ReminderRows);
        Assert.Equal(("La termen", "Are nevoie de un termen"), (row.Label, row.Next));
    }

    [Fact]
    public void Notification_center_lists_fired_reminders_and_acts_on_them()
    {
        var task = _kit.Tasks.Create(new TaskDraft("Pay supplier") { DueDate = Today, DueTime = new TimeOnly(17, 0) });
        var other = _kit.Tasks.Create(new TaskDraft("Call bank") { DueDate = Today, DueTime = new TimeOnly(17, 0) });
        _kit.Reminders.AddRelative(task.Id, 0);
        _kit.Reminders.AddRelative(other.Id, 0);
        var center = new NotificationCenterViewModel(_kit.Scheduler, Clock, _loc);
        string? opened = null;
        center.OpenTask = id => opened = id;

        _kit.Scheduler.Start(); // both are due now

        Assert.Equal(2, _kit.Sink.Shown.Count);
        Assert.Equal(2, center.UnreadCount);
        Assert.True(center.HasUnread);
        var pay = center.Items.Single(i => i.Title == "Pay supplier");
        Assert.Equal(("Reminder", "17:00", true), (pay.Detail, pay.Time, pay.CanAct));

        pay.SnoozeCommand.Execute(center.SnoozeOptions.Single(o => o.Value == "15"));
        Assert.Equal(1, center.UnreadCount);
        Assert.False(center.Items.Single(i => i.Title == "Pay supplier").CanAct);

        center.Items.Single(i => i.Title == "Call bank").DoneCommand.Execute(null);
        Assert.Equal(SystemStatuses.Done, _kit.Tasks.Get(other.Id)!.StatusId);

        center.Items.Single(i => i.Title == "Pay supplier").OpenCommand.Execute(null);
        Assert.Equal(task.Id, opened);
        Assert.Equal(0, center.UnreadCount);
        Assert.Equal("Mâine la 09:00", center.SnoozeOptions[^1].Label);
    }

    [Fact]
    public void Shell_opens_a_task_from_a_notification_in_the_tasks_page()
    {
        var task = _kit.Tasks.Create(new TaskDraft("From a toast"));
        var services = new PageServices(new FakeThemeService(), Clock, new FakeDataMaintenance(), new FakeLauncher(), _loc, _kit.Tasks, _kit.Bus, new FakeToasts(), _kit.Reminders);
        var center = new NotificationCenterViewModel(_kit.Scheduler, Clock, _loc);
        var shell = new ShellViewModel(new FakeThemeService(), Clock, s => PageFactory.Create(s, services), _loc, _kit.Tasks, new FakeToasts(), center);

        center.OpenTask!(task.Id);

        Assert.Equal(Section.Tasks, shell.CurrentSection);
        Assert.Equal(task.Id, Assert.IsType<TaskDetailsViewModel>(shell.CurrentPage.Details).TaskId);
        Assert.True(shell.IsDetailsPanelOpen);
    }

    [Fact]
    public void Toast_snooze_choices_follow_the_language()
    {
        Assert.Equal(["5", "10", "15", "30", "60", "tomorrow"], ReminderText.ToastChoices(_loc).Select(c => c.Id));
        Assert.Equal("Tomorrow at 09:00", ReminderText.ToastChoices(TestLoc.En())[^1].Label);
    }

    [Fact]
    public void Start_with_windows_toggle_writes_through_and_stays_off_when_refused()
    {
        var startup = new FakeStartup();
        var settings = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Clock, _loc, startup);
        Assert.True(settings.CanStartWithWindows);
        Assert.False(settings.StartWithWindows);

        settings.StartWithWindows = true;
        Assert.True(startup.IsEnabled);

        startup.Refuse = true;
        settings.StartWithWindows = false;
        Assert.True(settings.StartWithWindows); // Windows said no: the toggle shows the truth

        Assert.False(new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Clock, _loc).CanStartWithWindows);
    }

    private sealed class FakeStartup : FinanceOS.App.Services.IStartupRegistration
    {
        public bool IsEnabled { get; private set; }

        public bool Refuse { get; set; }

        public bool Set(bool enabled)
        {
            if (!Refuse)
            {
                IsEnabled = enabled;
            }

            return !Refuse;
        }
    }

    private TaskDetailsViewModel OpenDetails(string id)
    {
        var page = new TasksPageViewModel(_kit.Tasks, _kit.Bus, Clock, _loc, new FakeToasts(), new FakeLauncher(), _kit.Reminders);
        page.Select(id);
        return Assert.IsType<TaskDetailsViewModel>(page.Details);
    }
}

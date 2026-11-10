using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;
using Xunit;

namespace FinanceOS.App.Tests;

/// <summary>M7: the Tasks page, the details panel and the shell's task commands, over a real database.</summary>
public sealed class TasksPageTests : IDisposable
{
    // Monday 9 November 2026, 17:00 in Bucharest (UTC+2).
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 9, 17, 0, 0, TimeSpan.FromHours(2)));
    private static readonly DateOnly Today = new(2026, 11, 9);

    private readonly TaskKit _kit = new(Clock);
    private readonly FakeToasts _toasts = new();
    private readonly FakeLauncher _launcher = new();
    private readonly Localizer _loc = TestLoc.Ro();

    private TaskService Tasks => _kit.Tasks;

    public void Dispose() => _kit.Dispose();

    // Page ------------------------------------------------------------------------------------------

    [Fact]
    public void Quick_add_creates_the_task_shows_it_and_clears_the_box()
    {
        var page = CreatePage();
        Assert.True(page.IsEmpty);
        Assert.Equal("Niciun task deschis", page.EmptyTitle);
        Assert.False(page.AddTaskCommand.CanExecute(null));

        page.QuickAddText = "  Reconcile bank BT  ";
        page.AddTaskCommand.Execute(null);

        Assert.Equal("", page.QuickAddText);
        var row = Assert.Single(page.Rows);
        Assert.Equal("Reconcile bank BT", row.Title);
        Assert.Null(row.DueLabel);
        Assert.False(page.IsEmpty);
        Assert.Equal(1, page.Tabs.Single(t => t.View == TaskView.All).Count);
    }

    [Fact]
    public void Adding_in_today_makes_the_task_due_today_so_it_stays_in_view()
    {
        var page = CreatePage();
        page.SelectedTab = page.Tabs.Single(t => t.View == TaskView.Today);

        page.QuickAddText = "Approve invoices";
        page.AddTaskCommand.Execute(null);

        var row = Assert.Single(page.Rows);
        Assert.Equal("Azi", row.DueLabel);
        Assert.Equal(DueState.Today, row.Due);
        Assert.Equal(Today, Tasks.Get(row.Id)!.DueDate);
    }

    [Fact]
    public void Views_filter_and_count_and_rows_carry_their_context()
    {
        var project = Tasks.CreateTaxonomy(TaxonomyKind.Project, "Audit 2026");
        var tag = Tasks.GetOrCreateTag("urgent");
        var overdue = Tasks.Create(new TaskDraft("Auditor questions") { DueDate = Today.AddDays(-2), Priority = Priority.Urgent, ProjectId = project.Id, Tags = [tag.Id] });
        var parent = Tasks.Create(new TaskDraft("Month-end") { DueDate = Today.AddDays(1), Checklist = ["a", "b", "c"], IsPinned = true });
        Tasks.SetChecklistItemDone(parent.Id, parent.Checklist[0].Id, true);
        Tasks.Create(new TaskDraft("IC balances") { ParentId = parent.Id, DueDate = Today, DueTime = new TimeOnly(18, 30) });
        var done = Tasks.Create(new TaskDraft("Payroll"));
        Tasks.Complete(done.Id);

        var page = CreatePage();

        Assert.Equal([3, 1, 1, 1, 1], page.Tabs.Select(t => t.Count));
        Assert.Equal(["Month-end", "Auditor questions", "IC balances"], page.Rows.Select(r => r.Title));

        var first = page.Rows[0];
        Assert.True(first.IsPinned);
        Assert.Equal("1/3", first.ChecklistProgress);
        Assert.Equal("Mâine", first.DueLabel);

        var urgent = page.Rows[1];
        Assert.Equal(overdue.Id, urgent.Id);
        Assert.Equal(DueState.Overdue, urgent.Due);
        Assert.Equal("Urgentă", urgent.PriorityLabel);
        Assert.True(urgent.IsUrgent);
        Assert.Equal("Audit 2026", urgent.Context);
        Assert.Equal(["urgent"], urgent.Tags);

        var subtask = page.Rows[2];
        Assert.Equal("Azi, 18:30", subtask.DueLabel);
        Assert.Equal("Month-end", subtask.ParentTitle);

        page.SelectedTab = page.Tabs.Single(t => t.View == TaskView.Completed);
        Assert.True(Assert.Single(page.Rows).IsClosed);
    }

    [Fact]
    public void Checking_a_row_completes_it_and_it_moves_to_completed()
    {
        var task = Tasks.Create(new TaskDraft("Close books"));
        var page = CreatePage();

        page.ToggleCompleteCommand.Execute(page.Rows[0]);

        Assert.Empty(page.Rows);
        Assert.Equal(1, page.Tabs.Single(t => t.View == TaskView.Completed).Count);
        Assert.Equal(SystemStatuses.Done, Tasks.Get(task.Id)!.StatusId);
    }

    [Fact]
    public void Rows_are_updated_in_place_so_the_selection_survives_a_refresh()
    {
        Tasks.Create(new TaskDraft("A") { DueDate = Today.AddDays(3) });
        var b = Tasks.Create(new TaskDraft("B") { DueDate = Today.AddDays(5) });
        var page = CreatePage();
        var rowB = page.Rows.Single(r => r.Id == b.Id);
        page.SelectedRow = rowB;

        Tasks.Edit(b.Id, t => t with { Title = "B renamed", DueDate = Today.AddDays(1) });

        Assert.Same(rowB, page.Rows[0]);
        Assert.Equal("B renamed", rowB.Title);
        Assert.Same(rowB, page.SelectedRow);
    }

    [Fact]
    public void Delete_key_moves_to_trash_and_the_toast_undoes_exactly_that()
    {
        var task = Tasks.Create(new TaskDraft("Mistake"));
        var other = Tasks.Create(new TaskDraft("Keep"));
        var page = CreatePage();
        page.SelectedRow = page.Rows.Single(r => r.Id == task.Id);
        Assert.NotNull(page.Details);

        page.DeleteSelectedCommand.Execute(null);

        Assert.True(Tasks.Get(task.Id)!.IsDeleted);
        Assert.Null(page.Details);
        Assert.Equal("Mutat în coș", _toasts.Last.Title);
        Assert.Equal("Mistake", _toasts.Last.Message);
        Assert.Equal("Anulează", _toasts.Last.ActionText);

        // Something else happened since: the toast restores the task without undoing the other action.
        Tasks.Complete(other.Id);
        _toasts.RunAction();

        Assert.False(Tasks.Get(task.Id)!.IsDeleted);
        Assert.Equal(SystemStatuses.Done, Tasks.Get(other.Id)!.StatusId);
        Assert.Contains(page.Rows, r => r.Id == task.Id);
    }

    [Fact]
    public void Selecting_a_row_opens_its_details_and_escape_closes_them()
    {
        var task = Tasks.Create(new TaskDraft("Report"));
        var page = CreatePage();
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.SelectedRow = page.Rows[0];

        var details = Assert.IsType<TaskDetailsViewModel>(page.Details);
        Assert.Equal(task.Id, details.TaskId);
        Assert.Contains(nameof(PageViewModel.Details), changed);

        page.ClearSelectionCommand.Execute(null);
        Assert.Null(page.Details);
        Assert.Null(page.SelectedRow);
    }

    [Fact]
    public void Switching_language_relabels_views_sorts_and_rows()
    {
        Tasks.Create(new TaskDraft("Tomorrow's") { DueDate = Today.AddDays(1) });
        var page = CreatePage();
        Assert.Equal("Toate", page.Tabs[0].Label);
        Assert.Equal("Recomandată", page.SortOptions[0].Label);
        Assert.Equal("Mâine", page.Rows[0].DueLabel);

        _loc.SetLanguage(AppLanguage.English);

        Assert.Equal("All", page.Tabs[0].Label);
        Assert.Equal("Recommended", page.SortOptions[0].Label);
        Assert.Equal("Tomorrow", page.Rows[0].DueLabel);
        Assert.Equal("Tasks", page.Title);
    }

    // Details ---------------------------------------------------------------------------------------

    [Fact]
    public void Details_save_each_field_and_follow_the_stored_task()
    {
        var category = Tasks.CreateTaxonomy(TaxonomyKind.Category, "Tax");
        var task = Tasks.Create(new TaskDraft("VAT"));
        var details = OpenDetails(task.Id);

        details.Title = "VAT return Q4";
        details.SelectedPriority = details.PriorityOptions.Single(o => o.Value == Priority.High);
        details.SelectedCategory = details.CategoryOptions.Single(o => o.Value == category.Id);
        details.DueDate = new DateTime(2026, 11, 25);
        details.DueTime = new TimeSpan(14, 30, 0);
        details.Description = "Quarterly return";
        details.ExternalUrl = "https://anaf.ro";

        var stored = Tasks.Get(task.Id)!;
        Assert.Equal("VAT return Q4", stored.Title);
        Assert.Equal(Priority.High, stored.Priority);
        Assert.Equal(category.Id, stored.CategoryId);
        Assert.Equal(new DateOnly(2026, 11, 25), stored.DueDate);
        Assert.Equal(new TimeOnly(14, 30), stored.DueTime);
        Assert.Equal("Quarterly return", stored.Description);
        Assert.True(details.OpenLinkCommand.CanExecute(null));

        details.OpenLinkCommand.Execute(null);
        Assert.Equal("https://anaf.ro/", Assert.Single(_launcher.Opened).AbsoluteUri);

        // Clearing the date clears the time as well.
        details.DueDate = null;
        Assert.Null(Tasks.Get(task.Id)!.DueTime);
        Assert.Null(details.DueTime);
    }

    [Fact]
    public void A_refused_value_stays_in_the_field_with_the_reason_in_the_ui_language()
    {
        var task = Tasks.Create(new TaskDraft("Keep me"));
        var details = OpenDetails(task.Id);

        details.Title = "   ";
        details.ExternalUrl = "file:///C:/Windows/notepad.exe";

        Assert.Equal("Câmp obligatoriu.", details.Errors["Title"]);
        Assert.Equal("Doar linkuri http:// sau https://.", details.Errors["ExternalUrl"]);
        Assert.Equal("   ", details.Title);
        Assert.Equal("Keep me", Tasks.Get(task.Id)!.Title);
        Assert.False(details.OpenLinkCommand.CanExecute(null));

        details.Title = "Fixed";
        Assert.Null(details.Errors["Title"]);
        Assert.Equal("Fixed", Tasks.Get(task.Id)!.Title);
    }

    [Fact]
    public void Tags_checklist_and_subtasks_are_edited_from_the_panel()
    {
        var task = Tasks.Create(new TaskDraft("Month-end"));
        var details = OpenDetails(task.Id);

        details.NewTagText = "IC";
        details.AddTagCommand.Execute(null);
        details.NewTagText = "ic";
        details.AddTagCommand.Execute(null); // same tag, any case: no duplicate
        Assert.Equal(["IC"], details.Tags.Select(t => t.Name));
        Assert.Equal("Scoate tag-ul IC", details.Tags[0].RemoveLabel);

        details.NewChecklistText = "Accruals";
        details.AddChecklistItemCommand.Execute(null);
        details.Checklist[0].IsDone = true;
        details.Checklist[0].Text = "Accruals posted";
        var item = Tasks.Get(task.Id)!.Checklist[0];
        Assert.Equal(("Accruals posted", true), (item.Text, item.IsDone));

        details.NewSubtaskText = "Bank rec";
        details.AddSubtaskCommand.Execute(null);
        var subtask = Assert.Single(details.Subtasks);
        subtask.IsClosed = true;
        Assert.Equal(SystemStatuses.Done, Tasks.Get(subtask.Id)!.StatusId);

        details.RemoveTagCommand.Execute(details.Tags[0]);
        details.RemoveChecklistItemCommand.Execute(details.Checklist[0]);
        Assert.Empty(Tasks.Get(task.Id)!.Tags);
        Assert.Empty(Tasks.Get(task.Id)!.Checklist);
    }

    [Fact]
    public void Complete_duplicate_and_archive_from_the_panel()
    {
        var task = Tasks.Create(new TaskDraft("Report"));
        var page = CreatePage();
        page.Select(task.Id);
        var details = (TaskDetailsViewModel)page.Details!;
        Assert.Equal("Marchează finalizat", details.CompleteLabel);

        details.ToggleCompleteCommand.Execute(null);
        details = (TaskDetailsViewModel)page.Details!;
        Assert.True(details.IsClosed);
        Assert.Equal("Redeschide", details.CompleteLabel);

        details.DuplicateCommand.Execute(null);
        var copy = (TaskDetailsViewModel)page.Details!;
        Assert.NotEqual(task.Id, copy.TaskId);
        Assert.Equal("Report (copie)", copy.Title);
        Assert.Equal("Copie creată", _toasts.Last.Title);

        copy.ToggleArchiveCommand.Execute(null);
        Assert.True(Tasks.Get(copy.TaskId)!.IsArchived);
        Assert.Equal("Arhivat", _toasts.Last.Title);
    }

    [Fact]
    public void Undo_brings_the_panel_back_in_line()
    {
        var task = Tasks.Create(new TaskDraft("Original"));
        var page = CreatePage();
        page.Select(task.Id);
        var details = (TaskDetailsViewModel)page.Details!;

        details.Title = "Changed";
        Tasks.Undo();

        Assert.Equal("Original", details.Title);
        Assert.Equal("Original", page.Rows[0].Title);

        Tasks.Undo(); // the create itself: the task is gone, the panel closes
        Assert.Null(page.Details);
        Assert.Empty(page.Rows);
    }

    // Shell -----------------------------------------------------------------------------------------

    [Fact]
    public void Ctrl_z_undoes_and_says_what_was_undone()
    {
        var shell = CreateShell();

        shell.UndoCommand.Execute(null);
        Assert.Equal("Nimic de anulat", _toasts.Last.Title);

        var task = Tasks.Create(new TaskDraft("x"));
        Tasks.Complete(task.Id);
        shell.UndoCommand.Execute(null);

        Assert.Equal(("Anulat", "Finalizare task"), (_toasts.Last.Title, _toasts.Last.Message));
        Assert.Equal(SystemStatuses.Todo, Tasks.Get(task.Id)!.StatusId);
    }

    [Fact]
    public void Ctrl_n_goes_to_tasks_and_focuses_quick_add()
    {
        var shell = CreateShell();

        shell.NewTaskCommand.Execute(null);

        var page = Assert.IsType<TasksPageViewModel>(shell.CurrentPage);
        Assert.Equal(1, page.QuickAddFocusToken);
    }

    [Fact]
    public void Selecting_a_task_opens_the_details_panel()
    {
        Tasks.Create(new TaskDraft("Open me"));
        var shell = CreateShell();
        shell.NavigateCommand.Execute(Section.Tasks);
        var page = (TasksPageViewModel)shell.CurrentPage;
        Assert.False(shell.IsRightPanelOpen);

        page.SelectedRow = page.Rows[0];

        Assert.True(shell.IsDetailsPanelOpen);
    }

    // Texts -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, null, "Azi", "Today")]
    [InlineData(0, "09:15", "Azi, 09:15", "Today, 09:15")]
    [InlineData(1, null, "Mâine", "Tomorrow")]
    [InlineData(-1, null, "Ieri", "Yesterday")]
    [InlineData(4, null, "Vineri", "Friday")]
    [InlineData(12, null, "21 nov.", "21 Nov")]
    [InlineData(60, null, "8 ian. 2027", "8 Jan 2027")]
    public void Due_labels_read_naturally_in_both_languages(int days, string? time, string romanian, string english)
    {
        var task = new TaskItem
        {
            Id = "t",
            Title = "t",
            DueDate = Today.AddDays(days),
            DueTime = time is null ? null : TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture),
        };
        var now = new DateTime(2026, 11, 9, 8, 0, 0);

        Assert.Equal(romanian, TaskText.DueLabel(task, now, TestLoc.Ro()));
        Assert.Equal(english, TaskText.DueLabel(task, now, TestLoc.En()));
    }

    [Fact]
    public void Every_validation_code_status_priority_view_and_sort_has_a_text()
    {
        foreach (var loc in new[] { TestLoc.Ro(), TestLoc.En() })
        {
            foreach (var code in typeof(ValidationCodes).GetFields().Select(f => (string)f.GetValue(null)!))
            {
                Assert.True(loc.TryGet($"Validation.{code}", out _), $"Validation.{code}");
            }

            foreach (var status in Tasks.Statuses())
            {
                Assert.DoesNotContain("⟦", TaskText.Status(status, loc));
            }

            Assert.All(Enum.GetValues<Priority>(), p => Assert.DoesNotContain("⟦", TaskText.Priority(p, loc)));
            Assert.All(Enum.GetValues<TaskView>(), v => Assert.DoesNotContain("⟦", TaskText.View(v, loc) + loc[$"Tasks.Empty.{v}.Title"] + loc[$"Tasks.Empty.{v}.Description"]));
            Assert.All(Enum.GetValues<TaskSort>(), s => Assert.DoesNotContain("⟦", TaskText.Sort(s, loc)));
            Assert.Equal(loc["Action.Other"], TaskText.Action("something.new", loc));
        }
    }

    private TasksPageViewModel CreatePage() => new(Tasks, _kit.Bus, Clock, _loc, _toasts, _launcher, _kit.Reminders);

    private TaskDetailsViewModel OpenDetails(string id)
    {
        var page = CreatePage();
        page.Select(id);
        return Assert.IsType<TaskDetailsViewModel>(page.Details);
    }

    private ShellViewModel CreateShell()
    {
        var services = new PageServices(new FakeThemeService(), Clock, new FakeDataMaintenance(), _launcher, _loc, Tasks, _kit.Bus, _toasts, _kit.Reminders);
        return new ShellViewModel(new FakeThemeService(), Clock, section => PageFactory.Create(section, services), _loc, Tasks, _toasts, new FinanceOS.App.Reminders.NotificationCenterViewModel(_kit.Scheduler, Clock, _loc));
    }
}

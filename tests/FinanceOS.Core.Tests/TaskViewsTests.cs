using FinanceOS.Core.Tasks;
using Xunit;

namespace FinanceOS.Core.Tests;

public sealed class TaskViewsTests
{
    // Monday 9 November 2026, 17:00 local.
    private static readonly DateTime Now = new(2026, 11, 9, 17, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private static readonly TaskStatusInfo[] Statuses =
    [
        new(SystemStatuses.Todo, "To do", TaskStatusKind.Todo, 0, true),
        new(SystemStatuses.Waiting, "Waiting", TaskStatusKind.Waiting, 2, true),
        new(SystemStatuses.Done, "Done", TaskStatusKind.Done, 4, true),
        new(SystemStatuses.Cancelled, "Cancelled", TaskStatusKind.Cancelled, 5, true),
    ];

    private int _sort;

    [Fact]
    public void Each_view_shows_the_right_open_or_closed_tasks()
    {
        TaskItem[] tasks =
        [
            Task("no due"),
            Task("today, no time", Today),
            Task("today, later", Today, new TimeOnly(18, 0)),
            Task("today, time passed", Today, new TimeOnly(9, 0)),
            Task("yesterday", Today.AddDays(-1)),
            Task("tomorrow", Today.AddDays(1)),
            Task("waiting tomorrow", Today.AddDays(1)) with { StatusId = SystemStatuses.Waiting },
            Task("done today", Today) with { StatusId = SystemStatuses.Done, CompletedAt = At(10) },
            Task("cancelled", Today.AddDays(-3)) with { StatusId = SystemStatuses.Cancelled },
            Task("archived", Today) with { ArchivedAt = DateTimeOffset.UnixEpoch },
            Task("trashed", Today) with { DeletedAt = DateTimeOffset.UnixEpoch },
        ];

        AssertSame(["no due", "today, no time", "today, later", "today, time passed", "yesterday", "tomorrow", "waiting tomorrow"], Titles(tasks, TaskView.All));
        AssertSame(["today, no time", "today, later", "today, time passed"], Titles(tasks, TaskView.Today));
        AssertSame(["tomorrow", "waiting tomorrow"], Titles(tasks, TaskView.Upcoming));
        AssertSame(["today, time passed", "yesterday"], Titles(tasks, TaskView.Overdue));
        AssertSame(["done today", "cancelled"], Titles(tasks, TaskView.Completed));

        var counts = TaskViews.Counts(tasks, Statuses, Now);
        Assert.Equal(7, counts[TaskView.All]);
        Assert.Equal(3, counts[TaskView.Today]);
        Assert.Equal(2, counts[TaskView.Upcoming]);
        Assert.Equal(2, counts[TaskView.Overdue]);
        Assert.Equal(2, counts[TaskView.Completed]);
    }

    [Fact]
    public void Overdue_starts_the_minute_after_the_due_time()
    {
        var at = Task("at 17:00", Today, new TimeOnly(17, 0));
        Assert.False(TaskViews.IsOverdue(at, Now));
        Assert.True(TaskViews.IsOverdue(at, Now.AddMinutes(1)));

        // Without a time, a task is due all day: overdue only from the next day.
        var allDay = Task("today, no time", Today);
        Assert.False(TaskViews.IsOverdue(allDay, Today.ToDateTime(new TimeOnly(23, 59))));
        Assert.True(TaskViews.IsOverdue(allDay, Today.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        Assert.False(TaskViews.IsOverdue(Task("no due"), Now));
    }

    [Fact]
    public void Smart_sort_puts_pinned_then_overdue_then_by_due_then_by_priority()
    {
        TaskItem[] tasks =
        [
            Task("no due, urgent") with { Priority = Priority.Urgent },
            Task("next week", Today.AddDays(7)),
            Task("tomorrow low", Today.AddDays(1)) with { Priority = Priority.Low },
            Task("tomorrow high", Today.AddDays(1)) with { Priority = Priority.High },
            Task("overdue", Today.AddDays(-4)),
            Task("pinned, later", Today.AddDays(30)) with { IsPinned = true },
            Task("in 2 days, 10:00", Today.AddDays(2), new TimeOnly(10, 0)),
            Task("in 2 days, no time", Today.AddDays(2)),
        ];

        Assert.Equal(
            ["pinned, later", "overdue", "tomorrow high", "tomorrow low", "in 2 days, 10:00", "in 2 days, no time", "next week", "no due, urgent"],
            Titles(tasks, TaskView.All));
    }

    [Fact]
    public void Other_sorts_order_by_what_they_say()
    {
        TaskItem[] tasks =
        [
            Task("Beta", Today.AddDays(3)) with { Priority = Priority.Low, CreatedAt = At(1) },
            Task("alpha", Today.AddDays(1)) with { Priority = Priority.Medium, CreatedAt = At(3) },
            Task("Gamma") with { Priority = Priority.Urgent, CreatedAt = At(2) },
        ];

        Assert.Equal(["alpha", "Beta", "Gamma"], Titles(tasks, TaskView.All, TaskSort.DueDate));
        Assert.Equal(["Gamma", "alpha", "Beta"], Titles(tasks, TaskView.All, TaskSort.Priority));
        Assert.Equal(["alpha", "Beta", "Gamma"], Titles(tasks, TaskView.All, TaskSort.Title));
        Assert.Equal(["alpha", "Gamma", "Beta"], Titles(tasks, TaskView.All, TaskSort.Created));
    }

    [Fact]
    public void Completed_shows_the_most_recently_finished_first()
    {
        TaskItem[] tasks =
        [
            Task("first") with { StatusId = SystemStatuses.Done, CompletedAt = At(1) },
            Task("last") with { StatusId = SystemStatuses.Done, CompletedAt = At(5) },
            Task("middle") with { StatusId = SystemStatuses.Done, CompletedAt = At(3) },
        ];

        Assert.Equal(["last", "middle", "first"], Titles(tasks, TaskView.Completed));
    }

    private static void AssertSame(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    private static List<string> Titles(IEnumerable<TaskItem> tasks, TaskView view, TaskSort sort = TaskSort.Smart) =>
        [.. TaskViews.Apply(tasks, view, sort, Statuses, Now).Select(t => t.Title)];

    private static DateTimeOffset At(int hour) => new(2026, 11, 1, hour, 0, 0, TimeSpan.Zero);

    private TaskItem Task(string title, DateOnly? due = null, TimeOnly? time = null) => new()
    {
        Id = title,
        Title = title,
        DueDate = due,
        DueTime = time,
        SortOrder = ++_sort,
        CreatedAt = At(0),
        UpdatedAt = At(0),
    };
}

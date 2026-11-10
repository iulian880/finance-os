using System.Globalization;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Tasks;

/// <summary>How a due date reads in a list: plain, today, or overdue (colors the label).</summary>
public enum DueState
{
    None,
    Normal,
    Today,
    Overdue,
}

/// <summary>Task texts in the UI language: due labels, priorities, statuses, validation messages, action names.</summary>
public static class TaskText
{
    /// <summary>
    /// "Azi, 14:30", "Mâine", "Ieri", a weekday name for the next six days, "13 nov." this year,
    /// "13 nov. 2027" otherwise. Null without a due date.
    /// </summary>
    public static string? DueLabel(TaskItem task, DateTime localNow, Localizer l)
    {
        if (task.DueDate is not { } due)
        {
            return null;
        }

        var culture = l.Culture;
        var today = DateOnly.FromDateTime(localNow);
        string day;
        if (due == today)
        {
            day = l["Due.Today"];
        }
        else if (due == today.AddDays(1))
        {
            day = l["Due.Tomorrow"];
        }
        else if (due == today.AddDays(-1))
        {
            day = l["Due.Yesterday"];
        }
        else if (due > today && due <= today.AddDays(6))
        {
            day = Capitalize(due.ToString("dddd", culture), culture);
        }
        else
        {
            day = due.ToString(due.Year == today.Year ? "d MMM" : "d MMM yyyy", culture);
        }

        return task.DueTime is { } time ? $"{day}, {time.ToString("HH:mm", CultureInfo.InvariantCulture)}" : day;
    }

    public static DueState Due(TaskItem task, bool isClosed, DateTime localNow)
    {
        if (task.DueDate is not { } due)
        {
            return DueState.None;
        }

        if (isClosed)
        {
            return DueState.Normal;
        }

        if (TaskViews.IsOverdue(task, localNow))
        {
            return DueState.Overdue;
        }

        return due == DateOnly.FromDateTime(localNow) ? DueState.Today : DueState.Normal;
    }

    public static string Priority(Priority priority, Localizer l) => l[$"Priority.{priority}"];

    /// <summary>System statuses by id in the UI language; custom statuses (later) by their own name.</summary>
    public static string Status(TaskStatusInfo status, Localizer l) =>
        l.TryGet($"Status.{status.Id}", out var text) ? text : status.Name;

    public static string Validation(ValidationError error, Localizer l) =>
        l.TryGet($"Validation.{error.Code}", out var text) ? text : error.Code;

    /// <summary>Name of a command for the "Undone: …" toast; unknown commands get a generic name.</summary>
    public static string Action(string command, Localizer l) =>
        l.TryGet($"Action.{command}", out var text) ? text : l["Action.Other"];

    public static string View(TaskView view, Localizer l) => l[$"Tasks.View.{view}"];

    public static string Sort(TaskSort sort, Localizer l) => l[$"Tasks.Sort.{sort}"];

    private static string Capitalize(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
}

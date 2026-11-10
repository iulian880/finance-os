using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Onboarding;

/// <summary>
/// Sample tasks offered at the end of onboarding, in the interface language. Every task, category,
/// project and tag is marked <c>is_demo</c>, so Settings removes them all in one step without touching
/// real data. No reminders: sample data never pops up a notification.
/// </summary>
public static class DemoData
{
    /// <summary>Adds the sample tasks; returns how many. Names the user already has are not reused or duplicated.</summary>
    public static int Add(TaskService tasks, TimeProvider time, Localizer l)
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var close = Name(tasks, TaxonomyKind.Category, l["Demo.Category.Close"]);
        var tax = Name(tasks, TaxonomyKind.Category, l["Demo.Category.Tax"]);
        var audit = Name(tasks, TaxonomyKind.Project, l["Demo.Project.Audit"]);
        var urgent = Name(tasks, TaxonomyKind.Tag, l["Demo.Tag.Urgent"]);

        var created = new List<TaskItem>();
        TaskItem Add(TaskDraft draft)
        {
            var task = tasks.Create(draft with { IsDemo = true });
            created.Add(task);
            return task;
        }

        var monthEnd = Add(new TaskDraft(l["Demo.Close.Title"])
        {
            Description = l["Demo.Close.Description"],
            CategoryId = close,
            DueDate = today.AddDays(2),
            Priority = Priority.High,
            IsPinned = true,
            Checklist = [l["Demo.Close.Item1"], l["Demo.Close.Item2"], l["Demo.Close.Item3"]],
        });
        Add(new TaskDraft(l["Demo.Bank.Title"]) { ParentId = monthEnd.Id, CategoryId = close, DueDate = today.AddDays(1) });
        Add(new TaskDraft(l["Demo.Vat.Title"])
        {
            CategoryId = tax,
            DueDate = today.AddDays(5),
            DueTime = new TimeOnly(12, 0),
            Priority = Priority.Urgent,
            Tags = urgent is null ? [] : [urgent],
        });
        Add(new TaskDraft(l["Demo.Expenses.Title"]) { DueDate = today });
        Add(new TaskDraft(l["Demo.Audit.Title"])
        {
            ProjectId = audit,
            Requester = l["Demo.Audit.Requester"],
            DueDate = today.AddDays(7),
            Priority = Priority.High,
        });
        Add(new TaskDraft(l["Demo.Budget.Title"]) { Priority = Priority.Low });
        return created.Count;
    }

    /// <summary>A new sample name, or null when the user already has one with that name (it stays theirs).</summary>
    private static string? Name(TaskService tasks, TaxonomyKind kind, string name) =>
        tasks.Taxonomy(kind).Any(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase))
            ? null
            : tasks.CreateTaxonomy(kind, name, isDemo: true).Id;
}

using FinanceOS.Core.Tasks;

namespace FinanceOS.App.Dev;

/// <summary>
/// Debug builds only (<c>--seed</c>, use with <c>--data-root</c>): fills an empty database with a
/// handful of realistic finance tasks for screenshots and manual testing. Never runs on data that
/// already has tasks. Real demo data (marked is_demo, removable in one click) comes with onboarding in M12.
/// </summary>
internal static class DevSeed
{
    public static void Run(TaskService tasks, FinanceOS.Core.Reminders.ReminderService reminders, TimeProvider time)
    {
        if (tasks.List(TaskScope.All).Count > 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var close = tasks.CreateTaxonomy(TaxonomyKind.Category, "Month-end close");
        var tax = tasks.CreateTaxonomy(TaxonomyKind.Category, "Tax");
        var audit = tasks.CreateTaxonomy(TaxonomyKind.Project, "Audit 2026");
        var urgent = tasks.GetOrCreateTag("urgent");
        var ic = tasks.GetOrCreateTag("intercompany");

        var monthEnd = tasks.Create(new TaskDraft("Month-end close November")
        {
            CategoryId = close.Id,
            DueDate = today.AddDays(2),
            Priority = Priority.High,
            IsPinned = true,
            Checklist = ["Accruals posted", "Bank reconciliations", "Fixed asset depreciation", "Review P&L variance"],
            Description = "Close the books for November and send the flash report.",
        });
        tasks.SetChecklistItemDone(monthEnd.Id, monthEnd.Checklist[0].Id, true);

        // Reminders in the future only: a seed must never pop up a toast on the desktop.
        reminders.AddRelative(monthEnd.Id, 1440);
        reminders.AddRelative(monthEnd.Id, 60);
        tasks.Create(new TaskDraft("Intercompany balances with RO02") { ParentId = monthEnd.Id, DueDate = today.AddDays(1), Tags = [ic.Id] });
        tasks.Create(new TaskDraft("Send flash report to CFO") { ParentId = monthEnd.Id, DueDate = today.AddDays(2), DueTime = new TimeOnly(16, 0) });

        tasks.Create(new TaskDraft("VAT return Q4") { CategoryId = tax.Id, DueDate = today, DueTime = new TimeOnly(14, 30), Priority = Priority.Urgent, Tags = [urgent.Id] });
        tasks.Create(new TaskDraft("Reply to auditor questions on inventory") { ProjectId = audit.Id, DueDate = today.AddDays(-2), Priority = Priority.High, Requester = "Auditor" });
        tasks.Create(new TaskDraft("Approve supplier invoices batch 47") { DueDate = today, ExternalRef = "AP-47" });
        tasks.Create(new TaskDraft("Update cash forecast") { DueDate = today.AddDays(5), Priority = Priority.Medium });
        tasks.Create(new TaskDraft("Prepare PBC list for audit") { ProjectId = audit.Id, DueDate = today.AddDays(12) });
        tasks.Create(new TaskDraft("Read new IFRS 18 summary"));
        var done = tasks.Create(new TaskDraft("Payroll journal October") { CategoryId = close.Id, DueDate = today.AddDays(-5) });
        tasks.Complete(done.Id);
        tasks.ClearUndo();
    }
}

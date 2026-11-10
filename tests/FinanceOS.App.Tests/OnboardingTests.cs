using System.IO;
using FinanceOS.App.Onboarding;
using FinanceOS.App.Reminders;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;
using Xunit;

namespace FinanceOS.App.Tests;

/// <summary>M12c: first-run guide and sample data that never mixes with real data.</summary>
public sealed class OnboardingTests : IDisposable
{
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(2)));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));
    private readonly TaskKit _kit = new(Clock);
    private readonly Localizer _loc = TestLoc.Ro();
    private readonly FakeToasts _toasts = new();
    private readonly UserSettings _settings;

    public OnboardingTests() => _settings = new UserSettings(new PreferencesStore(Path.Combine(_root, "preferences.json")));

    public void Dispose()
    {
        _kit.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Sample_data_is_marked_and_in_the_interface_language()
    {
        var count = DemoData.Add(_kit.Tasks, Clock, _loc);

        var tasks = _kit.Tasks.List(TaskScope.All);
        Assert.Equal(6, count);
        Assert.Equal(6, tasks.Count);
        Assert.All(tasks, t => Assert.True(t.IsDemo));
        Assert.Contains(tasks, t => t.Title == "Plata TVA");
        Assert.All(Enum.GetValues<TaxonomyKind>().SelectMany(_kit.Tasks.Taxonomy), n => Assert.True(n.IsDemo));
        Assert.True(_kit.Tasks.HasDemoData);
        var row = new FinanceOS.App.Tasks.TaskRowViewModel(tasks[0].Id);
        new FinanceOS.App.Tasks.TaskSnapshot(_kit.Tasks, Clock, _loc).Fill(row, tasks[0]);
        Assert.True(row.IsDemo); // lists show the "Exemplu" badge
        Assert.All(tasks, t => Assert.Empty(_kit.Reminders.For(t.Id))); // samples never pop up a notification
    }

    [Fact]
    public void Removing_sample_data_keeps_real_tasks_and_only_detaches_them()
    {
        var mine = _kit.Tasks.CreateTaxonomy(TaxonomyKind.Category, "Taxe"); // same name as a sample category
        DemoData.Add(_kit.Tasks, Clock, _loc);
        var vat = _kit.Tasks.List().Single(t => t.Title == "Plata TVA");
        Assert.Null(vat.CategoryId); // the user's "Taxe" is not borrowed by a sample
        var sampleParent = _kit.Tasks.List().Single(t => t.Title == "Închiderea contabilă a lunii");
        var auditProject = _kit.Tasks.Taxonomy(TaxonomyKind.Project).Single();
        var urgent = _kit.Tasks.Taxonomy(TaxonomyKind.Tag).Single();
        var real = _kit.Tasks.Create(new TaskDraft("Real one") { CategoryId = mine.Id, ProjectId = auditProject.Id, Tags = [urgent.Id] });
        var realChild = _kit.Tasks.Create(new TaskDraft("My subtask") { ParentId = sampleParent.Id });

        var removed = _kit.Tasks.RemoveDemoData();

        Assert.Equal(6, removed);
        Assert.False(_kit.Tasks.HasDemoData);
        Assert.Equal(["My subtask", "Real one"], _kit.Tasks.List(TaskScope.All).Select(t => t.Title).Order());
        var kept = _kit.Tasks.Get(real.Id)!;
        Assert.Equal((mine.Id, (string?)null), (kept.CategoryId, kept.ProjectId));
        Assert.Empty(kept.Tags);
        Assert.Null(_kit.Tasks.Get(realChild.Id)!.ParentId);
        Assert.Equal(["Taxe"], _kit.Tasks.Taxonomy(TaxonomyKind.Category).Select(c => c.Name));

        _kit.Tasks.Undo(); // one step back brings everything back
        Assert.True(_kit.Tasks.HasDemoData);
        Assert.Equal(8, _kit.Tasks.List(TaskScope.All).Count);
        Assert.Equal(sampleParent.Id, _kit.Tasks.Get(realChild.Id)!.ParentId);
    }

    [Fact]
    public void Guide_walks_five_steps_and_saves_the_choices()
    {
        var finished = -1;
        var guide = CreateGuide(hasTasks: false, done: n => finished = n);

        Assert.True(guide.IsWelcome);
        Assert.True(guide.IsFirst);
        Assert.True(guide.AddDemoData); // empty database: samples offered and pre-checked
        Assert.Equal("Pasul 1 din 5", guide.Progress);
        Assert.Equal("Continuă", guide.NextLabel);

        guide.NextCommand.Execute(null);
        Assert.True(guide.IsModeStep);
        Assert.True(guide.IsSimple);
        guide.IsAdvanced = true;
        Assert.Equal(ExperienceMode.Advanced, _settings.Mode);
        Assert.False(guide.IsSimple);

        guide.NextCommand.Execute(null);
        guide.NextCommand.Execute(null);
        Assert.True(guide.IsHoursStep);
        guide.Settings.WorkStart = new TimeSpan(8, 0, 0);
        guide.BackCommand.Execute(null);
        Assert.True(guide.IsThemeStep);
        guide.NextCommand.Execute(null);
        guide.NextCommand.Execute(null);
        Assert.True(guide.IsNotificationsStep);
        Assert.Equal("Deschide Finance OS", guide.NextLabel);

        guide.NextCommand.Execute(null);

        Assert.Equal(6, finished);
        var saved = new UserSettings(new PreferencesStore(Path.Combine(_root, "preferences.json")));
        Assert.True(saved.Current.Onboarded);
        Assert.Equal(ExperienceMode.Advanced, saved.Mode);
        Assert.Equal(new TimeOnly(8, 0), saved.WorkStart);
    }

    [Fact]
    public void Skip_keeps_defaults_and_adds_no_samples()
    {
        var finished = -1;
        var guide = CreateGuide(hasTasks: true, done: n => finished = n);
        Assert.False(guide.AddDemoData); // the user already has tasks

        guide.SkipCommand.Execute(null);

        Assert.Equal(0, finished);
        Assert.True(_settings.Current.Onboarded);
        Assert.False(_kit.Tasks.HasDemoData);
    }

    [Fact]
    public void Shell_shows_the_guide_and_lands_on_the_overview_with_a_toast()
    {
        var services = new PageServices(new FakeThemeService(), Clock, new FakeDataMaintenance(), new FakeLauncher(), _loc, _kit.Tasks, _kit.Bus, _toasts, _kit.Reminders, null, _settings);
        var shell = new ShellViewModel(new FakeThemeService(), Clock, s => PageFactory.Create(s, services), _loc, _kit.Tasks, _toasts,
            new NotificationCenterViewModel(_kit.Scheduler, Clock, _loc), _settings);
        shell.NavigateCommand.Execute(Section.Tasks);

        shell.StartOnboarding();
        var guide = Assert.IsType<OnboardingViewModel>(shell.Onboarding);
        for (var i = 0; i < OnboardingViewModel.StepCount; i++)
        {
            guide.NextCommand.Execute(null);
        }

        Assert.Null(shell.Onboarding);
        Assert.Equal(Section.Overview, shell.CurrentSection);
        Assert.Equal("Am adăugat taskuri exemplu", Assert.Single(_toasts.Shown).Title);

        // Settings offers to remove them, in one click.
        shell.NavigateCommand.Execute(Section.Settings);
        var settings = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
        Assert.True(settings.HasDemoData);
        settings.RemoveDemoDataCommand.Execute(null);
        Assert.False(settings.HasDemoData);
        Assert.Empty(_kit.Tasks.List(TaskScope.All));
    }

    [Fact]
    public void Without_saved_settings_there_is_no_guide()
    {
        var services = new PageServices(new FakeThemeService(), Clock, new FakeDataMaintenance(), new FakeLauncher(), _loc, _kit.Tasks, _kit.Bus, _toasts, _kit.Reminders);
        var shell = new ShellViewModel(new FakeThemeService(), Clock, s => PageFactory.Create(s, services), _loc, _kit.Tasks, _toasts, new NotificationCenterViewModel(_kit.Scheduler, Clock, _loc));

        shell.StartOnboarding();

        Assert.Null(shell.Onboarding);
    }

    private OnboardingViewModel CreateGuide(bool hasTasks, Action<int> done)
    {
        var page = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Clock, _loc, null, _settings, _kit.Tasks);
        return new OnboardingViewModel(page, _settings, _loc, hasTasks, () => DemoData.Add(_kit.Tasks, Clock, _loc), done);
    }
}

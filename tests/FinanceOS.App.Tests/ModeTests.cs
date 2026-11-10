using System.Globalization;
using System.IO;
using System.Text.Json;
using FinanceOS.App.Reminders;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Localization;
using Xunit;

namespace FinanceOS.App.Tests;

/// <summary>M12b: Simple / Advanced mode and the date format.</summary>
public sealed class ModeTests : IDisposable
{
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(2)));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));
    private readonly TaskKit _kit = new(Clock);
    private readonly Localizer _loc = TestLoc.Ro();
    private readonly UserSettings _settings;

    public ModeTests() => _settings = new UserSettings(new PreferencesStore(Path.Combine(_root, "preferences.json")));

    public void Dispose()
    {
        _kit.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void New_users_start_in_simple_mode_and_the_choice_persists()
    {
        Assert.Equal(ExperienceMode.Simple, _settings.Mode);
        Assert.False(_settings.Capabilities.Shows(Section.Workflows));
        Assert.True(_settings.Capabilities.Shows(Section.Calendar));

        _settings.SetMode(ExperienceMode.Advanced);
        _settings.Update(p => p with { DateFormat = "yyyy-MM-dd" });

        var next = new UserSettings(new PreferencesStore(Path.Combine(_root, "preferences.json")));
        Assert.Equal(ExperienceMode.Advanced, next.Mode);
        Assert.Equal("yyyy-MM-dd", next.DateFormat);
        Assert.True(next.Capabilities.Shows(Section.Analytics));
    }

    [Fact]
    public void Simple_mode_hides_the_advanced_sections_everywhere_in_the_shell()
    {
        var shell = CreateShell();

        Assert.Equal(["Overview", "MyDay", "Tasks", "Calendar", "Reminders", "Settings"], Visible(shell));
        Assert.DoesNotContain(shell.Commands, c => Equals(c.Parameter, Section.Projects));
        Assert.Contains(shell.Commands, c => c.Title == "Treci la modul Avansat");

        shell.NavigateCommand.Execute(Section.Workflows); // Ctrl+6 in Simple: nothing happens
        Assert.Equal(Section.Overview, shell.CurrentSection);

        shell.Commands.Single(c => c.Title == "Treci la modul Avansat").Command.Execute(null);
        Assert.Equal(10, Visible(shell).Count);
        shell.NavigateCommand.Execute(Section.Archive);
        Assert.Equal(Section.Archive, shell.CurrentSection);

        _settings.SetMode(ExperienceMode.Simple); // from Settings while the archive is open
        Assert.Equal(Section.Overview, shell.CurrentSection);
        Assert.Contains(shell.Commands, c => c.Title == "Treci la modul Avansat");
    }

    [Fact]
    public void Switching_modes_never_changes_task_data()
    {
        var project = _kit.Tasks.CreateTaxonomy(TaxonomyKind.Project, "Year-end");
        _kit.Tasks.Create(new TaskDraft("Invoice 42") { ProjectId = project.Id, Requester = "Ana", ExternalUrl = "https://example.com/42", DueDate = new DateOnly(2026, 11, 12) });
        _kit.Tasks.Create(new TaskDraft("Plain"));
        var before = Snapshot();
        var undo = _kit.Tasks.UndoCommand;
        var shell = CreateShell();
        var tasks = new TasksPageViewModel(_kit.Tasks, _kit.Bus, Clock, _loc, new FakeToasts(), new FakeLauncher(), _kit.Reminders) { Settings = _settings };
        tasks.Select(_kit.Tasks.List().Single(t => t.Title == "Invoice 42").Id);

        foreach (var mode in new[] { ExperienceMode.Advanced, ExperienceMode.Simple, ExperienceMode.Advanced, ExperienceMode.Simple })
        {
            _settings.SetMode(mode);
        }

        Assert.Equal(before, Snapshot());
        Assert.Equal(undo, _kit.Tasks.UndoCommand); // nothing new to undo: no write happened
        Assert.Equal(Section.Overview, shell.CurrentSection);
    }

    [Fact]
    public void Simple_mode_keeps_filled_in_advanced_fields_visible_and_read_only()
    {
        var project = _kit.Tasks.CreateTaxonomy(TaxonomyKind.Project, "Audit");
        var filled = _kit.Tasks.Create(new TaskDraft("With references") { ProjectId = project.Id, Responsible = "Mihai" });
        var empty = _kit.Tasks.Create(new TaskDraft("Without"));
        var page = new TasksPageViewModel(_kit.Tasks, _kit.Bus, Clock, _loc, new FakeToasts(), new FakeLauncher(), _kit.Reminders) { Settings = _settings };

        page.Select(empty.Id);
        var details = Assert.IsType<TaskDetailsViewModel>(page.Details);
        Assert.False(details.AdvancedFields);
        Assert.False(details.ShowProject);
        Assert.False(details.ShowReferences);

        page.Select(filled.Id);
        details = Assert.IsType<TaskDetailsViewModel>(page.Details);
        Assert.True(details.ShowProject);
        Assert.True(details.ShowReferences);
        Assert.True(details.ReferencesReadOnly);

        _settings.SetMode(ExperienceMode.Advanced); // the open panel follows
        Assert.True(details.AdvancedFields);
        Assert.False(details.ReferencesReadOnly);
    }

    [Fact]
    public void Without_saved_settings_everything_is_shown()
    {
        var services = new PageServices(new FakeThemeService(), Clock, new FakeDataMaintenance(), new FakeLauncher(), _loc, _kit.Tasks, _kit.Bus, new FakeToasts(), _kit.Reminders);
        var shell = new ShellViewModel(new FakeThemeService(), Clock, s => PageFactory.Create(s, services), _loc, _kit.Tasks, new FakeToasts(), new NotificationCenterViewModel(_kit.Scheduler, Clock, _loc));

        Assert.Equal(10, Visible(shell).Count);
        Assert.DoesNotContain(shell.Commands, c => c.Title.Contains("modul", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_page_switches_mode_and_date_format()
    {
        var page = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Clock, _loc, null, _settings);

        Assert.True(page.HasPreferences);
        Assert.Equal("© 2026 Petcu Iulian. Toate drepturile rezervate.", page.CopyrightText);
        Assert.Equal(ExperienceMode.Simple, page.SelectedMode!.Value);
        Assert.StartsWith("Ziua mea", page.ModeDescription, StringComparison.Ordinal);
        Assert.Equal(["09.11.2026", "09/11/2026", "2026-11-09", "11/09/2026"], page.DateFormatOptions.Skip(1).Select(o => o.Label));
        Assert.StartsWith("Ca în Windows (", page.DateFormatOptions[0].Label, StringComparison.Ordinal);

        page.SelectedMode = page.ModeOptions.Single(o => o.Value == ExperienceMode.Advanced);
        page.SelectedDateFormat = page.DateFormatOptions.Single(o => o.Value == "MM/dd/yyyy");

        Assert.Equal(ExperienceMode.Advanced, _settings.Mode);
        Assert.Equal("MM/dd/yyyy", _settings.DateFormat);
        Assert.Equal("Toate secțiunile și toate câmpurile taskului.", page.ModeDescription);

        _settings.SetMode(ExperienceMode.Simple); // from the palette: the page follows
        Assert.Equal(ExperienceMode.Simple, page.SelectedMode!.Value);
    }

    [Fact]
    public void Date_format_keeps_the_interface_language_for_names()
    {
        var ro = CultureInfo.GetCultureInfo("ro-RO");
        var date = new DateTime(2026, 11, 9);

        var iso = DateFormats.Build(ro, "yyyy-MM-dd");
        Assert.Equal("2026-11-09", date.ToString("d", iso));
        Assert.Equal("noiembrie", iso.DateTimeFormat.GetMonthName(11));
        Assert.Equal("11/09/2026", date.ToString("d", DateFormats.Build(CultureInfo.GetCultureInfo("en-GB"), "MM/dd/yyyy")));
        Assert.Null(DateFormats.Normalize("dd-MMM")); // unknown pattern: like Windows
        Assert.Equal(DateFormats.Example(date, ro, null), DateFormats.Example(date, ro, "nonsense"));
    }

    private static List<string> Visible(ShellViewModel shell) => [.. shell.NavItems.Where(i => i.IsVisible).Select(i => i.Section.ToString())];

    private string Snapshot() => JsonSerializer.Serialize(_kit.Tasks.List(TaskScope.Open));

    private ShellViewModel CreateShell()
    {
        var services = new PageServices(new FakeThemeService(), Clock, new FakeDataMaintenance(), new FakeLauncher(), _loc, _kit.Tasks, _kit.Bus, new FakeToasts(), _kit.Reminders, null, _settings);
        return new ShellViewModel(new FakeThemeService(), Clock, s => PageFactory.Create(s, services), _loc, _kit.Tasks, new FakeToasts(), new NotificationCenterViewModel(_kit.Scheduler, Clock, _loc), _settings);
    }
}

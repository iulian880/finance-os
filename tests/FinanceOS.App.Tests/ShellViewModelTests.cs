using System.ComponentModel;
using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;
using Xunit;

namespace FinanceOS.App.Tests;

public sealed class ShellViewModelTests : IDisposable
{
    // Wednesday, 4 November 2026, 09:30 local (Bucharest winter time).
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.FromHours(2)));

    private readonly FakeThemeService _theme = new();
    private readonly List<Section> _created = [];
    private readonly Localizer _loc = TestLoc.Ro();
    private readonly TaskKit _kit = new(Clock);
    private readonly FakeToasts _toasts = new();

    public void Dispose() => _kit.Dispose();

    private ShellViewModel CreateShell()
    {
        var services = new PageServices(_theme, Clock, new FakeDataMaintenance(), new FakeLauncher(), _loc, _kit.Tasks, _kit.Bus, _toasts, _kit.Reminders);
        return new(_theme, Clock, section =>
        {
            _created.Add(section);
            return PageFactory.Create(section, services);
        }, _loc, _kit.Tasks, _toasts, new FinanceOS.App.Reminders.NotificationCenterViewModel(_kit.Scheduler, Clock, _loc));
    }

    [Fact]
    public void Starts_on_overview_with_all_ten_sections_in_sidebar_order()
    {
        var shell = CreateShell();

        Assert.Equal(Enum.GetValues<Section>(), shell.NavItems.Select(i => i.Section));
        Assert.Equal(Section.Overview, shell.CurrentSection);
        Assert.Same(shell.NavItems[0], shell.SelectedItem);
        Assert.IsType<OverviewPageViewModel>(shell.CurrentPage);
        Assert.Equal([Section.Overview], _created);
    }

    [Fact]
    public void Only_settings_sits_in_the_sidebar_footer()
    {
        var footer = CreateShell().NavItems.Where(i => i.IsFooter).Select(i => i.Section);
        Assert.Equal([Section.Settings], footer);
    }

    [Fact]
    public void Today_text_is_the_day_and_month_in_the_ui_language()
    {
        var shell = CreateShell();
        Assert.Equal("Miercuri, 4 noiembrie", shell.TodayText);

        _loc.SetLanguage(AppLanguage.English);
        Assert.Equal("Wednesday, 4 November", shell.TodayText);
    }

    [Fact]
    public void Switching_language_renames_sidebar_panel_and_palette_without_a_restart()
    {
        var shell = CreateShell();
        shell.ToggleNotificationsCommand.Execute(null);
        var settingsItem = shell.NavItems.Single(i => i.Section == Section.Settings);
        var itemChanges = RecordChanges(settingsItem);
        var shellChanges = RecordChanges(shell);
        Assert.Equal("Setări", settingsItem.Title);
        Assert.Equal("Organizare", shell.NavItems.Single(i => i.Section == Section.Workflows).GroupHeader);

        _loc.SetLanguage(AppLanguage.English);

        Assert.Equal("Settings", settingsItem.Title);
        Assert.Contains(nameof(NavItemViewModel.Title), itemChanges);
        Assert.Equal("Organize", shell.NavItems.Single(i => i.Section == Section.Workflows).GroupHeader);
        Assert.Equal("Notifications", shell.RightPanelTitle);
        Assert.Contains(nameof(ShellViewModel.RightPanelTitle), shellChanges);
        Assert.Contains(nameof(ShellViewModel.TodayText), shellChanges);
        Assert.Contains(nameof(ShellViewModel.Commands), shellChanges);
        Assert.Contains(shell.Commands, c => c.Title == "Go to Settings" && c.Group == "Navigation");

        // The open page refreshes too (empty string = every property).
        var pageChanges = RecordChanges(shell.CurrentPage);
        _loc.SetLanguage(AppLanguage.Romanian);
        Assert.Contains(string.Empty, pageChanges);
        Assert.Equal("Prezentare", shell.CurrentPage.Title);
    }

    [Fact]
    public void Navigate_switches_page_and_selection_and_notifies_current_section()
    {
        var shell = CreateShell();
        var changed = RecordChanges(shell);

        shell.NavigateCommand.Execute(Section.Tasks);

        Assert.Equal(Section.Tasks, shell.CurrentSection);
        Assert.Equal(Section.Tasks, shell.SelectedItem?.Section);
        Assert.IsType<FinanceOS.App.Tasks.TasksPageViewModel>(shell.CurrentPage);
        Assert.Contains(nameof(ShellViewModel.CurrentPage), changed);
        Assert.Contains(nameof(ShellViewModel.CurrentSection), changed);
    }

    [Fact]
    public void Pages_are_created_once_and_keep_their_state_between_visits()
    {
        var shell = CreateShell();
        var settings = Navigate(shell, Section.Settings);

        Navigate(shell, Section.Calendar);
        Navigate(shell, Section.Overview);

        Assert.Same(settings, Navigate(shell, Section.Settings));
        Assert.Equal([Section.Overview, Section.Settings, Section.Calendar], _created);
    }

    [Fact]
    public void Selecting_a_sidebar_item_directly_navigates_like_the_command()
    {
        var shell = CreateShell();
        shell.SelectedItem = shell.NavItems.Single(i => i.Section == Section.Archive);
        Assert.Equal(Section.Archive, shell.CurrentSection);
    }

    [Fact]
    public void Toggle_sidebar_flips_collapsed_and_expanded()
    {
        var shell = CreateShell();
        Assert.True(shell.IsSidebarExpanded);

        shell.ToggleSidebarCommand.Execute(null);
        Assert.True(shell.IsSidebarCollapsed);
        Assert.False(shell.IsSidebarExpanded);

        shell.ToggleSidebarCommand.Execute(null);
        Assert.True(shell.IsSidebarExpanded);
    }

    [Fact]
    public void Right_panel_toggles_open_and_closed_for_the_same_kind()
    {
        var shell = CreateShell();
        Assert.False(shell.IsRightPanelOpen);

        shell.ToggleDetailsCommand.Execute(null);
        Assert.True(shell.IsDetailsPanelOpen);
        Assert.False(shell.IsNotificationsPanelOpen);
        Assert.Equal("Detalii", shell.RightPanelTitle);

        shell.ToggleDetailsCommand.Execute(null);
        Assert.False(shell.IsRightPanelOpen);
        Assert.False(shell.IsDetailsPanelOpen);
    }

    [Fact]
    public void Toggling_the_other_panel_switches_content_and_stays_open()
    {
        var shell = CreateShell();
        shell.ToggleDetailsCommand.Execute(null);
        var changed = RecordChanges(shell);

        shell.ToggleNotificationsCommand.Execute(null);

        Assert.True(shell.IsRightPanelOpen);
        Assert.True(shell.IsNotificationsPanelOpen);
        Assert.False(shell.IsDetailsPanelOpen);
        Assert.Equal("Notificări", shell.RightPanelTitle);
        Assert.Contains(nameof(ShellViewModel.IsDetailsPanelOpen), changed);
        Assert.Contains(nameof(ShellViewModel.IsNotificationsPanelOpen), changed);
        Assert.Contains(nameof(ShellViewModel.RightPanelTitle), changed);
    }

    [Fact]
    public void Close_right_panel_closes_whatever_is_open()
    {
        var shell = CreateShell();
        shell.ToggleNotificationsCommand.Execute(null);

        shell.CloseRightPanelCommand.Execute(null);

        Assert.False(shell.IsRightPanelOpen);
        Assert.False(shell.IsNotificationsPanelOpen);
    }

    [Fact]
    public void Open_palette_sets_the_flag()
    {
        var shell = CreateShell();
        shell.OpenPaletteCommand.Execute(null);
        Assert.True(shell.IsPaletteOpen);
    }

    [Fact]
    public void Toggle_theme_asks_for_the_opposite_of_the_current_theme()
    {
        var shell = CreateShell();

        shell.ToggleThemeCommand.Execute(null);
        shell.ToggleThemeCommand.Execute(null);

        Assert.Equal([ThemePreference.Dark, ThemePreference.Light], _theme.PreferenceCalls);
    }

    [Fact]
    public void Palette_has_a_working_go_to_command_for_every_section()
    {
        var shell = CreateShell();
        var goTo = shell.Commands.Where(c => c.Group == "Navigare").ToList();
        Assert.Contains(shell.Commands, c => c.Title == "Task nou" && c.Shortcut == "Ctrl+N");
        Assert.Equal(10, goTo.Count);

        Assert.Equal(shell.NavItems.Select(i => $"Mergi la {i.Title}"), goTo.Select(c => c.Title));
        foreach (var command in goTo)
        {
            command.Command.Execute(command.Parameter);
            Assert.Equal(command.Parameter, shell.CurrentSection);
        }
    }

    [Fact]
    public void Every_palette_command_can_execute()
    {
        // Rule 2 from PLAN.md: nothing is offered that does not work.
        var shell = CreateShell();
        Assert.All(shell.Commands, c => Assert.True(c.Command.CanExecute(c.Parameter), c.Title));
    }

    [Fact]
    public void Section_catalog_is_indexed_by_section_and_shortcuts_are_unique()
    {
        Assert.All(Enum.GetValues<Section>(), s => Assert.Equal(s, SectionCatalog.Get(s).Section));
        Assert.Equal(SectionCatalog.All.Count, SectionCatalog.All.Select(i => i.Shortcut).Distinct().Count());
    }

    [Fact]
    public void Sections_without_features_yet_have_an_honest_empty_state_in_both_languages()
    {
        var comingSoon = Enum.GetValues<Section>().Where(s => s is not (Section.Overview or Section.Settings)).ToList();
        Assert.All(comingSoon, s => Assert.True(SectionCatalog.Get(s).HasEmptyState));

        foreach (var loc in new[] { TestLoc.Ro(), TestLoc.En() })
        {
            Assert.All(Enum.GetValues<Section>(), s =>
            {
                var info = SectionCatalog.Get(s);
                Assert.DoesNotContain("⟦", info.Title(loc) + info.Subtitle(loc) + info.EmptyTitle(loc) + info.EmptyDescription(loc));
                if (info.HasEmptyState)
                {
                    Assert.False(string.IsNullOrWhiteSpace(info.EmptyTitle(loc)), $"{s} has no empty-state title");
                    Assert.False(string.IsNullOrWhiteSpace(info.EmptyDescription(loc)), $"{s} has no empty-state description");
                }
            });
        }
    }

    private static PageViewModel Navigate(ShellViewModel shell, Section section)
    {
        shell.NavigateCommand.Execute(section);
        return shell.CurrentPage;
    }

    private static List<string?> RecordChanges(INotifyPropertyChanged source)
    {
        var names = new List<string?>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        return names;
    }
}

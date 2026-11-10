using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;
using Xunit;

namespace FinanceOS.App.Tests;

public sealed class PageViewModelTests : IDisposable
{
    private readonly List<TaskKit> _kits = [];

    public void Dispose() => _kits.ForEach(k => k.Dispose());

    /// <summary>An Overview over an empty database (no tasks yet).</summary>
    private OverviewPageViewModel Overview(TimeProvider clock, Localizer loc)
    {
        var kit = new TaskKit(clock);
        _kits.Add(kit);
        return new OverviewPageViewModel(kit.Tasks, kit.Bus, clock, loc, new FakeToasts(), new FakeLauncher(), kit.Reminders);
    }

    [Theory]
    [InlineData(0, "Bună seara")]
    [InlineData(4, "Bună seara")]
    [InlineData(5, "Bună dimineața")]
    [InlineData(11, "Bună dimineața")]
    [InlineData(12, "Bună ziua")]
    [InlineData(17, "Bună ziua")]
    [InlineData(18, "Bună seara")]
    [InlineData(23, "Bună seara")]
    public void Greeting_follows_the_hour(int hour, string expected)
    {
        Assert.Equal(expected, TestLoc.Ro()[OverviewPageViewModel.GreetingKey(hour)]);
    }

    [Fact]
    public void Overview_uses_local_time_for_greeting_and_date()
    {
        // 00:30 on 1 Jan in UTC+2 is still 31 Dec in UTC: the local date must win.
        var clock = new FixedTimeProvider(new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(2)));
        var overview = Overview(clock, TestLoc.Ro());

        Assert.Equal("Bună seara", overview.Greeting);
        Assert.Equal("Vineri, 1 ianuarie 2027", overview.DateText);
    }

    [Fact]
    public void Overview_stats_start_at_zero_until_tasks_exist()
    {
        var overview = Overview(TimeProvider.System, TestLoc.Ro());
        Assert.Equal(4, overview.Stats.Count);
        Assert.All(overview.Stats, s => Assert.Equal("0", s.Value));
    }

    [Fact]
    public void Page_factory_maps_sections_to_page_types()
    {
        using var kit = new TaskKit(TimeProvider.System);
        var services = new PageServices(new FakeThemeService(), TimeProvider.System, new FakeDataMaintenance(), new FakeLauncher(), TestLoc.Ro(),
            kit.Tasks, kit.Bus, new FakeToasts(), kit.Reminders);
        Assert.All(Enum.GetValues<Section>(), section =>
        {
            var page = PageFactory.Create(section, services);
            Assert.Equal(section, page.Section);
            var expected = section switch
            {
                Section.Overview => typeof(OverviewPageViewModel),
                Section.Settings => typeof(SettingsPageViewModel),
                Section.Tasks => typeof(TasksPageViewModel),
                Section.MyDay => typeof(MyDayPageViewModel),
                Section.Calendar => typeof(CalendarPageViewModel),
                _ => typeof(ComingSoonPageViewModel),
            };
            Assert.IsType(expected, page);
        });
    }

    [Fact]
    public void Settings_starts_from_the_current_theme_without_writing_it_back()
    {
        var theme = new FakeThemeService();
        theme.ChangeExternally(ThemePreference.Dark, Accents.All[^1]);

        var settings = new SettingsPageViewModel(theme, new FakeDataMaintenance(), new FakeLauncher(), TimeProvider.System, TestLoc.Ro());

        Assert.Equal(ThemePreference.Dark, settings.SelectedTheme?.Value);
        Assert.Same(Accents.All[^1], settings.SelectedAccent?.Value);
        Assert.Empty(theme.PreferenceCalls);
        Assert.Empty(theme.AccentCalls);
    }

    [Fact]
    public void Settings_applies_theme_and_accent_choices()
    {
        var theme = new FakeThemeService();
        var settings = new SettingsPageViewModel(theme, new FakeDataMaintenance(), new FakeLauncher(), TimeProvider.System, TestLoc.Ro());

        settings.SelectedTheme = settings.ThemeOptions.Single(o => o.Value == ThemePreference.System);
        settings.SelectedAccent = settings.Accents[1];

        Assert.Equal([ThemePreference.System], theme.PreferenceCalls);
        Assert.Equal([settings.Accents[1].Value], theme.AccentCalls);
    }

    [Fact]
    public void Settings_follows_changes_made_elsewhere_without_echoing_them()
    {
        var theme = new FakeThemeService();
        var settings = new SettingsPageViewModel(theme, new FakeDataMaintenance(), new FakeLauncher(), TimeProvider.System, TestLoc.Ro());

        theme.ChangeExternally(ThemePreference.Dark, Accents.All[2]);

        Assert.Equal(ThemePreference.Dark, settings.SelectedTheme?.Value);
        Assert.Same(Accents.All[2], settings.SelectedAccent?.Value);
        Assert.Empty(theme.PreferenceCalls);
        Assert.Empty(theme.AccentCalls);
    }

    [Fact]
    public void Settings_offers_all_three_theme_choices()
    {
        var settings = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), TimeProvider.System, TestLoc.Ro());
        Assert.Equal(Enum.GetValues<ThemePreference>(), settings.ThemeOptions.Select(o => o.Value));
    }

    private static readonly FixedTimeProvider Bucharest = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(2)));

    [Fact]
    public void Settings_says_when_there_is_no_backup_yet()
    {
        var settings = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Bucharest, TestLoc.Ro());
        Assert.StartsWith("Încă niciun backup", settings.LastBackupText);
        Assert.Equal(@"C:\Data\FinanceOS", settings.DataFolder);
    }

    [Fact]
    public async Task Backup_now_confirms_with_the_local_time_and_updates_the_last_backup()
    {
        var data = new FakeDataMaintenance();
        var settings = new SettingsPageViewModel(new FakeThemeService(), data, new FakeLauncher(), Bucharest, TestLoc.Ro());

        await settings.BackupNowCommand.ExecuteAsync(null);

        Assert.False(settings.BackupFailed);
        Assert.Equal("Backup creat: 5 noiembrie 2026, 10:30.", settings.BackupMessage); // 08:30 UTC = 10:30 local
        Assert.StartsWith("Ultimul backup: 5 noiembrie 2026, 10:30.", settings.LastBackupText);
    }

    [Fact]
    public async Task Failed_backup_is_reported_not_swallowed()
    {
        var data = new FakeDataMaintenance { Fail = true };
        var settings = new SettingsPageViewModel(new FakeThemeService(), data, new FakeLauncher(), Bucharest, TestLoc.Ro());

        await settings.BackupNowCommand.ExecuteAsync(null);

        Assert.True(settings.BackupFailed);
        Assert.StartsWith("Backup-ul nu a reușit", settings.BackupMessage);
        Assert.StartsWith("Încă niciun backup", settings.LastBackupText);
    }

    [Fact]
    public void Open_data_folder_goes_through_the_service()
    {
        var data = new FakeDataMaintenance();
        new SettingsPageViewModel(new FakeThemeService(), data, new FakeLauncher(), Bucharest, TestLoc.Ro()).OpenDataFolderCommand.Execute(null);
        Assert.Equal(1, data.OpenedFolder);
    }

    [Fact]
    public void About_shows_the_author_and_feedback_opens_a_prefilled_email()
    {
        var launcher = new FakeLauncher();
        var settings = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), launcher, Bucharest, TestLoc.Ro());

        Assert.Equal("Petcu Iulian", settings.Author);
        Assert.Equal("App Author", settings.AuthorRole);
        Assert.Equal("contact@example.com", settings.ContactEmail);

        settings.SendFeedbackCommand.Execute(null);

        var uri = Assert.Single(launcher.Opened);
        Assert.Equal("mailto", uri.Scheme);
        Assert.StartsWith("mailto:contact@example.com?subject=", uri.OriginalString);
        var query = Uri.UnescapeDataString(uri.Query);
        Assert.Contains($"Finance OS {settings.Version}", query);
        Assert.Contains("Windows:", query);
    }

    [Fact]
    public void Overview_switches_greeting_date_and_stats_to_english()
    {
        var loc = TestLoc.Ro();
        var clock = new FixedTimeProvider(new DateTimeOffset(2027, 1, 1, 9, 0, 0, TimeSpan.FromHours(2)));
        var overview = Overview(clock, loc);
        Assert.Equal("Bună dimineața", overview.Greeting);
        Assert.Equal("În lucru", overview.Stats[0].Label);

        loc.SetLanguage(AppLanguage.English);

        Assert.Equal("Good morning", overview.Greeting);
        Assert.Equal("Friday, 1 January 2027", overview.DateText);
        Assert.Equal(["In progress", "Done today", "Overdue", "Waiting"], overview.Stats.Select(s => s.Label));
        Assert.Equal("Overview", overview.Title);
    }

    [Fact]
    public void Coming_soon_pages_switch_their_empty_state()
    {
        var loc = TestLoc.Ro();
        var page = new ComingSoonPageViewModel(SectionCatalog.Get(Section.Tasks), loc);
        Assert.Equal("Niciun task încă", page.EmptyTitle);

        loc.SetLanguage(AppLanguage.English);

        Assert.Equal("No tasks yet", page.EmptyTitle);
        Assert.Equal("Tasks", page.Title);
    }

    [Fact]
    public void Settings_starts_on_the_current_language_and_switches_the_whole_app()
    {
        var loc = TestLoc.Ro();
        var data = new FakeDataMaintenance { LastBackupUtc = new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero) };
        var settings = new SettingsPageViewModel(new FakeThemeService(), data, new FakeLauncher(), Bucharest, loc);
        var labelChanges = new List<string?>();
        settings.ThemeOptions[0].PropertyChanged += (_, e) => labelChanges.Add(e.PropertyName);
        var raised = 0;
        loc.Changed += (_, _) => raised++;

        Assert.Equal(AppLanguage.Romanian, settings.SelectedLanguage?.Value);
        Assert.Equal(["English", "Română"], settings.LanguageOptions.Select(o => o.Label));
        Assert.Equal("Ca Windows", settings.ThemeOptions[0].Label);
        Assert.Equal("Albastru", settings.Accents.Single(a => a.Value.Id == "blue").Label);
        Assert.StartsWith("Ultimul backup: 5 noiembrie 2026, 10:00.", settings.LastBackupText);

        settings.SelectedLanguage = settings.LanguageOptions.Single(o => o.Value == AppLanguage.English);

        Assert.Equal(AppLanguage.English, loc.Language);
        Assert.Equal(1, raised);
        Assert.Equal("Settings", settings.Title);
        Assert.Equal("Same as Windows", settings.ThemeOptions[0].Label);
        Assert.Contains(nameof(ChoiceOption<ThemePreference>.Label), labelChanges);
        Assert.Equal("Blue", settings.Accents.Single(a => a.Value.Id == "blue").Label);
        Assert.StartsWith("Last backup: 5 November 2026, 10:00.", settings.LastBackupText);
        Assert.Equal(["English", "Română"], settings.LanguageOptions.Select(o => o.Label));
    }

    [Fact]
    public void Language_changed_elsewhere_moves_the_selection_without_echoing_it()
    {
        var loc = TestLoc.Ro();
        var settings = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Bucharest, loc);
        var raised = 0;
        loc.Changed += (_, _) => raised++;

        loc.SetLanguage(AppLanguage.English);

        Assert.Equal(AppLanguage.English, settings.SelectedLanguage?.Value);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Backup_message_follows_a_language_change_after_it_was_shown()
    {
        var loc = TestLoc.Ro();
        var settings = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Bucharest, loc);
        await settings.BackupNowCommand.ExecuteAsync(null);
        Assert.Equal("Backup creat: 5 noiembrie 2026, 10:30.", settings.BackupMessage);

        loc.SetLanguage(AppLanguage.English);

        Assert.Equal("Backup created: 5 November 2026, 10:30.", settings.BackupMessage);
    }

    [Fact]
    public void Feedback_email_is_written_in_the_ui_language()
    {
        var launcher = new FakeLauncher();
        var settings = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), launcher, Bucharest, TestLoc.En());

        settings.SendFeedbackCommand.Execute(null);

        var query = Uri.UnescapeDataString(Assert.Single(launcher.Opened).Query);
        Assert.Contains($"Finance OS {settings.Version} – suggestion / problem", query);
        Assert.Contains("Describe the suggestion", query);
        Assert.Contains("Language: en-GB", query);
    }

    [Fact]
    public void Automatic_backup_finishing_later_updates_the_settings_text()
    {
        var data = new FakeDataMaintenance();
        var settings = new SettingsPageViewModel(new FakeThemeService(), data, new FakeLauncher(), Bucharest, TestLoc.Ro());
        Assert.StartsWith("Încă niciun backup", settings.LastBackupText);

        data.CompleteAutomaticBackup(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));

        Assert.StartsWith("Ultimul backup: 5 noiembrie 2026, 10:00.", settings.LastBackupText);
    }
}

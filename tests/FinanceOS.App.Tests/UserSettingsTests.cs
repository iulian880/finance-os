using System.IO;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.Core.Reminders;
using FinanceOS.UI.Theming;
using Xunit;

namespace FinanceOS.App.Tests;

/// <summary>M12a: theme, accent, working hours and quiet hours kept between sessions.</summary>
public sealed class UserSettingsTests : IDisposable
{
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 9, 17, 0, 0, TimeSpan.FromHours(2)));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));

    private string PreferencesPath => Path.Combine(_root, "preferences.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Defaults_until_the_user_changes_something()
    {
        var settings = new UserSettings(new PreferencesStore(PreferencesPath));

        Assert.Null(settings.Theme);
        Assert.Null(settings.Quiet);
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(18, 0)), (settings.WorkStart, settings.WorkEnd));
        Assert.False(File.Exists(PreferencesPath));
    }

    [Fact]
    public void Changes_are_saved_and_read_back_by_the_next_session()
    {
        var settings = new UserSettings(new PreferencesStore(PreferencesPath));
        var changes = 0;
        settings.Changed += (_, _) => changes++;

        settings.Update(p => p with { Theme = UserSettings.ThemeCode(ThemePreference.Dark), Accent = "teal", WorkStart = "08:30" });
        settings.Update(p => p with { QuietStart = "21:00", QuietEnd = "07:00", QuietWeekends = true });
        settings.Update(p => p with { WorkStart = "08:30" }); // no change: nothing saved, no event

        Assert.Equal(2, changes);
        var next = new UserSettings(new PreferencesStore(PreferencesPath));
        Assert.Equal(ThemePreference.Dark, next.Theme);
        Assert.Equal("teal", next.Current.Accent);
        Assert.Equal(new TimeOnly(8, 30), next.WorkStart);
        Assert.Equal(new TimeOnly(18, 0), next.WorkEnd);
        Assert.Equal(new QuietHours(new TimeOnly(21, 0), new TimeOnly(7, 0), true), next.Quiet);
    }

    [Fact]
    public void A_damaged_time_falls_back_to_the_default()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PreferencesPath, "{\"workStart\":\"25:99\",\"quietStart\":\"22:00\"}");

        var settings = new UserSettings(new PreferencesStore(PreferencesPath));

        Assert.Equal(UserSettings.DefaultWorkStart, settings.WorkStart);
        Assert.Null(settings.Quiet); // half a quiet range is no range
    }

    [Fact]
    public void Settings_page_saves_working_and_quiet_hours()
    {
        var settings = new UserSettings(new PreferencesStore(PreferencesPath));
        var page = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Clock, TestLoc.Ro(), null, settings);

        Assert.True(page.HasSchedule);
        Assert.False(page.QuietEnabled);
        Assert.Null(page.QuietStart);

        page.WorkStart = new TimeSpan(8, 0, 0);
        page.WorkEnd = new TimeSpan(16, 30, 0);
        page.QuietEnabled = true;
        Assert.Equal((new TimeSpan(20, 0, 0), new TimeSpan(8, 0, 0)), (page.QuietStart!.Value, page.QuietEnd!.Value));
        page.QuietStart = new TimeSpan(22, 0, 0);
        page.QuietWeekends = true;
        page.WorkEnd = null; // cleared picker: the saved value stays

        var next = new UserSettings(new PreferencesStore(PreferencesPath));
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(16, 30)), (next.WorkStart, next.WorkEnd));
        Assert.Equal(new QuietHours(new TimeOnly(22, 0), new TimeOnly(8, 0), true), next.Quiet);

        page.QuietEnabled = false;
        Assert.Null(new UserSettings(new PreferencesStore(PreferencesPath)).Quiet);
    }

    [Fact]
    public void Without_saved_settings_the_schedule_card_is_hidden()
    {
        var page = new SettingsPageViewModel(new FakeThemeService(), new FakeDataMaintenance(), new FakeLauncher(), Clock, TestLoc.Ro());

        Assert.False(page.HasSchedule);
        Assert.Null(page.WorkStart);
    }

    [Fact]
    public void Data_folder_override_must_be_an_absolute_path()
    {
        const string fallback = @"C:\Users\me\AppData\Local\FinanceOS";

        Assert.Equal(@"D:\Temp\fos", FinanceOS.Platform.Windows.AppPaths.Resolve(@"""D:\Temp\fos"" ", fallback));
        Assert.Equal(fallback, FinanceOS.Platform.Windows.AppPaths.Resolve(@"relative\fos", fallback));
        Assert.Equal(fallback, FinanceOS.Platform.Windows.AppPaths.Resolve("  ", fallback));
        Assert.Equal(fallback, FinanceOS.Platform.Windows.AppPaths.Resolve(null, fallback));
    }

    [Fact]
    public void A_separate_data_folder_leaves_the_real_scheduled_toasts_alone()
    {
        using var services = AppServices.Build(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, TestLoc.En(), _root);

        Assert.IsType<FinanceOS.App.Reminders.InertScheduledToasts>(services.GetService(typeof(IScheduledToasts)));
    }

    [Fact]
    public void Calendar_day_view_follows_the_configured_working_hours()
    {
        using var kit = new TaskKit(Clock);
        var settings = new UserSettings(new PreferencesStore(PreferencesPath));
        settings.Update(p => p with { WorkStart = "07:30", WorkEnd = "15:00" });
        var page = new CalendarPageViewModel(kit.Tasks, kit.Bus, Clock, TestLoc.Ro(), new FakeToasts(), new FakeLauncher(), kit.Reminders, settings);

        page.OpenDayCommand.Execute(new DateOnly(2026, 11, 10));

        Assert.Equal("Program 07:30–15:00", page.WorkingHoursText);
        Assert.True(page.DayHours.Single(h => h.Label == "07:00").IsWorking);
        Assert.False(page.DayHours.Single(h => h.Label == "15:00").IsWorking);

        settings.Update(p => p with { WorkEnd = "17:00" }); // changed in Settings while the calendar is open
        Assert.Equal("Program 07:30–17:00", page.WorkingHoursText);
        Assert.True(page.DayHours.Single(h => h.Label == "16:00").IsWorking);
    }
}

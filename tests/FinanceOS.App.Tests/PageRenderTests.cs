using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FinanceOS.App.Onboarding;
using FinanceOS.App.Services;
using FinanceOS.App.Shell;
using FinanceOS.App.Tasks;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Theming;
using Xunit;

namespace FinanceOS.App.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "App WPF";
}

/// <summary>
/// Renders every page and the details panel with the real templates (PageTemplates.xaml on top of the
/// design system, merged like App.xaml). Template errors — a missing StaticResource, a resource defined
/// after its first use — only surface when a template is instantiated, which ViewModel tests never do.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PageRenderTests : IDisposable
{
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(2)));

    private readonly TaskKit _kit = new(Clock);
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));
    private readonly UserSettings _user;

    public PageRenderTests() => _user = new UserSettings(new PreferencesStore(System.IO.Path.Combine(_root, "preferences.json")));

    public void Dispose()
    {
        _kit.Dispose();
        if (System.IO.Directory.Exists(_root))
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Every_page_and_the_details_panel_render_with_tasks_present_in_both_themes()
    {
        var today = new DateOnly(2026, 11, 9);
        var tag = _kit.Tasks.GetOrCreateTag("urgent");
        var parent = _kit.Tasks.Create(new TaskDraft("Month-end") { DueDate = today, DueTime = new TimeOnly(9, 0), Checklist = ["a", "b"], Tags = [tag.Id], IsPinned = true, Priority = Priority.Urgent });
        _kit.Tasks.Create(new TaskDraft("Subtask") { ParentId = parent.Id, DueDate = today.AddDays(1) });
        _kit.Tasks.Create(new TaskDraft("Late") { DueDate = today.AddDays(-3), Priority = Priority.High });

        RunOnSta(() =>
        {
            var services = new PageServices(new FakeThemeService(), Clock, new FakeDataMaintenance(), new FakeLauncher(), TestLoc.Ro(),
                _kit.Tasks, _kit.Bus, new FakeToasts(), _kit.Reminders);
            // Like the app: templates resolve StaticResource through Application.Resources. One Application
            // per process, so both themes render in this single test.
            // The app's own App.xaml (InitializeComponent loads its resources; OnStartup only runs with Run()).
            // Like App.xaml: the dictionaries load while already part of Application.Resources, so templates
            // resolve StaticResource through the application exactly as in the app. One Application per process.
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.Resources.MergedDictionaries.Count == 0)
            {
                foreach (var source in new[] { "pack://application:,,,/FinanceOS.UI;component/Themes/FinanceOS.xaml", "pack://application:,,,/FinanceOS;component/Shell/PageTemplates.xaml" })
                {
                    var dictionary = new ResourceDictionary();
                    app.Resources.MergedDictionaries.Add(dictionary);
                    dictionary.Source = new Uri(source);
                }
            }

            foreach (var dark in new[] { false, true })
            {
            SetTheme(app, dark);
            var resources = new ResourceDictionary();
            foreach (var section in Enum.GetValues<Section>())
            {
                var page = PageFactory.Create(section, services);
                var host = Render(page, resources);
                Assert.True(CountVisuals(host) > 10, $"{section} rendered almost nothing");

                if (page is CalendarPageViewModel calendar)
                {
                    // Collapsed views are not instantiated: render each one on its own.
                    foreach (var mode in calendar.ModeOptions)
                    {
                        calendar.SelectedMode = mode;
                        Assert.True(CountVisuals(Render(calendar, resources)) > 10, $"calendar {mode.Value} rendered almost nothing");
                    }
                }

                if (page is TaskListPageViewModel list)
                {
                    list.Select(parent.Id);
                    var details = Render(Assert.IsType<TaskDetailsViewModel>(list.Details), resources);
                    Assert.True(CountVisuals(details) > 50, $"{section}: details panel rendered almost nothing");
                }
            }

            // The first-run guide, every step (it lives over the window, outside the pages).
            var guide = new OnboardingViewModel(Assert.IsType<SettingsPageViewModel>(PageFactory.Create(Section.Settings, services)), _user, TestLoc.Ro(), false, () => 0, _ => { });
            for (var step = 0; step < OnboardingViewModel.StepCount; step++)
            {
                guide.Step = step;
                Assert.True(CountVisuals(Render(guide, resources)) > 30, $"onboarding step {step} rendered almost nothing");
            }
            }
        });
    }

    /// <summary>Adds (or swaps) the theme colors, as ThemeManager does at runtime.</summary>
    private static void SetTheme(Application app, bool dark)
    {
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 2)
        {
            merged.RemoveAt(2);
        }

        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;
        merged.Add(ThemeResources.Build(palette, AccentColors.Derive(Accents.Default, palette)));
    }

    private static FrameworkElement Render(object content, ResourceDictionary resources)
    {
        var host = new Border { Resources = resources, Width = 1100, Height = 820, Child = new ContentControl { Content = content } };
        host.Measure(new Size(1100, 820));
        host.Arrange(new Rect(0, 0, 1100, 820));
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        return host;
    }

    private static int CountVisuals(DependencyObject root)
    {
        var count = 1;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            count += CountVisuals(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        }

        return count;
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

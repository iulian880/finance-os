using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FinanceOS.UI.Theming;
using Xunit;

namespace FinanceOS.UI.Tests;

/// <summary>
/// Test classes that create WPF elements or load XAML. They run one after another: building UI on
/// several STA threads at once intermittently deadlocked inside WPF under Microsoft.Testing.Platform
/// (about 3 runs in 4; never with parallelization off). Pure logic tests stay parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "WPF";
}

/// <summary>
/// Hosts controls with the real design-system resources, without an Application or a window:
/// the element is placed under a Border carrying FinanceOS.xaml + a theme dictionary, then measured
/// and arranged so templates are applied and DynamicResource references resolve.
/// </summary>
internal static class UiHarness
{
    public static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Async UI code (await in DialogHost) must resume on this thread, as it would in the app.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                // Popups create HWNDs; shutting the dispatcher down releases them so the test host can exit promptly.
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    public static ResourceDictionary LoadDesignSystem() =>
        (ResourceDictionary)Application.LoadComponent(new Uri("/FinanceOS.UI;component/Themes/FinanceOS.xaml", UriKind.Relative));

    public static ResourceDictionary BuildTheme(bool dark)
    {
        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;
        return ThemeResources.Build(palette, AccentColors.Derive(Accents.Default, palette));
    }

    /// <summary>Hosts <paramref name="element"/> in a themed root and runs layout. Returns the root for theme swaps.</summary>
    public static Border Host(FrameworkElement element, bool dark = false)
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(LoadDesignSystem());
        resources.MergedDictionaries.Add(BuildTheme(dark));
        var root = new Border { Resources = resources, Child = element };
        Layout(root);
        return root;
    }

    public static void SwapTheme(Border root, bool dark)
    {
        var merged = root.Resources.MergedDictionaries;
        merged.RemoveAt(merged.Count - 1);
        merged.Add(BuildTheme(dark));
        Layout(root);
    }

    public static void Layout(FrameworkElement element)
    {
        var root = element;
        while (root.Parent is FrameworkElement parent)
        {
            root = parent;
        }

        root.Measure(new Size(800, 600));
        root.Arrange(new Rect(0, 0, 800, 600));
        root.UpdateLayout();
        DrainDispatcher();
        root.UpdateLayout();
    }

    /// <summary>Pumps the dispatcher until <paramref name="task"/> completes (max 5 s), then returns its result.</summary>
    public static T Await<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        // Wake the dispatcher: setting Continue from another thread is only noticed after the next queued item.
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        Assert.True(task.IsCompleted, "Task did not complete within 5 s");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Runs queued dispatcher work (data bindings resolve at DataBind priority, after layout).</summary>
    public static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    public static T Part<T>(Control control, string name)
        where T : class
    {
        var part = control.Template?.FindName(name, control) as T;
        Assert.NotNull(part);
        return part;
    }

    public static Color ColorOf(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    public static Color Token(bool dark, string name) => (Color)BuildTheme(dark)[$"Color.{name}"];
}

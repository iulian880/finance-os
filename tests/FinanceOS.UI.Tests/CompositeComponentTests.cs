using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FinanceOS.UI.Controls;
using Xunit;
using static FinanceOS.UI.Tests.UiHarness;

namespace FinanceOS.UI.Tests;

/// <summary>M3: icons, card, empty state, skeleton, menus, dialog host, toasts, command palette.</summary>
[Collection(WpfCollection.Name)]
public sealed class CompositeComponentTests
{
    [Fact]
    public void Every_icon_is_a_valid_geometry_inside_the_24px_grid()
    {
        RunOnSta(() =>
        {
            var icons = (ResourceDictionary)Application.LoadComponent(new Uri("/FinanceOS.UI;component/Themes/Icons.xaml", UriKind.Relative));
            var keys = icons.Keys.OfType<string>().Where(k => k.StartsWith("Icon.", StringComparison.Ordinal)).ToList();
            Assert.True(keys.Count >= 40, $"expected the full icon set, found {keys.Count}");

            foreach (var key in keys)
            {
                var bounds = Assert.IsAssignableFrom<Geometry>(icons[key]).Bounds;
                Assert.False(bounds.IsEmpty, $"{key} is empty");
                Assert.True(bounds.Left >= -0.01 && bounds.Top >= -0.01 && bounds.Right <= 24.01 && bounds.Bottom <= 24.01,
                    $"{key} leaves the 24×24 grid: {bounds}");
            }
        });
    }

    [Fact]
    public void Icon_takes_its_size_and_the_color_of_the_surrounding_text()
    {
        RunOnSta(() =>
        {
            var icon = new Icon { Data = Geometry.Parse("M0,0 L24,24"), Size = 20 };
            var button = new Button { Content = icon, Style = null };
            var root = Host(button);
            button.Style = (Style)button.FindResource("Button.Danger");
            Layout(root);

            Assert.Equal(new Size(20, 20), icon.DesiredSize);
            Assert.Equal(Token(false, "OnDanger"), ColorOf(icon.Foreground));
            Assert.False(icon.Focusable);
        });
    }

    [Fact]
    public void Card_hides_its_header_row_when_there_is_nothing_to_show()
    {
        RunOnSta(() =>
        {
            var plain = new Card { Content = "x" };
            var titled = new Card { Content = "x", Header = "Reconciliere" };
            Host(new StackPanel { Children = { plain, titled } });

            Assert.Equal(Visibility.Collapsed, Part<FrameworkElement>(plain, "HeaderRow").Visibility);
            Assert.Equal(Visibility.Visible, Part<FrameworkElement>(titled, "HeaderRow").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<FrameworkElement>(titled, "ActionsPresenter").Visibility);
        });
    }

    [Fact]
    public void Empty_state_shows_only_the_parts_that_are_set()
    {
        RunOnSta(() =>
        {
            var empty = new EmptyState { Title = "Nimic azi" };
            Host(empty);
            Assert.Equal(Visibility.Visible, Part<FrameworkElement>(empty, "TitleText").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<FrameworkElement>(empty, "IconBadge").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<FrameworkElement>(empty, "DescriptionText").Visibility);
            Assert.Equal(Visibility.Collapsed, Part<FrameworkElement>(empty, "Actions").Visibility);
        });
    }

    [Fact]
    public void Skeleton_does_not_animate_when_motion_is_off()
    {
        RunOnSta(() =>
        {
            var previous = Motion.Enabled;
            Motion.Enabled = false;
            try
            {
                var skeleton = new Skeleton { Width = 200 };
                Host(skeleton);
                Assert.False(skeleton.IsSweeping);
                Assert.False(skeleton.Focusable);
                Assert.Equal(Visibility.Collapsed, Part<FrameworkElement>(skeleton, Skeleton.PartShimmer).Visibility);
            }
            finally
            {
                Motion.Enabled = previous;
            }
        });
    }

    [Theory]
    [InlineData("MenuItem.Danger")]
    public void Menu_styles_exist(string key)
    {
        RunOnSta(() =>
        {
            var resources = LoadDesignSystem();
            Assert.IsType<Style>(resources[key]);
            Assert.IsType<Style>(resources[typeof(ContextMenu)]);
            Assert.IsType<Style>(resources[typeof(MenuItem)]);
            Assert.IsType<Style>(resources[MenuItem.SeparatorStyleKey]);
        });
    }

    [Fact]
    public void Menu_items_render_header_gesture_and_submenu_popup()
    {
        RunOnSta(() =>
        {
            var item = new MenuItem { Header = "Duplică", InputGestureText = "Ctrl+D" };
            var parent = new MenuItem { Header = "Amână", Items = { new MenuItem { Header = "Mâine" } } };
            var menu = new ContextMenu { Items = { item, parent } };
            Host(new StackPanel { Children = { new MenuItem { Header = "standalone" } } });
            // Menus live in their own popup; apply resources and templates directly.
            menu.Resources.MergedDictionaries.Add(LoadDesignSystem());
            menu.Resources.MergedDictionaries.Add(BuildTheme(false));
            menu.Measure(new Size(400, 400));
            menu.Arrange(new Rect(0, 0, 400, 400));
            menu.UpdateLayout();

            Assert.Equal("Ctrl+D", Part<TextBlock>(item, "Gesture").Text);
            Assert.Equal(MenuItemRole.SubmenuHeader, parent.Role);
            Assert.Equal(Visibility.Visible, Part<FrameworkElement>(parent, "SubmenuArrow").Visibility);
            Part<Popup>(parent, "PART_Popup");
        });
    }

    [Fact]
    public void Dialog_returns_the_button_that_closed_it_and_restores_the_page()
    {
        RunOnSta(() =>
        {
            var host = new DialogHost { Content = new TextBlock { Text = "page" } };
            Host(host);
            var dialog = new Dialog { Title = "Task nou", PrimaryButtonText = "Creează", SecondaryButtonText = "Anulează" };

            var shown = host.ShowAsync(dialog);
            Layout(host);
            Assert.True(host.IsDialogOpen);
            Assert.Same(dialog, host.CurrentDialog);
            Assert.False(Part<FrameworkElement>(host, "PageContent").IsHitTestVisible);
            Assert.Equal("Task nou", AutomationProperties.GetName(dialog));
            Assert.True(AutomationProperties.GetIsDialog(dialog));

            dialog.PrimaryButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(DialogOutcome.Primary, Await(shown));
            Layout(host);
            Assert.False(host.IsDialogOpen);
            Assert.Null(host.CurrentDialog);
            Assert.True(Part<FrameworkElement>(host, "PageContent").IsHitTestVisible);
        });
    }

    [Fact]
    public void Dialog_body_text_wraps_in_secondary_color_for_strings_and_elements()
    {
        RunOnSta(() =>
        {
            var fromString = new Dialog { Title = "A", Content = "Text simplu", PrimaryButtonText = "OK" };
            var element = new TextBlock { Text = "Text adăugat din cod" };
            var fromElement = new Dialog { Title = "B", Content = new StackPanel { Children = { element } }, PrimaryButtonText = "OK" };
            Host(new StackPanel { Children = { fromString, fromElement } });

            var generated = FindDescendant<TextBlock>(fromString, t => t.Text == "Text simplu");
            foreach (var text in new[] { generated, element })
            {
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.Equal(Token(false, "TextSecondary"), ColorOf(text.Foreground));
            }
        });
    }

    [Fact]
    public void Dialogs_are_shown_one_at_a_time()
    {
        RunOnSta(() =>
        {
            var host = new DialogHost();
            Host(host);
            var first = new Dialog { Title = "1", PrimaryButtonText = "OK" };
            var second = new Dialog { Title = "2", PrimaryButtonText = "OK" };

            var a = host.ShowAsync(first);
            var b = host.ShowAsync(second);
            DrainDispatcher();
            Assert.Same(first, host.CurrentDialog);
            Assert.False(b.IsCompleted);

            first.Hide(DialogOutcome.Secondary);
            Assert.Equal(DialogOutcome.Secondary, Await(a));
            DrainDispatcher();
            Assert.Same(second, host.CurrentDialog);

            second.Hide();
            Assert.Equal(DialogOutcome.None, Await(b));
        });
    }

    [Fact]
    public void Destructive_dialogs_use_the_danger_button_and_ignore_enter()
    {
        RunOnSta(() =>
        {
            var host = new DialogHost();
            Host(host);
            var dialog = new Dialog { Title = "Ștergi?", PrimaryButtonText = "Șterge", SecondaryButtonText = "Anulează", IsPrimaryDestructive = true };
            var shown = host.ShowAsync(dialog);
            Layout(host);

            var primary = Assert.IsType<Button>(dialog.PrimaryButton);
            Assert.Equal(Token(false, "DangerFillRest"), ColorOf(primary.Background));
            Assert.Equal(Token(false, "OnDanger"), ColorOf(primary.Foreground));
            Assert.False(primary.IsDefault);

            dialog.Hide();
            Await(shown);
        });
    }

    [Fact]
    public void Toasts_keep_the_newest_and_expire_on_time()
    {
        RunOnSta(() =>
        {
            var clock = new ManualClock();
            var host = new ToastHost { MaxVisible = 2, Time = clock };
            Host(host);

            var a = host.Show(new ToastMessage { Title = "A" });
            var b = host.Show(new ToastMessage { Title = "B", Duration = TimeSpan.FromSeconds(10) });
            var sticky = host.Show(new ToastMessage { Title = "C", Duration = null });
            Assert.Equal([b, sticky], host.Items); // oldest dropped beyond MaxVisible

            clock.Advance(TimeSpan.FromSeconds(6));
            host.RemoveExpired();
            Assert.Equal([b, sticky], host.Items);

            clock.Advance(TimeSpan.FromSeconds(5));
            host.RemoveExpired();
            Assert.Equal([sticky], host.Items);
            Assert.DoesNotContain(a, host.Items);
        });
    }

    [Fact]
    public void Toast_action_runs_once_and_dismisses_the_toast()
    {
        RunOnSta(() =>
        {
            var host = new ToastHost();
            Host(host);
            var runs = 0;
            var toast = host.Show(new ToastMessage { Title = "Task șters", ActionText = "Anulează", Action = () => runs++ });

            ToastHost.RunActionCommand.Execute(toast, host);
            Assert.Equal(1, runs);
            Assert.Empty(host.Items);

            var other = host.Show(new ToastMessage { Title = "x" });
            ToastHost.DismissCommand.Execute(other, host);
            Assert.Empty(host.Items);
        });
    }

    [Fact]
    public void Toast_text_for_screen_readers_combines_title_and_message()
    {
        Assert.Equal("Reminder ratat. Mutat la 10:00", new ToastMessage { Title = "Reminder ratat", Message = "Mutat la 10:00" }.AccessibleText);
        Assert.Equal("Salvat", new ToastMessage { Title = "Salvat" }.AccessibleText);
    }

    [Fact]
    public void Command_palette_filters_runs_the_selection_and_closes()
    {
        RunOnSta(() =>
        {
            var ran = new List<string>();
            var executed = new List<CommandItem>();
            CommandItem[] items =
            [
                new() { Title = "Task nou", Command = new TestCommand(() => ran.Add("new")) },
                new() { Title = "Mergi la Calendar", Command = new TestCommand(() => ran.Add("calendar")) },
                new() { Title = "Mergi la My Day", Command = new TestCommand(() => ran.Add("myday")) },
            ];
            var palette = new CommandPalette { ItemsSource = items };
            palette.Executed += (_, item) => executed.Add(item);
            Host(palette);

            palette.IsOpen = true;
            Layout(palette);
            Assert.Equal(Visibility.Visible, palette.Visibility);
            Assert.Equal(3, palette.Results.Count);

            palette.Query = "mergi";
            Assert.Equal(2, palette.Results.Count);
            palette.MoveSelection(1);
            Assert.True(palette.ExecuteSelected());

            Assert.Equal(["myday"], ran);
            Assert.Equal("Mergi la My Day", Assert.Single(executed).Title);
            Assert.False(palette.IsOpen);
            Assert.Equal(Visibility.Collapsed, palette.Visibility);
        });
    }

    [Fact]
    public void Command_palette_selection_wraps_and_reopening_clears_the_query()
    {
        RunOnSta(() =>
        {
            CommandItem[] items = [new() { Title = "A" }, new() { Title = "B" }];
            var palette = new CommandPalette { ItemsSource = items };
            Host(palette);
            palette.IsOpen = true;
            Layout(palette);

            palette.MoveSelection(-1);
            Assert.Equal(1, palette.ResultList!.SelectedIndex);
            palette.MoveSelection(1);
            Assert.Equal(0, palette.ResultList.SelectedIndex);

            palette.Query = "zzz";
            Assert.False(palette.HasResults);
            Assert.False(palette.ExecuteSelected());

            palette.IsOpen = false;
            palette.IsOpen = true;
            Assert.Equal(string.Empty, palette.Query);
            Assert.True(palette.HasResults);
        });
    }

    private static T FindDescendant<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found))
            {
                return found;
            }

            if (FindDescendantOrNull(child, match) is { } nested)
            {
                return nested;
            }
        }

        throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} found");
    }

    private static T? FindDescendantOrNull<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        try
        {
            return FindDescendant(root, match);
        }
        catch (Xunit.Sdk.XunitException)
        {
            return null;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class TestCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }
}

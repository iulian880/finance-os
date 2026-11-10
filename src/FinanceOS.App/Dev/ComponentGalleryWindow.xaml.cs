using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FinanceOS.UI.Controls;
using FinanceOS.UI.Theming;
using UiIcon = FinanceOS.UI.Controls.Icon;

namespace FinanceOS.App.Dev;

/// <summary>Developer page with every design-system component in every state (M2 + M3 deliverables).</summary>
public partial class ComponentGalleryWindow : Window
{
    private static readonly RoutedCommand OpenPaletteCommand = new(nameof(OpenPaletteCommand), typeof(ComponentGalleryWindow));

    private static readonly (ThemePreference Value, string Label)[] ThemeOptions =
        [(ThemePreference.System, "Sistem"), (ThemePreference.Light, "Luminos"), (ThemePreference.Dark, "Întunecat")];

    private bool _syncing;

    public ComponentGalleryWindow()
    {
        InitializeComponent();
        WindowTheming.Track(this);

        // Sample values relative to today, so the page never shows stale dates.
        var today = DateTime.Today;
        StartDate.SelectedDate = today;
        ArchivedDate.SelectedDate = today.AddDays(-12);
        InlineCalendar.SelectedDate = today.AddDays(3);
        InlineCalendar.DisplayDate = today;
        FollowUpTime.SelectedTime = new TimeSpan(9, 30, 0);
        LockedTime.SelectedTime = new TimeSpan(17, 0, 0);

        ThemePicker.ItemsSource = ThemeOptions.Select(o => o.Label).ToList();
        AccentPicker.ItemsSource = Accents.All;
        SyncPickers();

        BuildSkeletonRows();
        BuildIconGrid();
        Palette.ItemsSource = BuildCommands();
        Palette.Executed += (_, item) => LastResult.Text = $"Ultima acțiune: comanda „{item.Title}”";

        CommandBindings.Add(new CommandBinding(OpenPaletteCommand, (_, _) => Palette.IsOpen = true));
        InputBindings.Add(new KeyBinding(OpenPaletteCommand, Key.K, ModifierKeys.Control));

        ThemeManager.Current.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Current.Changed -= OnThemeChanged;
        DpiChanged += (_, _) => SyncPickers();
    }

    /// <summary>
    /// Opens each overlay (drop-downs, context menu, dialog, toasts, palette) in turn and returns what to
    /// capture, for --screenshot. Popups are separate windows and in-window overlays sit outside the page.
    /// </summary>
    internal async IAsyncEnumerable<(string Name, FrameworkElement Content)> OpenOverlaysForCapture()
    {
        (string Name, Action<bool> Toggle, Control Owner)[] dropDowns =
        [
            ("select", open => SampleSelect.IsDropDownOpen = open, SampleSelect),
            ("datepicker", open => SampleDate.IsDropDownOpen = open, SampleDate),
            ("timepicker", open => FollowUpTime.IsDropDownOpen = open, FollowUpTime),
        ];

        foreach (var (name, toggle, owner) in dropDowns)
        {
            toggle(true);
            await Idle();
            if (owner.Template.FindName("PART_Popup", owner) is Popup { Child: FrameworkElement child })
            {
                yield return (name, child);
            }

            toggle(false);
        }

        TaskMenu.PlacementTarget = TaskCard;
        TaskMenu.IsOpen = true;
        await Idle();
        if (TaskMenu.Items[2] is MenuItem postpone)
        {
            postpone.IsSubmenuOpen = true;
            await Idle();
        }

        yield return ("contextmenu", TaskMenu);
        TaskMenu.IsOpen = false;

        var dialog = CreateDeleteDialog();
        _ = Dialogs.ShowAsync(dialog);
        await Idle();
        yield return ("dialog", Root);
        dialog.Hide();

        var form = CreateTaskDialog(out _);
        _ = Dialogs.ShowAsync(form);
        await Idle();
        yield return ("dialog-form", Root);
        form.Hide();
        await Idle();

        Toasts.Show(new ToastMessage { Title = "Task finalizat", Message = "„Reconciliere bancară BT” a fost mutat în Completed.", Tone = ToastTone.Success, Duration = null });
        Toasts.Show(new ToastMessage { Title = "Task șters", Tone = ToastTone.Info, ActionText = "Anulează", Action = () => { }, Duration = null });
        Toasts.Show(new ToastMessage { Title = "Reminder ratat", Message = "PC-ul era în sleep la 09:30. Follow-up-ul a fost mutat la 10:00.", Tone = ToastTone.Warning, Duration = null });
        await Idle();
        yield return ("toasts", Root);
        foreach (var toast in Toasts.Items.ToList())
        {
            Toasts.Dismiss(toast);
        }

        Palette.IsOpen = true;
        await Idle();
        yield return ("palette", Root);
        Palette.Query = "sedinta";
        await Idle();
        yield return ("palette-search", Root);
        Palette.Query = "xyz123";
        await Idle();
        yield return ("palette-empty", Root);
        Palette.IsOpen = false;
    }

    private Task Idle() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private void OnThemeChanged(object? sender, EventArgs e) => SyncPickers();

    private void SyncPickers()
    {
        _syncing = true;
        var tm = ThemeManager.Current;
        ThemePicker.SelectedIndex = Array.FindIndex(ThemeOptions, o => o.Value == tm.Preference);
        AccentPicker.SelectedItem = tm.Accent;
        _syncing = false;

        var dpi = VisualTreeHelper.GetDpi(this);
        StateText.Text = $"{(tm.IsDark ? "Întunecat" : "Luminos")} · {tm.Accent.Name} · scalare {dpi.DpiScaleX:P0}";
    }

    private void OnThemePicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && ThemePicker.SelectedIndex >= 0)
        {
            ThemeManager.Current.SetPreference(ThemeOptions[ThemePicker.SelectedIndex].Value);
        }
    }

    private void OnAccentPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && AccentPicker.SelectedItem is AccentOption accent)
        {
            ThemeManager.Current.SetAccent(accent);
        }
    }

    // --- M3 samples -------------------------------------------------------------------------

    private void OnCardMenuClick(object sender, RoutedEventArgs e)
    {
        TaskMenu.PlacementTarget = (UIElement)sender;
        TaskMenu.Placement = PlacementMode.Bottom;
        TaskMenu.IsOpen = true;
    }

    private async void OnFormDialogClick(object sender, RoutedEventArgs e)
    {
        var dialog = CreateTaskDialog(out var title);
        var result = await Dialogs.ShowAsync(dialog);
        LastResult.Text = result == DialogOutcome.Primary
            ? $"Ultima acțiune: dialog → Creează („{title.Text}”)"
            : $"Ultima acțiune: dialog → {result}";
        if (result == DialogOutcome.Primary)
        {
            Toasts.Show(new ToastMessage { Title = "Task creat", Message = title.Text, Tone = ToastTone.Success });
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var result = await Dialogs.ShowAsync(CreateDeleteDialog());
        LastResult.Text = $"Ultima acțiune: dialog distructiv → {result}";
        if (result == DialogOutcome.Primary)
        {
            Toasts.Show(new ToastMessage
            {
                Title = "Task șters",
                Message = "„Reconciliere bancară BT”",
                ActionText = "Anulează",
                Action = () => Toasts.Show(new ToastMessage { Title = "Task restaurat", Tone = ToastTone.Success }),
            });
        }
    }

    private void OnToastClick(object sender, RoutedEventArgs e)
    {
        var toast = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "Success" => new ToastMessage { Title = "Task finalizat", Message = "„Trimite raportul TVA” a fost mutat în Completed.", Tone = ToastTone.Success },
            "Undo" => new ToastMessage
            {
                Title = "3 taskuri arhivate",
                ActionText = "Anulează",
                Action = () => LastResult.Text = "Ultima acțiune: arhivarea a fost anulată",
            },
            "Warning" => new ToastMessage { Title = "Reminder ratat", Message = "PC-ul era în sleep la 09:30. Follow-up-ul a fost mutat la 10:00.", Tone = ToastTone.Warning },
            _ => new ToastMessage { Title = "Exportul nu a reușit", Message = "Fișierul este deschis în Excel. Închide-l și încearcă din nou.", Tone = ToastTone.Danger, Duration = TimeSpan.FromSeconds(8) },
        };
        Toasts.Show(toast);
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e) => Palette.IsOpen = true;

    private static Dialog CreateDeleteDialog() => new()
    {
        Title = "Ștergi taskul?",
        Content = "„Reconciliere bancară BT” și cele 3 remindere ale lui vor fi șterse. Îl poți restaura din Arhivă.",
        PrimaryButtonText = "Șterge",
        SecondaryButtonText = "Anulează",
        IsPrimaryDestructive = true,
    };

    private static Dialog CreateTaskDialog(out TextBox title)
    {
        title = new TextBox();
        FieldAssist.SetHeader(title, "Titlu");
        FieldAssist.SetPlaceholder(title, "Ex.: Trimite raportul TVA");

        var due = new DatePicker { Margin = new Thickness(0, 16, 0, 0) };
        FieldAssist.SetHeader(due, "Termen");
        FieldAssist.SetPlaceholder(due, "Alege o dată");

        return new Dialog
        {
            Title = "Task nou",
            PrimaryButtonText = "Creează",
            SecondaryButtonText = "Anulează",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Taskul apare în My Day în ziua termenului.", Margin = new Thickness(0, 0, 0, 16) },
                    title,
                    due,
                },
            },
        };
    }

    private void BuildSkeletonRows()
    {
        for (var i = 0; i < 3; i++)
        {
            var avatar = new Skeleton { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Top };
            var lines = new StackPanel
            {
                Margin = new Thickness(12, 2, 0, 0),
                Children =
                {
                    new Skeleton { Width = 200 - (i * 30), Height = 12 },
                    new Skeleton { Width = 130 + (i * 20), Height = 10, Margin = new Thickness(0, 8, 0, 0) },
                },
            };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, i < 2 ? 18 : 0) };
            DockPanel.SetDock(avatar, Dock.Left);
            row.Children.Add(avatar);
            row.Children.Add(lines);
            SkeletonRows.Children.Add(row);
        }
    }

    private void BuildIconGrid()
    {
        var icons = (ResourceDictionary)Application.LoadComponent(new Uri("/FinanceOS.UI;component/Themes/Icons.xaml", UriKind.Relative));
        foreach (var key in icons.Keys.OfType<string>().Order(StringComparer.Ordinal))
        {
            var label = new TextBlock
            {
                Text = key["Icon.".Length..],
                Style = (Style)FindResource("Text.Caption"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var icon = new UiIcon { Data = (Geometry)icons[key], Size = 20, HorizontalAlignment = HorizontalAlignment.Center };
            icon.SetResourceReference(UiIcon.ForegroundProperty, "Brush.TextPrimary");
            IconGrid.Children.Add(new StackPanel { Width = 112, Margin = new Thickness(0, 0, 0, 20), Children = { icon, label }, ToolTip = key });
        }
    }

    private List<CommandItem> BuildCommands()
    {
        Geometry I(string key) => (Geometry)FindResource(key);
        var toggleTheme = new GalleryCommand(() =>
            ThemeManager.Current.SetPreference(ThemeManager.Current.IsDark ? ThemePreference.Light : ThemePreference.Dark));

        return
        [
            new() { Title = "Task nou", Group = "Task", Icon = I("Icon.Plus"), Shortcut = "Ctrl+N", Keywords = "new create adauga" },
            new() { Title = "Mergi la My Day", Group = "Navigare", Icon = I("Icon.Sun"), Shortcut = "Ctrl+1", Keywords = "azi today" },
            new() { Title = "Mergi la Tasks", Group = "Navigare", Icon = I("Icon.ListChecks"), Shortcut = "Ctrl+2" },
            new() { Title = "Mergi la Calendar", Group = "Navigare", Icon = I("Icon.Calendar"), Shortcut = "Ctrl+3" },
            new() { Title = "Ședință săptămânală — pregătire raport", Subtitle = "Task · scadent vineri", Group = "Task", Icon = I("Icon.Flag") },
            new() { Title = "Reconciliere bancară BT", Subtitle = "Task · scadent azi", Group = "Task", Icon = I("Icon.ListChecks") },
            new() { Title = "Caută în arhivă", Group = "Căutare", Icon = I("Icon.Archive"), Keywords = "archive" },
            new() { Title = "Comută tema (luminos / întunecat)", Group = "Aspect", Icon = I("Icon.Moon"), Keywords = "theme dark light", Command = toggleTheme },
            new() { Title = "Setări", Group = "Navigare", Icon = I("Icon.Settings"), Shortcut = "Ctrl+,", Keywords = "settings preferinte" },
        ];
    }

    /// <summary>Minimal ICommand for gallery samples.</summary>
    private sealed class GalleryCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using FinanceOS.App.Onboarding;
using FinanceOS.App.Reminders;
using FinanceOS.App.Services;
using FinanceOS.App.Tasks;
using FinanceOS.Core.Events;
using FinanceOS.Core.Reminders;
using FinanceOS.Core.Tasks;
using FinanceOS.UI.Controls;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;

namespace FinanceOS.App.Shell;

public enum RightPanelKind
{
    Details,
    Notifications,
}

/// <summary>Sidebar entry. Title and group label follow the language.</summary>
public sealed class NavItemViewModel(SectionInfo info, Localizer localizer) : ObservableObject
{
    public SectionInfo Info { get; } = info;

    public Section Section => Info.Section;

    public string Title => Info.Title(localizer);

    public string IconKey => Info.IconKey;

    public string? GroupHeader => Info.GroupHeader(localizer);

    public string Shortcut => Info.Shortcut;

    /// <summary>Settings sits at the bottom of the sidebar.</summary>
    public bool IsFooter => Section == Section.Settings;

    private bool _isVisible = true;

    /// <summary>False when the current mode hides this section (Simple).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(GroupHeader));
    }

    public override string ToString() => Title;
}

/// <summary>Command offered in the command palette. IconKey refers to Themes/Icons.xaml.</summary>
public sealed record ShellCommand(string Title, string Group, string IconKey, string? Shortcut, string? Keywords, ICommand Command, object? Parameter = null);

/// <summary>
/// State of the main window: current section and page, sidebar and right panel, palette commands.
/// Pages are created on first visit and kept, so a page keeps its state while you move around.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Dictionary<Section, PageViewModel> _pages = [];
    private readonly Func<Section, PageViewModel> _createPage;
    private readonly IThemeService _theme;
    private readonly TimeProvider _time;
    private readonly Localizer _l;
    private readonly TaskService _tasks;
    private readonly IInAppToasts _toasts;
    private readonly UserSettings? _settings;

    [ObservableProperty]
    private NavItemViewModel? _selectedItem;

    [ObservableProperty]
    private PageViewModel _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded))]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsPanelOpen), nameof(IsNotificationsPanelOpen))]
    private bool _isRightPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsPanelOpen), nameof(IsNotificationsPanelOpen), nameof(RightPanelTitle))]
    private RightPanelKind _rightPanel;

    [ObservableProperty]
    private bool _isPaletteOpen;

    /// <summary>Palette commands; rebuilt when the language changes (titles and groups are text).</summary>
    [ObservableProperty]
    private IReadOnlyList<ShellCommand> _commands;

    /// <summary>The first-run guide shown over the whole window, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseShell))]
    private OnboardingViewModel? _onboarding;

    /// <summary>False while the first-run guide is open: the title bar buttons wait for it.</summary>
    public bool CanUseShell => Onboarding is null;

    public ShellViewModel(IThemeService theme, TimeProvider time, Func<Section, PageViewModel> createPage, Localizer localizer, TaskService tasks, IInAppToasts toasts,
        NotificationCenterViewModel notifications, UserSettings? settings = null)
    {
        Notifications = notifications;
        notifications.OpenTask = OpenTask;
        _theme = theme;
        _time = time;
        _createPage = createPage;
        _l = localizer;
        _tasks = tasks;
        _toasts = toasts;
        NavItems = SectionCatalog.All.Select(info => new NavItemViewModel(info, localizer)).ToList();
        _currentPage = GetPage(Section.Overview);
        _currentPage.PropertyChanged += OnPagePropertyChanged;
        _selectedItem = NavItems[0];
        _commands = BuildCommands();
        localizer.Changed += (_, _) => OnLanguageChanged();
        _settings = settings;
        if (settings is not null)
        {
            ApplyCapabilities();
            settings.Changed += (_, _) => ApplyCapabilities();
        }
    }

    /// <summary>What the current mode shows; everything without saved settings (tests, tools).</summary>
    public Capabilities Capabilities => _settings?.Capabilities ?? Capabilities.Advanced;

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    /// <summary>The notification center shown in the right panel; its unread count badges the bell.</summary>
    public NotificationCenterViewModel Notifications { get; }

    /// <summary>"Marți, 4 noiembrie" in the title bar.</summary>
    public string TodayText => DisplayFormat.DayAndMonth(_time.GetLocalNow().DateTime, _l.Culture);

    public bool IsSidebarExpanded => !IsSidebarCollapsed;

    public bool IsDetailsPanelOpen => IsRightPanelOpen && RightPanel == RightPanelKind.Details;

    public bool IsNotificationsPanelOpen => IsRightPanelOpen && RightPanel == RightPanelKind.Notifications;

    public string RightPanelTitle => _l[RightPanel == RightPanelKind.Details ? "Shell.Details" : "Shell.Notifications"];

    public Section CurrentSection => CurrentPage.Section;

    [RelayCommand]
    private void Navigate(Section section)
    {
        // Shortcuts and links to a section the mode hides do nothing.
        if (Capabilities.Shows(section))
        {
            SelectedItem = NavItems.First(item => item.Section == section);
        }
    }

    [RelayCommand]
    private void ToggleMode() =>
        _settings?.SetMode(_settings.Mode == ExperienceMode.Advanced ? ExperienceMode.Simple : ExperienceMode.Advanced);

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void ToggleDetails() => TogglePanel(RightPanelKind.Details);

    [RelayCommand]
    private void ToggleNotifications() => TogglePanel(RightPanelKind.Notifications);

    [RelayCommand]
    private void CloseRightPanel() => IsRightPanelOpen = false;

    [RelayCommand]
    private void OpenPalette() => IsPaletteOpen = true;

    [RelayCommand]
    private void ToggleTheme() => _theme.SetPreference(_theme.IsDark ? ThemePreference.Light : ThemePreference.Dark);

    partial void OnSelectedItemChanged(NavItemViewModel? value)
    {
        if (value is not null)
        {
            CurrentPage = GetPage(value.Section);
        }
    }

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnPagePropertyChanged;
        }

        newValue.PropertyChanged += OnPagePropertyChanged;
        newValue.OnActivated();
        OnPropertyChanged(nameof(CurrentSection));
    }

    /// <summary>Opens the first-run guide (needs saved settings: its choices are kept).</summary>
    public void StartOnboarding()
    {
        if (_settings is null || GetPage(Section.Settings) is not SettingsPageViewModel settingsPage)
        {
            return;
        }

        Onboarding = new OnboardingViewModel(settingsPage, _settings, _l, _tasks.List(TaskScope.All).Count > 0,
            () => DemoData.Add(_tasks, _time, _l), FinishOnboarding);
    }

    private void FinishOnboarding(int demoTasks)
    {
        Onboarding = null;
        Navigate(Section.Overview);
        if (demoTasks > 0)
        {
            _toasts.Show(new ToastMessage
            {
                Title = _l["Toast.DemoAdded.Title"],
                Message = _l.Format("Toast.DemoAdded.Body", demoTasks),
                Tone = ToastTone.Success,
            });
        }
    }

    /// <summary>Shows a task: goes to Tasks and opens it in the details panel (from a notification or a toast).</summary>
    public void OpenTask(string taskId)
    {
        Navigate(Section.Tasks);
        if (CurrentPage is TaskListPageViewModel page)
        {
            page.Select(taskId);
        }
    }

    /// <summary>Ctrl+N: go to Tasks and put the cursor in the quick-add box.</summary>
    [RelayCommand]
    private void NewTask()
    {
        Navigate(Section.Tasks);
        (CurrentPage as TasksPageViewModel)?.RequestQuickAdd();
    }

    /// <summary>Ctrl+Z: reverts the last task action of this session and says what was undone.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_tasks.UndoCommand is not { } command)
        {
            _toasts.Show(new ToastMessage { Title = _l["Toast.NothingToUndo"] });
            return;
        }

        try
        {
            _tasks.Undo();
            _toasts.Show(new ToastMessage { Title = _l["Toast.Undone.Title"], Message = TaskText.Action(command, _l), Tone = ToastTone.Success });
        }
        catch (UndoConflictException)
        {
            _toasts.Show(new ToastMessage { Title = _l["Toast.UndoConflict.Title"], Message = _l["Toast.UndoConflict.Body"], Tone = ToastTone.Warning });
        }
    }

    /// <summary>A page that gets something to show in the details panel (a task was selected) opens it.</summary>
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.Details) && sender == CurrentPage && CurrentPage.Details is not null)
        {
            RightPanel = RightPanelKind.Details;
            IsRightPanelOpen = true;
        }
    }

    private void OnLanguageChanged()
    {
        foreach (var item in NavItems)
        {
            item.Refresh();
        }

        Commands = BuildCommands();
        OnPropertyChanged(nameof(TodayText));
        OnPropertyChanged(nameof(RightPanelTitle));
    }

    /// <summary>Shows only the sections of the current mode; leaves a section that just got hidden.</summary>
    private void ApplyCapabilities()
    {
        var capabilities = Capabilities;
        foreach (var item in NavItems)
        {
            item.IsVisible = capabilities.Shows(item.Section);
        }

        if (!capabilities.Shows(CurrentSection))
        {
            Navigate(Section.Overview);
        }

        Commands = BuildCommands();
        OnPropertyChanged(nameof(Capabilities));
    }

    private void TogglePanel(RightPanelKind kind)
    {
        if (IsRightPanelOpen && RightPanel == kind)
        {
            IsRightPanelOpen = false;
            return;
        }

        RightPanel = kind;
        IsRightPanelOpen = true;
    }

    private PageViewModel GetPage(Section section)
    {
        if (!_pages.TryGetValue(section, out var page))
        {
            page = _createPage(section);
            _pages[section] = page;
        }

        return page;
    }

    private List<ShellCommand> BuildCommands()
    {
        // Keywords stay bilingual, so a command is found by its name in either language.
        var commands = NavItems
            .Where(item => item.IsVisible)
            .Select(item => new ShellCommand(_l.Format("Command.GoTo", item.Title), _l["Command.Group.Navigation"], item.IconKey, item.Shortcut,
                $"{item.Info.Subtitle(_l)} {item.Section}", NavigateCommand, item.Section))
            .ToList();

        var appearance = _l["Command.Group.Appearance"];
        var panel = _l["Command.Group.Panel"];
        var tasks = _l["Command.Group.Tasks"];
        commands.Insert(0, new(_l["Command.NewTask"], tasks, "Icon.Plus", "Ctrl+N", "new task nou adaugă add", NewTaskCommand));
        commands.Insert(1, new(_l["Command.Undo"], tasks, "Icon.Undo", "Ctrl+Z", "undo anulează înapoi", UndoCommand));
        commands.Add(new(_l["Command.ToggleTheme"], appearance, "Icon.Moon", null, "theme dark light tema întunecat luminos", ToggleThemeCommand));
        commands.Add(new(_l["Command.ToggleSidebar"], appearance, "Icon.PanelLeft", "Ctrl+B", "sidebar menu meniu bara", ToggleSidebarCommand));
        commands.Add(new(_l["Command.ShowNotifications"], panel, "Icon.Bell", null, "notifications notificări reminder", ToggleNotificationsCommand));
        commands.Add(new(_l["Command.ShowDetails"], panel, "Icon.PanelLeft", null, "details detalii", ToggleDetailsCommand));
        if (_settings is not null)
        {
            var key = _settings.Mode == ExperienceMode.Simple ? "Command.Mode.ToAdvanced" : "Command.Mode.ToSimple";
            commands.Add(new(_l[key], appearance, "Icon.LayoutDashboard", null, "mode mod simple simplu advanced avansat", ToggleModeCommand));
        }
        return commands;
    }
}

/// <summary>Everything a page may need, handed over as one bundle by the composition root.</summary>
public sealed record PageServices(
    IThemeService Theme, TimeProvider Time, IDataMaintenance Data, ILauncher Launcher, Localizer Localizer,
    TaskService Tasks, IEventBus Bus, IInAppToasts Toasts, ReminderService Reminders, IStartupRegistration? Startup = null, UserSettings? Settings = null);

/// <summary>Creates the page for each section (composition root registers this as the page factory).</summary>
public static class PageFactory
{
    public static PageViewModel Create(Section section, PageServices s) => section switch
    {
        Section.Overview => new OverviewPageViewModel(s.Tasks, s.Bus, s.Time, s.Localizer, s.Toasts, s.Launcher, s.Reminders) { Settings = s.Settings },
        Section.MyDay => new MyDayPageViewModel(s.Tasks, s.Bus, s.Time, s.Localizer, s.Toasts, s.Launcher, s.Reminders) { Settings = s.Settings },
        Section.Calendar => new CalendarPageViewModel(s.Tasks, s.Bus, s.Time, s.Localizer, s.Toasts, s.Launcher, s.Reminders, s.Settings),
        Section.Tasks => new TasksPageViewModel(s.Tasks, s.Bus, s.Time, s.Localizer, s.Toasts, s.Launcher, s.Reminders) { Settings = s.Settings },
        Section.Settings => new SettingsPageViewModel(s.Theme, s.Data, s.Launcher, s.Time, s.Localizer, s.Startup, s.Settings, s.Tasks),
        _ => new ComingSoonPageViewModel(SectionCatalog.Get(section), s.Localizer),
    };
}

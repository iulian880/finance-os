using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using FinanceOS.App.Services;
using FinanceOS.Platform.Windows.Windowing;
using FinanceOS.UI.Controls;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;

namespace FinanceOS.App.Shell;

/// <summary>
/// Main window. View-only concerns live here (backdrop, placement, transitions, palette item mapping);
/// all state and behavior is in <see cref="ShellViewModel"/>.
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>Three Windows 11 caption buttons (46 DIP each) plus a little breathing room.</summary>
    private const double NativeCaptionButtonsWidth = 146;

    private readonly ShellViewModel _viewModel;
    private readonly WindowPlacementStore _placement;

    public ShellWindow(ShellViewModel viewModel, WindowPlacementStore placement, InAppToasts toasts, Localizer localizer)
    {
        _viewModel = viewModel;
        _placement = placement;
        InitializeComponent();
        DataContext = viewModel;
        WindowTheming.Track(this);

        // ViewModels show toasts here while this window is open.
        toasts.Host = Toasts;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(toasts.Host, Toasts))
            {
                toasts.Host = null;
            }
        };

        // Date and time pickers format and parse in the UI language (WPF's default would be en-US).
        void ApplyLanguage(object? sender, EventArgs e) => Language = XmlLanguage.GetLanguage(localizer.Culture.IetfLanguageTag);
        ApplyLanguage(null, EventArgs.Empty);
        localizer.Changed += ApplyLanguage;
        Closed += (_, _) => localizer.Changed -= ApplyLanguage;

        Palette.ItemsSource = viewModel.Commands.Select(ToCommandItem).ToList();

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateMaximizedPadding();
        DpiChanged += (_, _) => UpdateMaximizedPadding();
        Closing += (_, _) => SavePlacement();
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
    }

    /// <summary>True when Windows 11 Mica is active behind the sidebar and title bar.</summary>
    public bool HasMica { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_placement.Load() is { } saved)
        {
            WindowPlacementStore.Apply(handle, saved);
        }

        // Mica needs the frame extended into the whole client area and nothing opaque painted behind it.
        WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(-1);
        HasMica = WindowBackdrop.TryApplyMica(this);
        if (HasMica)
        {
            Background = Brushes.Transparent;

            // With the frame extended, DWM draws the real caption buttons; use them instead of ours.
            WindowChrome.GetWindowChrome(this).UseAeroCaptionButtons = true;
            Caption.Visibility = Visibility.Collapsed;
            CaptionSpacer.Width = NativeCaptionButtonsWidth;
        }
        else
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(0);
        }

        UpdateMaximizedPadding();
    }

    private void UpdateMaximizedPadding() =>
        Root.Margin = WindowState == WindowState.Maximized ? WindowBackdrop.MaximizedFrameThickness(this) : new Thickness(0);

    private void SavePlacement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && WindowPlacementStore.Capture(handle) is { } current)
        {
            _placement.Save(current);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentPage):
                PageScroller.ScrollToTop();
                Motion.Enter(PageHost, offsetY: 6);
                break;
            case nameof(ShellViewModel.Commands):
                // Language changed: same commands, new titles.
                Palette.ItemsSource = _viewModel.Commands.Select(ToCommandItem).ToList();
                break;
            case nameof(ShellViewModel.IsRightPanelOpen) when _viewModel.IsRightPanelOpen:
                Motion.Enter(RightPanel, offsetY: 0);
                break;
        }
    }

    private CommandItem ToCommandItem(ShellCommand command) => new()
    {
        Title = command.Title,
        Group = command.Group,
        Keywords = command.Keywords,
        Shortcut = command.Shortcut,
        Icon = TryFindResource(command.IconKey) as Geometry,
        Command = command.Command,
        CommandParameter = command.Parameter,
    };
}

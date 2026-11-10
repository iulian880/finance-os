using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Shell;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Minimize / maximize-restore / close buttons for a window with custom chrome (WindowChrome).
/// The maximize button answers WM_NCHITTEST with HTMAXBUTTON, so Windows 11 shows Snap Layouts on hover;
/// because the system then owns the mouse over that button, its hover/pressed look is driven from the
/// window messages (<see cref="IsMaximizeHovered"/>, <see cref="IsMaximizePressed"/>).
/// Like native caption buttons they are not tab stops; Alt+Space opens the system menu.
/// </summary>
[TemplatePart(Name = PartMinimize, Type = typeof(ButtonBase))]
[TemplatePart(Name = PartMaximize, Type = typeof(ButtonBase))]
[TemplatePart(Name = PartClose, Type = typeof(ButtonBase))]
public class CaptionButtons : Control
{
    public const string PartMinimize = "PART_Minimize";
    public const string PartMaximize = "PART_Maximize";
    public const string PartClose = "PART_Close";

    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseLeave = 0x02A2;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int HtMaxButton = 9;

    private static readonly DependencyPropertyKey IsWindowMaximizedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsWindowMaximized), typeof(bool), typeof(CaptionButtons), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsWindowMaximizedProperty = IsWindowMaximizedPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsWindowActivePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsWindowActive), typeof(bool), typeof(CaptionButtons), new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty IsWindowActiveProperty = IsWindowActivePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsMaximizeHoveredPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsMaximizeHovered), typeof(bool), typeof(CaptionButtons), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsMaximizeHoveredProperty = IsMaximizeHoveredPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsMaximizePressedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsMaximizePressed), typeof(bool), typeof(CaptionButtons), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsMaximizePressedProperty = IsMaximizePressedPropertyKey.DependencyProperty;

    public static readonly DependencyProperty MinimizeTextProperty = DependencyProperty.Register(
        nameof(MinimizeText), typeof(string), typeof(CaptionButtons), new FrameworkPropertyMetadata("Minimize"));

    public static readonly DependencyProperty MaximizeTextProperty = DependencyProperty.Register(
        nameof(MaximizeText), typeof(string), typeof(CaptionButtons), new FrameworkPropertyMetadata("Maximize"));

    public static readonly DependencyProperty RestoreTextProperty = DependencyProperty.Register(
        nameof(RestoreText), typeof(string), typeof(CaptionButtons), new FrameworkPropertyMetadata("Restore"));

    public static readonly DependencyProperty CloseTextProperty = DependencyProperty.Register(
        nameof(CloseText), typeof(string), typeof(CaptionButtons), new FrameworkPropertyMetadata("Close"));

    private ButtonBase? _minimize;
    private ButtonBase? _maximize;
    private ButtonBase? _close;
    private Window? _window;
    private HwndSource? _source;

    static CaptionButtons()
    {
        FocusableProperty.OverrideMetadata(typeof(CaptionButtons), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(CaptionButtons), new FrameworkPropertyMetadata(false));
        WindowChrome.IsHitTestVisibleInChromeProperty.OverrideMetadata(typeof(CaptionButtons), new FrameworkPropertyMetadata(true));
    }

    public CaptionButtons()
    {
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    public bool IsWindowMaximized => (bool)GetValue(IsWindowMaximizedProperty);

    public bool IsWindowActive => (bool)GetValue(IsWindowActiveProperty);

    public bool IsMaximizeHovered => (bool)GetValue(IsMaximizeHoveredProperty);

    public bool IsMaximizePressed => (bool)GetValue(IsMaximizePressedProperty);

    public string MinimizeText
    {
        get => (string)GetValue(MinimizeTextProperty);
        set => SetValue(MinimizeTextProperty, value);
    }

    public string MaximizeText
    {
        get => (string)GetValue(MaximizeTextProperty);
        set => SetValue(MaximizeTextProperty, value);
    }

    public string RestoreText
    {
        get => (string)GetValue(RestoreTextProperty);
        set => SetValue(RestoreTextProperty, value);
    }

    public string CloseText
    {
        get => (string)GetValue(CloseTextProperty);
        set => SetValue(CloseTextProperty, value);
    }

    public override void OnApplyTemplate()
    {
        if (_minimize is not null)
        {
            _minimize.Click -= OnMinimize;
        }

        if (_maximize is not null)
        {
            _maximize.Click -= OnMaximize;
        }

        if (_close is not null)
        {
            _close.Click -= OnClose;
        }

        base.OnApplyTemplate();
        _minimize = GetTemplateChild(PartMinimize) as ButtonBase;
        _maximize = GetTemplateChild(PartMaximize) as ButtonBase;
        _close = GetTemplateChild(PartClose) as ButtonBase;

        if (_minimize is not null)
        {
            _minimize.Click += OnMinimize;
        }

        if (_maximize is not null)
        {
            _maximize.Click += OnMaximize;
        }

        if (_close is not null)
        {
            _close.Click += OnClose;
        }
    }

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += OnWindowStateChanged;
        _window.Activated += OnActivationChanged;
        _window.Deactivated += OnActivationChanged;
        OnWindowStateChanged(null, EventArgs.Empty);
        OnActivationChanged(null, EventArgs.Empty);

        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WndProc);
    }

    private void Detach()
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowStateChanged;
            _window.Activated -= OnActivationChanged;
            _window.Deactivated -= OnActivationChanged;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _window = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) =>
        SetValue(IsWindowMaximizedPropertyKey, _window?.WindowState == WindowState.Maximized);

    private void OnActivationChanged(object? sender, EventArgs e) =>
        SetValue(IsWindowActivePropertyKey, _window?.IsActive ?? true);

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            SystemCommands.MinimizeWindow(_window);
        }
    }

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            SystemCommands.CloseWindow(_window);
        }
    }

    private void ToggleMaximize()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(_window);
        }
        else
        {
            SystemCommands.MaximizeWindow(_window);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                var over = IsOverMaximize(lParam);
                SetValue(IsMaximizeHoveredPropertyKey, over);
                if (!over)
                {
                    SetValue(IsMaximizePressedPropertyKey, false);
                    return IntPtr.Zero;
                }

                handled = true;
                return HtMaxButton;

            case WmNcMouseLeave:
                SetValue(IsMaximizeHoveredPropertyKey, false);
                SetValue(IsMaximizePressedPropertyKey, false);
                break;

            case WmNcLButtonDown when wParam == HtMaxButton:
                SetValue(IsMaximizePressedPropertyKey, true);
                handled = true;
                break;

            case WmNcLButtonUp when wParam == HtMaxButton:
                var wasPressed = IsMaximizePressed;
                SetValue(IsMaximizePressedPropertyKey, false);
                if (wasPressed)
                {
                    ToggleMaximize();
                }

                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximize(IntPtr lParam)
    {
        if (_maximize is not { IsVisible: true } button || PresentationSource.FromVisual(button) is null)
        {
            return false;
        }

        // lParam carries screen coordinates in physical pixels (signed 16-bit each).
        var value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        var local = button.PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X < button.ActualWidth && local.Y < button.ActualHeight;
    }
}

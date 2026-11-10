using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FinanceOS.UI.Theming;

/// <summary>
/// Windows 11 Mica backdrop for custom-chrome windows. The window must extend its frame into the whole
/// client area (WindowChrome GlassFrameThickness = -1) and keep transparent backgrounds where Mica should
/// show. On Windows 10, or if DWM refuses, the caller keeps solid backgrounds. With "Transparency effects"
/// off in Windows, DWM itself renders Mica as a solid color.
/// </summary>
public static partial class WindowBackdrop
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    /// <summary>Windows 11 22H2 (build 22621) introduced the system backdrop attribute.</summary>
    public static bool IsMicaSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Applies Mica after the window handle exists. Returns false when the solid fallback must be used.</summary>
    public static bool TryApplyMica(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !IsMicaSupported)
        {
            return false;
        }

        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
        {
            // WPF paints an opaque background behind everything unless told otherwise.
            target.BackgroundColor = Colors.Transparent;
        }

        var value = DwmsbtMainWindow;
        return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref value, sizeof(int)) == 0;
    }

    /// <summary>
    /// A maximized custom-chrome window extends past the screen by the resize frame; pad the content by this
    /// much (in DIPs, for the window's current monitor DPI) so nothing is cut off.
    /// </summary>
    public static Thickness MaximizedFrameThickness(Window window)
    {
        const int smCxFrame = 32;
        const int smCyFrame = 33;
        const int smCxPaddedBorder = 92;

        var dpi = VisualTreeHelper.GetDpi(window);
        var pixelsPerInch = (uint)Math.Round(dpi.PixelsPerInchX);
        var padded = GetSystemMetricsForDpi(smCxPaddedBorder, pixelsPerInch);
        var x = (GetSystemMetricsForDpi(smCxFrame, pixelsPerInch) + padded) / dpi.DpiScaleX;
        var y = (GetSystemMetricsForDpi(smCyFrame, pixelsPerInch) + padded) / dpi.DpiScaleY;
        return new Thickness(x, y, x, y);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}

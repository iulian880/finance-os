using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FinanceOS.UI.Theming;

/// <summary>Matches the native title bar to the app theme (Windows 10 20H1+ / 11; no-op elsewhere).</summary>
public static partial class WindowTheming
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Call once per window (e.g. in its constructor); keeps the title bar in sync with theme changes.</summary>
    public static void Track(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window, ThemeManager.Current.IsDark);
    }

    public static void ApplyTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return; // Not shown yet; Track() applies it on SourceInitialized.
        }

        var value = dark ? 1 : 0;
        // Failure (older Windows) only means a light title bar; nothing to recover.
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

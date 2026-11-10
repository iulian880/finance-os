using System.Runtime.InteropServices;
using System.Text.Json;

namespace FinanceOS.Platform.Windows.Windowing;

/// <summary>
/// Restored-state bounds (physical pixels, workspace coordinates, as Win32 reports them) plus whether
/// the window was maximized. Minimized is never stored: the app always reopens visible.
/// </summary>
public sealed record SavedWindowPlacement(int Left, int Top, int Right, int Bottom, bool IsMaximized)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// Remembers the main window's size and position between sessions in <c>window.json</c>.
/// Uses Win32 Get/SetWindowPlacement, which keeps the restored size of a maximized window and moves a
/// window back on screen when its monitor is gone (multi-monitor safe, DPI-aware per monitor).
/// Any read or write problem means "use the default placement": it must never block startup.
/// </summary>
public sealed partial class WindowPlacementStore(string path)
{
    private const int SwShowNormal = 1;
    private const int SwShowMaximized = 3;

    public WindowPlacementStore()
        : this(Path.Combine(AppPaths.DataRoot, "window.json"))
    {
    }

    public string FilePath { get; } = path;

    public SavedWindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var saved = JsonSerializer.Deserialize<SavedWindowPlacement>(File.ReadAllText(FilePath));
            return saved is { Width: > 0, Height: > 0 } ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(SavedWindowPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write-then-rename, so a crash mid-write never leaves a half file behind.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(placement));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is harmless; the next close tries again.
        }
    }

    /// <summary>Reads the current placement of <paramref name="hwnd"/>, or null if Windows refuses.</summary>
    public static SavedWindowPlacement? Capture(IntPtr hwnd)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var r = placement.NormalPosition;
        return new SavedWindowPlacement(r.Left, r.Top, r.Right, r.Bottom, placement.ShowCmd == SwShowMaximized);
    }

    /// <summary>Applies <paramref name="saved"/> to <paramref name="hwnd"/>. Call before the window is first shown.</summary>
    public static void Apply(IntPtr hwnd, SavedWindowPlacement saved)
    {
        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(),
            ShowCmd = saved.IsMaximized ? SwShowMaximized : SwShowNormal,
            NormalPosition = new Rect { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
        };
        _ = SetWindowPlacement(hwnd, ref placement);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
}

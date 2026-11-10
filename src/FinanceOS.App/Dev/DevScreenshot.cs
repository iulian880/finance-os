using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FinanceOS.App.Dev;

/// <summary>
/// Renders a developer page to PNG (--screenshot=path [--scale=1.5]) for design reviews.
/// The whole scrollable page is captured, not just the visible part. Scale renders the vector UI
/// at a higher pixel density; it approximates, but does not replace, a real 150 % Windows setting
/// (layout rounding and text hinting follow the monitor DPI, not the bitmap).
/// </summary>
internal static class DevScreenshot
{
    public static void Capture(Window window, string path, double scale)
    {
        // Pages name their scrollable content "Page"; capture all of it, not just the visible part.
        var page = window.FindName("Page") as FrameworkElement
            ?? (window.Content is ScrollViewer { Content: FrameworkElement inner } ? inner : (FrameworkElement)window.Content);
        CaptureElement(page, window.Background, path, scale);
    }

    /// <summary>Renders one element (including its margin) on <paramref name="background"/>. Works for popup content too.</summary>
    public static void CaptureElement(FrameworkElement element, Brush background, string path, double scale)
    {
        var margin = element.Margin;
        var size = new Size(element.ActualWidth + margin.Left + margin.Right, element.ActualHeight + margin.Top + margin.Bottom);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(size));
            // The brush's coordinate space already includes the element's layout offset (its margin),
            // so the viewbox spans margin + content and is drawn at the origin.
            var brush = new VisualBrush(element)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(size),
                Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(brush, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

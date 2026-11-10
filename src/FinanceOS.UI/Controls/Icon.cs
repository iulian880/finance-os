using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Draws a Lucide geometry (24×24 grid, see Themes/Icons.xaml) as a round 2-unit stroke scaled to
/// <see cref="Size"/>. Color comes from the inherited Foreground, so an icon inside a button follows
/// the button's state colors. Purely decorative: it is not a tab stop and has no automation peer;
/// the owning control carries the accessible name.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender, OnPenChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Stroke width in grid units (24 = icon size). Lucide's default is 2.</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPenChanged));

    private const double Grid = 24;
    private Pen? _pen;

    static Icon()
    {
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is not { } data || Foreground is null)
        {
            return;
        }

        var side = Math.Min(Size, Math.Min(RenderSize.Width, RenderSize.Height));
        var scale = side / Grid;
        var offsetX = (RenderSize.Width - side) / 2;
        var offsetY = (RenderSize.Height - side) / 2;

        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, offsetX, offsetY));
        drawingContext.DrawGeometry(null, GetPen(), data);
        drawingContext.Pop();
    }

    protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;

    private static void OnPenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Icon)d)._pen = null;

    private Pen GetPen()
    {
        if (_pen is null)
        {
            _pen = new Pen(Foreground, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            if (_pen.CanFreeze)
            {
                _pen.Freeze();
            }
        }

        return _pen;
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FinanceOS.UI.Theming;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Short entrance animations (fade + small slide/scale). Every helper is a no-op when Windows
/// animations are off (<see cref="ThemeManager.ReducedMotion"/>), so motion is never required to
/// understand the UI. Final values are set as base values first, so a stopped animation never
/// leaves an element half-visible.
/// </summary>
public static class Motion
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(120));
    public static readonly Duration Normal = new(TimeSpan.FromMilliseconds(180));

    /// <summary>Can be forced off in tests or by a future in-app setting; Windows' setting always wins.</summary>
    public static bool Enabled { get; set; } = true;

    private static bool Active => Enabled && !ThemeManager.ReducedMotion;

    /// <summary>Fades in while moving up from <paramref name="offsetY"/> and growing from <paramref name="fromScale"/>.</summary>
    public static void Enter(UIElement element, double offsetY = 8, double fromScale = 1.0, Duration? duration = null)
    {
        element.Opacity = 1;
        if (!Active)
        {
            return;
        }

        var d = duration ?? Normal;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, translate } };

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        if (offsetY != 0)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }

        if (fromScale != 1.0)
        {
            var grow = new DoubleAnimation(fromScale, 1, d) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    /// <summary>Fades opacity from 0 to 1 only (scrims, overlays).</summary>
    public static void FadeIn(UIElement element, Duration? duration = null)
    {
        element.Opacity = 1;
        if (Active)
        {
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration ?? Fast) { FillBehavior = FillBehavior.Stop });
        }
    }
}

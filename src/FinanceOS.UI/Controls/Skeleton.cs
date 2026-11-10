using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FinanceOS.UI.Theming;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Placeholder block shown while content loads, sized like the content it stands in for.
/// A soft highlight sweeps across it; with Windows animations off it stays static.
/// The animation only runs while the skeleton is loaded (visible in the tree).
/// </summary>
[TemplatePart(Name = PartShimmer, Type = typeof(FrameworkElement))]
public class Skeleton : Control
{
    public const string PartShimmer = "PART_Shimmer";

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(Skeleton), new FrameworkPropertyMetadata(new CornerRadius(6)));

    private static readonly Duration SweepDuration = new(TimeSpan.FromMilliseconds(1400));

    private FrameworkElement? _shimmer;
    private TranslateTransform? _sweep;

    static Skeleton()
    {
        FocusableProperty.OverrideMetadata(typeof(Skeleton), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Skeleton), new FrameworkPropertyMetadata(false));
    }

    public Skeleton()
    {
        Loaded += (_, _) => StartSweep();
        Unloaded += (_, _) => StopSweep();
        SizeChanged += (_, _) => StartSweep();
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>True while the highlight animation runs (false with reduced motion).</summary>
    internal bool IsSweeping { get; private set; }

    public override void OnApplyTemplate()
    {
        StopSweep();
        base.OnApplyTemplate();
        _shimmer = GetTemplateChild(PartShimmer) as FrameworkElement;
        if (_shimmer is not null)
        {
            _sweep = new TranslateTransform();
            _shimmer.RenderTransform = _sweep;
        }

        if (IsLoaded)
        {
            StartSweep();
        }
    }

    private void StartSweep()
    {
        StopSweep();
        if (_shimmer is null || _sweep is null || ActualWidth <= 0 || !Motion.Enabled || ThemeManager.ReducedMotion)
        {
            if (_shimmer is not null)
            {
                _shimmer.Visibility = Visibility.Collapsed;
            }

            return;
        }

        _shimmer.Visibility = Visibility.Visible;
        _shimmer.Width = Math.Max(48, ActualWidth * 0.6);
        _sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-_shimmer.Width, ActualWidth, SweepDuration)
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        IsSweeping = true;
    }

    private void StopSweep()
    {
        _sweep?.BeginAnimation(TranslateTransform.XProperty, null);
        IsSweeping = false;
    }
}

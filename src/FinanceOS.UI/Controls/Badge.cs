using System.Windows;
using System.Windows.Controls;

namespace FinanceOS.UI.Controls;

public enum BadgeTone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Danger,
    Info,
}

/// <summary>Small, non-interactive label for status, category or counts ("Overdue", "Intercompany", "3").</summary>
public class Badge : ContentControl
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(BadgeTone), typeof(Badge), new FrameworkPropertyMetadata(BadgeTone.Neutral));

    /// <summary>Shows a leading status dot in the tone's strong color.</summary>
    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.Register(
        nameof(ShowDot), typeof(bool), typeof(Badge), new FrameworkPropertyMetadata(false));

    static Badge()
    {
        // Purely informational: never a tab stop.
        FocusableProperty.OverrideMetadata(typeof(Badge), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Badge), new FrameworkPropertyMetadata(false));
    }

    public BadgeTone Tone
    {
        get => (BadgeTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public bool ShowDot
    {
        get => (bool)GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }
}

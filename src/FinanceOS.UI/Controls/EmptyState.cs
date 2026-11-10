using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FinanceOS.UI.Controls;

/// <summary>
/// What a page or list shows when it has nothing to show: icon, title, a sentence that explains why
/// and, as <see cref="ContentControl.Content"/>, the action that fixes it (e.g. "New task").
/// A real empty state, never a dead end.
/// </summary>
public class EmptyState : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(EmptyState), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyState), new FrameworkPropertyMetadata(null));

    static EmptyState()
    {
        FocusableProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

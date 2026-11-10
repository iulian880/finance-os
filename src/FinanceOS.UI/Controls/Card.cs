using System.Windows;
using System.Windows.Controls;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Surface for a group of related content. Optional <see cref="HeaderedContentControl.Header"/>
/// (title) and <see cref="Actions"/> (e.g. a ghost button or an overflow menu) in the top row.
/// </summary>
public class Card : HeaderedContentControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(Card), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(Card), new FrameworkPropertyMetadata(null));

    static Card()
    {
        FocusableProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(false));
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>Secondary line under the header.</summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

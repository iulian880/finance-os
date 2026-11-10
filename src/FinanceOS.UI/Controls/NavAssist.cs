using System.Windows;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Attached properties for sidebar navigation items (ListBoxItem with style <c>Nav.Item</c>):
/// a section label drawn above the item ("Organizare"), and a compact mode that hides labels.
/// The label is decoration: it is not part of the item's accessible name and is not clickable.
/// </summary>
public static class NavAssist
{
    public static readonly DependencyProperty GroupHeaderProperty = DependencyProperty.RegisterAttached(
        "GroupHeader", typeof(string), typeof(NavAssist), new FrameworkPropertyMetadata(null));

    /// <summary>Set on the list: true shows icons only (collapsed sidebar). Inherited by the items.</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(NavAssist), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static string? GetGroupHeader(DependencyObject element) => (string?)element.GetValue(GroupHeaderProperty);

    public static void SetGroupHeader(DependencyObject element, string? value) => element.SetValue(GroupHeaderProperty, value);

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);

    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);
}

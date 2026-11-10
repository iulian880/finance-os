using System.Windows;
using System.Windows.Automation;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Attached properties shared by input controls (TextBox, ComboBox, DatePicker, TimePicker):
/// a label above the field, placeholder, helper text and an error message below it.
/// Header and messages are mirrored into <see cref="AutomationProperties"/> so screen readers get them.
/// </summary>
public static class FieldAssist
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.RegisterAttached(
        "Header", typeof(string), typeof(FieldAssist), new PropertyMetadata(null, OnHeaderChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(FieldAssist), new PropertyMetadata(null));

    public static readonly DependencyProperty HelperTextProperty = DependencyProperty.RegisterAttached(
        "HelperText", typeof(string), typeof(FieldAssist), new PropertyMetadata(null, OnMessageChanged));

    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.RegisterAttached(
        "ErrorText", typeof(string), typeof(FieldAssist), new PropertyMetadata(null, OnMessageChanged));

    private static readonly DependencyPropertyKey HasErrorPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "HasError", typeof(bool), typeof(FieldAssist), new PropertyMetadata(false));

    /// <summary>True while <c>ErrorText</c> is non-empty. Templates switch to the danger state on it.</summary>
    public static readonly DependencyProperty HasErrorProperty = HasErrorPropertyKey.DependencyProperty;

    public static string? GetHeader(DependencyObject element) => (string?)element.GetValue(HeaderProperty);

    public static void SetHeader(DependencyObject element, string? value) => element.SetValue(HeaderProperty, value);

    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string? value) => element.SetValue(PlaceholderProperty, value);

    public static string? GetHelperText(DependencyObject element) => (string?)element.GetValue(HelperTextProperty);

    public static void SetHelperText(DependencyObject element, string? value) => element.SetValue(HelperTextProperty, value);

    public static string? GetErrorText(DependencyObject element) => (string?)element.GetValue(ErrorTextProperty);

    public static void SetErrorText(DependencyObject element, string? value) => element.SetValue(ErrorTextProperty, value);

    public static bool GetHasError(DependencyObject element) => (bool)element.GetValue(HasErrorProperty);

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The visible label doubles as the accessible name, unless the author set a different name.
        var current = AutomationProperties.GetName(d);
        if (string.IsNullOrEmpty(current) || current == (string?)e.OldValue)
        {
            AutomationProperties.SetName(d, (string?)e.NewValue ?? string.Empty);
        }
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var error = GetErrorText(d);
        var hasError = !string.IsNullOrEmpty(error);
        d.SetValue(HasErrorPropertyKey, hasError);
        AutomationProperties.SetHelpText(d, (hasError ? error : GetHelperText(d)) ?? string.Empty);
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FinanceOS.UI.Controls;

/// <summary>How a dialog was closed. <see cref="None"/> = dismissed (Esc, or closed by code).</summary>
public enum DialogOutcome
{
    None,
    Primary,
    Secondary,
}

/// <summary>
/// Modal card shown by <see cref="DialogHost.ShowAsync"/>: title, content, up to two buttons.
/// With <see cref="IsPrimaryDestructive"/> the primary button is red, Enter does not trigger it and
/// focus starts on the secondary ("Cancel") button, so a stray Enter never deletes anything.
/// </summary>
[TemplatePart(Name = PartPrimaryButton, Type = typeof(ButtonBase))]
[TemplatePart(Name = PartSecondaryButton, Type = typeof(ButtonBase))]
public class Dialog : ContentControl
{
    public const string PartPrimaryButton = "PART_PrimaryButton";
    public const string PartSecondaryButton = "PART_SecondaryButton";

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(Dialog), new FrameworkPropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty PrimaryButtonTextProperty = DependencyProperty.Register(
        nameof(PrimaryButtonText), typeof(string), typeof(Dialog), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty SecondaryButtonTextProperty = DependencyProperty.Register(
        nameof(SecondaryButtonText), typeof(string), typeof(Dialog), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsPrimaryDestructiveProperty = DependencyProperty.Register(
        nameof(IsPrimaryDestructive), typeof(bool), typeof(Dialog), new FrameworkPropertyMetadata(false));

    private ButtonBase? _primary;
    private ButtonBase? _secondary;

    static Dialog()
    {
        FocusableProperty.OverrideMetadata(typeof(Dialog), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Dialog), new FrameworkPropertyMetadata(false));
    }

    public Dialog()
    {
        AutomationProperties.SetIsDialog(this, true);
    }

    internal event Action<DialogOutcome>? Completed;

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? PrimaryButtonText
    {
        get => (string?)GetValue(PrimaryButtonTextProperty);
        set => SetValue(PrimaryButtonTextProperty, value);
    }

    public string? SecondaryButtonText
    {
        get => (string?)GetValue(SecondaryButtonTextProperty);
        set => SetValue(SecondaryButtonTextProperty, value);
    }

    public bool IsPrimaryDestructive
    {
        get => (bool)GetValue(IsPrimaryDestructiveProperty);
        set => SetValue(IsPrimaryDestructiveProperty, value);
    }

    internal ButtonBase? PrimaryButton => _primary;

    internal ButtonBase? SecondaryButton => _secondary;

    /// <summary>Closes the dialog with <paramref name="result"/> (e.g. after validating its content).</summary>
    public void Hide(DialogOutcome result = DialogOutcome.None) => Completed?.Invoke(result);

    public override void OnApplyTemplate()
    {
        if (_primary is not null)
        {
            _primary.Click -= OnPrimaryClick;
        }

        if (_secondary is not null)
        {
            _secondary.Click -= OnSecondaryClick;
        }

        base.OnApplyTemplate();
        _primary = GetTemplateChild(PartPrimaryButton) as ButtonBase;
        _secondary = GetTemplateChild(PartSecondaryButton) as ButtonBase;

        if (_primary is not null)
        {
            _primary.Click += OnPrimaryClick;
        }

        if (_secondary is not null)
        {
            _secondary.Click += OnSecondaryClick;
        }
    }

    /// <summary>
    /// Initial focus: the secondary button for destructive dialogs; otherwise the first focusable
    /// element of the content (e.g. a text field), falling back to the primary button.
    /// </summary>
    internal void FocusInitialElement()
    {
        if (IsPrimaryDestructive && _secondary is { IsVisible: true } cancel)
        {
            cancel.Focus();
            return;
        }

        if (Content is UIElement content && content.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First)))
        {
            return;
        }

        if (Content is UIElement { Focusable: true } single && single.Focus())
        {
            return;
        }

        (_primary is { IsVisible: true } ? _primary : _secondary)?.Focus();
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AutomationProperties.SetName(d, (string?)e.NewValue ?? string.Empty);

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Hide(DialogOutcome.Primary);

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => Hide(DialogOutcome.Secondary);
}

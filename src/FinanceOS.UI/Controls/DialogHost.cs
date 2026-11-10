using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Wraps a window's content and shows <see cref="Dialog"/>s above it, in the same window: a scrim
/// blocks the page, Tab cycles inside the dialog, Esc dismisses, and focus returns to where it was.
/// Dialogs are shown one at a time; a second <see cref="ShowAsync"/> waits for the first to close.
/// </summary>
[TemplatePart(Name = PartScrim, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartDialogPresenter, Type = typeof(FrameworkElement))]
public class DialogHost : ContentControl
{
    public const string PartScrim = "PART_Scrim";
    public const string PartDialogPresenter = "PART_DialogPresenter";

    private static readonly DependencyPropertyKey CurrentDialogPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CurrentDialog), typeof(Dialog), typeof(DialogHost), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty CurrentDialogProperty = CurrentDialogPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsDialogOpenPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsDialogOpen), typeof(bool), typeof(DialogHost), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsDialogOpenProperty = IsDialogOpenPropertyKey.DependencyProperty;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private FrameworkElement? _scrim;
    private FrameworkElement? _presenter;

    static DialogHost()
    {
        FocusableProperty.OverrideMetadata(typeof(DialogHost), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(DialogHost), new FrameworkPropertyMetadata(false));
    }

    public DialogHost()
    {
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public Dialog? CurrentDialog => (Dialog?)GetValue(CurrentDialogProperty);

    public bool IsDialogOpen => (bool)GetValue(IsDialogOpenProperty);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scrim = GetTemplateChild(PartScrim) as FrameworkElement;
        _presenter = GetTemplateChild(PartDialogPresenter) as FrameworkElement;
    }

    /// <summary>Shows <paramref name="dialog"/> and completes when it closes.</summary>
    public async Task<DialogOutcome> ShowAsync(Dialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var previousFocus = Keyboard.FocusedElement;
            var completion = new TaskCompletionSource<DialogOutcome>();
            void OnCompleted(DialogOutcome result) => completion.TrySetResult(result);
            dialog.Completed += OnCompleted;

            SetValue(CurrentDialogPropertyKey, dialog);
            SetValue(IsDialogOpenPropertyKey, true);
            if (_scrim is not null)
            {
                Motion.FadeIn(_scrim);
            }

            if (_presenter is not null)
            {
                Motion.Enter(_presenter, offsetY: 0, fromScale: 0.96);
            }

            // After layout, so the dialog's template (and its buttons) exist.
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, dialog.FocusInitialElement);

            DialogOutcome outcome;
            try
            {
                outcome = await completion.Task.ConfigureAwait(true);
            }
            finally
            {
                dialog.Completed -= OnCompleted;
                SetValue(CurrentDialogPropertyKey, null);
                SetValue(IsDialogOpenPropertyKey, false);
            }

            if (previousFocus is UIElement { IsVisible: true } element)
            {
                element.Focus();
            }

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CurrentDialog is { } dialog)
        {
            dialog.Hide(DialogOutcome.None);
            e.Handled = true;
        }
    }
}

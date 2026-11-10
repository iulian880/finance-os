using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FinanceOS.UI.Controls;

/// <summary>One entry in the drop-down list.</summary>
public sealed record TimeOption(TimeSpan Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Time-of-day input: free typing (parsed by <see cref="TimeParser"/> on Enter or focus loss) plus a
/// drop-down list in <see cref="MinuteIncrement"/> steps. Up/Down step the time when the list is closed.
/// Invalid input reverts to the last valid value instead of guessing.
/// </summary>
[TemplatePart(Name = PartTextBox, Type = typeof(TextBox))]
[TemplatePart(Name = PartPopup, Type = typeof(Popup))]
[TemplatePart(Name = PartList, Type = typeof(ListBox))]
public class TimePicker : Control
{
    public const string PartTextBox = "PART_TextBox";
    public const string PartPopup = "PART_Popup";
    public const string PartList = "PART_List";

    private const int MinutesPerDay = 24 * 60;

    public static readonly DependencyProperty SelectedTimeProperty = DependencyProperty.Register(
        nameof(SelectedTime), typeof(TimeSpan?), typeof(TimePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedTimeChanged, CoerceSelectedTime));

    public static readonly DependencyProperty MinuteIncrementProperty = DependencyProperty.Register(
        nameof(MinuteIncrement), typeof(int), typeof(TimePicker), new FrameworkPropertyMetadata(15), IsValidIncrement);

    public static readonly DependencyProperty IsDropDownOpenProperty = DependencyProperty.Register(
        nameof(IsDropDownOpen), typeof(bool), typeof(TimePicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsDropDownOpenChanged));

    private TextBox? _textBox;
    private ListBox? _list;

    static TimePicker()
    {
        // Focus lives in the inner text box; the control itself is not a tab stop.
        FocusableProperty.OverrideMetadata(typeof(TimePicker), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(TimePicker), new FrameworkPropertyMetadata(false));
    }

    public event EventHandler? SelectedTimeChanged;

    /// <summary>Selected time of day, or null when empty. Always within [00:00, 24:00) and whole minutes.</summary>
    public TimeSpan? SelectedTime
    {
        get => (TimeSpan?)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    /// <summary>Step of the drop-down list and of the Up/Down keys, in minutes (1–60).</summary>
    public int MinuteIncrement
    {
        get => (int)GetValue(MinuteIncrementProperty);
        set => SetValue(MinuteIncrementProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => (bool)GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    /// <summary>
    /// Culture for parsing and display. Like WPF's DatePicker: an explicitly set <see cref="FrameworkElement.Language"/>
    /// wins; otherwise the thread culture (WPF's default Language is en-US regardless of Windows settings).
    /// </summary>
    internal CultureInfo Culture =>
        DependencyPropertyHelper.GetValueSource(this, LanguageProperty).BaseValueSource == BaseValueSource.Default
            ? CultureInfo.CurrentCulture
            : Language.GetSpecificCulture();

    internal TextBox? TextBox => _textBox;

    internal ListBox? List => _list;

    public override void OnApplyTemplate()
    {
        if (_textBox is not null)
        {
            _textBox.PreviewKeyDown -= OnTextBoxPreviewKeyDown;
            _textBox.LostKeyboardFocus -= OnTextBoxLostFocus;
        }

        if (_list is not null)
        {
            _list.PreviewMouseLeftButtonUp -= OnListMouseUp;
        }

        base.OnApplyTemplate();

        _textBox = GetTemplateChild(PartTextBox) as TextBox;
        _list = GetTemplateChild(PartList) as ListBox;

        if (_textBox is not null)
        {
            _textBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
            _textBox.LostKeyboardFocus += OnTextBoxLostFocus;
        }

        if (_list is not null)
        {
            _list.PreviewMouseLeftButtonUp += OnListMouseUp;
        }

        UpdateText();
    }

    /// <summary>Parses the typed text. Empty clears the value; invalid text reverts. Returns false when reverted.</summary>
    internal bool CommitText()
    {
        if (_textBox is null)
        {
            return true;
        }

        var text = _textBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SelectedTime = null;
            UpdateText();
            return true;
        }

        if (TimeParser.TryParse(text, Culture, out var time))
        {
            SelectedTime = time;
            UpdateText(); // Normalize "930" to "09:30" even when the value did not change.
            return true;
        }

        UpdateText();
        return false;
    }

    /// <summary>Moves to the next (+1) or previous (-1) multiple of <see cref="MinuteIncrement"/>, wrapping at midnight.</summary>
    internal void Step(int direction)
    {
        var step = MinuteIncrement;
        var current = (int)(SelectedTime ?? RoundDown(DateTime.Now.TimeOfDay, step)).TotalMinutes;
        int next;
        if (direction > 0)
        {
            next = ((current / step) + 1) * step;
        }
        else
        {
            next = current % step == 0 ? current - step : current / step * step;
        }

        next = ((next % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
        SelectedTime = TimeSpan.FromMinutes(next);
    }

    internal IReadOnlyList<TimeOption> BuildOptions()
    {
        var culture = Culture;
        var options = new List<TimeOption>();
        for (var minutes = 0; minutes < MinutesPerDay; minutes += MinuteIncrement)
        {
            var value = TimeSpan.FromMinutes(minutes);
            options.Add(new TimeOption(value, TimeParser.Format(value, culture)));
        }

        return options;
    }

    private static TimeSpan RoundDown(TimeSpan time, int stepMinutes) =>
        TimeSpan.FromMinutes((int)time.TotalMinutes / stepMinutes * stepMinutes);

    private static bool IsValidIncrement(object value) => value is int i and >= 1 and <= 60;

    private static object? CoerceSelectedTime(DependencyObject d, object? value)
    {
        if (value is not TimeSpan time)
        {
            return null;
        }

        // Keep only the time of day, in whole minutes.
        var minutes = ((int)Math.Floor(time.TotalMinutes) % MinutesPerDay + MinutesPerDay) % MinutesPerDay;
        return TimeSpan.FromMinutes(minutes);
    }

    private static void OnSelectedTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (TimePicker)d;
        picker.UpdateText();
        picker.SelectedTimeChanged?.Invoke(picker, EventArgs.Empty);
    }

    private static void OnIsDropDownOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (TimePicker)d;
        if ((bool)e.NewValue)
        {
            picker.PopulateList();
        }
    }

    private void UpdateText()
    {
        if (_textBox is not null)
        {
            _textBox.Text = SelectedTime is { } time ? TimeParser.Format(time, Culture) : string.Empty;
        }
    }

    private void PopulateList()
    {
        if (_list is null)
        {
            return;
        }

        // A value typed but not yet committed should be what the list highlights.
        CommitText();

        var options = BuildOptions();
        _list.ItemsSource = options;

        var target = SelectedTime ?? DateTime.Now.TimeOfDay;
        var index = options.Count - 1;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Value >= target)
            {
                index = i;
                break;
            }
        }

        _list.SelectedIndex = index;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_list.SelectedItem is { } item)
            {
                _list.ScrollIntoView(item);
            }
        });
    }

    private void Choose(TimeOption option)
    {
        SelectedTime = option.Value;
        IsDropDownOpen = false;
        _textBox?.Focus();
        _textBox?.SelectAll();
    }

    private void MoveListSelection(int delta)
    {
        if (_list is null || _list.Items.Count == 0)
        {
            return;
        }

        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if ((alt && key is Key.Down or Key.Up) || key == Key.F4)
        {
            IsDropDownOpen = !IsDropDownOpen;
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.Enter when IsDropDownOpen && _list?.SelectedItem is TimeOption option:
                Choose(option);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitText();
                _textBox?.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when IsDropDownOpen:
                IsDropDownOpen = false;
                e.Handled = true;
                break;
            case Key.Escape:
                UpdateText(); // Discard the uncommitted edit.
                break;
            case Key.Up or Key.Down when IsDropDownOpen:
                MoveListSelection(key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Up or Key.Down:
                if (CommitText())
                {
                    Step(key == Key.Up ? 1 : -1);
                    _textBox?.SelectAll();
                }

                e.Handled = true;
                break;
        }
    }

    private void OnTextBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Focus moving into our own popup (e.g. scrollbar) is not "leaving" the field.
        if (e.NewFocus is DependencyObject target && _list is not null && IsInside(target, _list))
        {
            return;
        }

        CommitText();
        IsDropDownOpen = false;
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem { DataContext: TimeOption option })
        {
            Choose(option);
            e.Handled = true;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }
}

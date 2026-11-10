using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace FinanceOS.App.Shell;

/// <summary>Small converters shared by the app's views.</summary>
public static class Converters
{
    /// <summary>Visible when the value is not null (and not an empty string).</summary>
    public static IValueConverter NullToCollapsed { get; } = new VisibilityConverter(v => v is string s ? s.Length > 0 : v is not null);

    /// <summary>Visible when the value is null (or an empty string): placeholders and empty states.</summary>
    public static IValueConverter NotNullToCollapsed { get; } = new VisibilityConverter(v => v is string s ? s.Length == 0 : v is null);

    /// <summary>Visible when the number is zero.</summary>
    public static IValueConverter ZeroToVisible { get; } = new VisibilityConverter(v => v is 0);

    /// <summary>Visible when the number is not zero.</summary>
    public static IValueConverter ZeroToCollapsed { get; } = new VisibilityConverter(v => v is int i && i != 0);

    /// <summary>Visible when false.</summary>
    public static IValueConverter TrueToCollapsed { get; } = new VisibilityConverter(v => v is not true);

    private sealed class VisibilityConverter(Func<object?, bool> isVisible) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            isVisible(value) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// <c>shell:EnterCommits.IsEnabled="True"</c> on a single-line TextBox whose Text saves when focus
/// leaves: Enter saves right away too (what people expect from an inline field).
/// </summary>
public static class EnterCommits
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(EnterCommits), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is System.Windows.Controls.TextBox box)
        {
            box.KeyDown -= OnKeyDown;
            if ((bool)e.NewValue)
            {
                box.KeyDown += OnKeyDown;
            }
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox box)
        {
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}

/// <summary>
/// <c>shell:ClickOpensMenu.IsEnabled="True"</c> on a Button with a ContextMenu: a click (or Enter / Space)
/// opens the menu under the button, with the button's DataContext (a "Snooze ▾" choice list).
/// </summary>
public static class ClickOpensMenu
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ClickOpensMenu), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is System.Windows.Controls.Button button)
        {
            button.Click -= OnClick;
            if ((bool)e.NewValue)
            {
                button.Click += OnClick;
            }
        }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}

/// <summary>
/// Drag a task (<c>shell:TaskDrag.TaskId</c>) onto a day (<c>shell:TaskDrag.DropCommand</c> + <c>DropDate</c>):
/// the command receives a <see cref="FinanceOS.App.Tasks.MoveRequest"/>; the page confirms before moving.
/// </summary>
public static class TaskDrag
{
    private const string Format = "FinanceOS.TaskId";

    public static readonly DependencyProperty TaskIdProperty = DependencyProperty.RegisterAttached(
        "TaskId", typeof(string), typeof(TaskDrag), new PropertyMetadata(null, OnTaskIdChanged));

    public static readonly DependencyProperty DropCommandProperty = DependencyProperty.RegisterAttached(
        "DropCommand", typeof(ICommand), typeof(TaskDrag), new PropertyMetadata(null, OnDropCommandChanged));

    public static readonly DependencyProperty DropDateProperty = DependencyProperty.RegisterAttached(
        "DropDate", typeof(DateOnly), typeof(TaskDrag), new PropertyMetadata(default(DateOnly)));

    private static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(Point?), typeof(TaskDrag), new PropertyMetadata(null));

    public static string? GetTaskId(DependencyObject d) => (string?)d.GetValue(TaskIdProperty);

    public static void SetTaskId(DependencyObject d, string? value) => d.SetValue(TaskIdProperty, value);

    public static ICommand? GetDropCommand(DependencyObject d) => (ICommand?)d.GetValue(DropCommandProperty);

    public static void SetDropCommand(DependencyObject d, ICommand? value) => d.SetValue(DropCommandProperty, value);

    public static DateOnly GetDropDate(DependencyObject d) => (DateOnly)d.GetValue(DropDateProperty);

    public static void SetDropDate(DependencyObject d, DateOnly value) => d.SetValue(DropDateProperty, value);

    private static void OnTaskIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && e.OldValue is null && e.NewValue is not null)
        {
            element.PreviewMouseLeftButtonDown += (_, args) => element.SetValue(StartProperty, args.GetPosition(element));
            element.PreviewMouseLeftButtonUp += (_, _) => element.SetValue(StartProperty, null);
            element.PreviewMouseMove += (_, args) =>
            {
                if (args.LeftButton != MouseButtonState.Pressed || element.GetValue(StartProperty) is not Point start)
                {
                    return;
                }

                var delta = args.GetPosition(element) - start;
                if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    element.SetValue(StartProperty, null);
                    DragDrop.DoDragDrop(element, new DataObject(Format, GetTaskId(element)!), DragDropEffects.Move);
                }
            };
        }
    }

    private static void OnDropCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && e.OldValue is null && e.NewValue is not null)
        {
            element.AllowDrop = true;
            element.DragOver += (_, args) =>
            {
                args.Effects = args.Data.GetDataPresent(Format) ? DragDropEffects.Move : DragDropEffects.None;
                args.Handled = true;
            };
            element.Drop += (_, args) =>
            {
                if (args.Data.GetData(Format) is string taskId && GetDropCommand(element) is { } command)
                {
                    var request = new FinanceOS.App.Tasks.MoveRequest(taskId, GetDropDate(element));
                    if (command.CanExecute(request))
                    {
                        command.Execute(request);
                    }
                }

                args.Handled = true;
            };
        }
    }
}

/// <summary>
/// <c>shell:FocusRequest.Token="{Binding QuickAddFocusToken}"</c>: every time the bound number changes,
/// the element takes keyboard focus. Lets a ViewModel ask for focus (Ctrl+N) without knowing the view.
/// </summary>
public static class FocusRequest
{
    public static readonly DependencyProperty TokenProperty = DependencyProperty.RegisterAttached(
        "Token", typeof(int), typeof(FocusRequest), new PropertyMetadata(0, OnTokenChanged));

    public static int GetToken(DependencyObject element) => (int)element.GetValue(TokenProperty);

    public static void SetToken(DependencyObject element, int value) => element.SetValue(TokenProperty, value);

    private static void OnTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && (int)e.NewValue != 0)
        {
            // After layout: the page may have just been switched in and not be loaded yet.
            element.Dispatcher.BeginInvoke(() =>
            {
                element.Focus();
                Keyboard.Focus(element);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}

using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FinanceOS.UI.Controls;

/// <summary>One entry in the command palette.</summary>
public sealed class CommandItem
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>Section label shown at the right, e.g. "Navigare", "Task".</summary>
    public string? Group { get; init; }

    /// <summary>Extra search terms not shown in the UI (synonyms, English names).</summary>
    public string? Keywords { get; init; }

    public Geometry? Icon { get; init; }

    /// <summary>Shortcut hint, display only (e.g. "Ctrl+N").</summary>
    public string? Shortcut { get; init; }

    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    public override string ToString() => Title;
}

/// <summary>
/// Ranks commands for a query. Case- and diacritics-insensitive ("sedinta" finds "Ședință"), every word
/// of the query must match. Title prefix beats word start beats substring beats a match only in
/// subtitle/keywords/group; ties keep the original order.
/// </summary>
public static class CommandFilter
{
    public static IReadOnlyList<CommandItem> Filter(IEnumerable<CommandItem> items, string? query, int max = 50)
    {
        ArgumentNullException.ThrowIfNull(items);
        var tokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return items.Take(max).ToList();
        }

        return items
            .Select((item, index) => (item, index, score: Score(item, tokens)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(max)
            .Select(x => x.item)
            .ToList();
    }

    /// <summary>Lower-case, without diacritics (ș/ş, ț/ţ, ă, â, î all reduce to their base letter).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static int Score(CommandItem item, string[] tokens)
    {
        var title = Normalize(item.Title);
        var extra = Normalize($"{item.Subtitle} {item.Keywords} {item.Group}");
        var total = 0;
        foreach (var token in tokens)
        {
            int score;
            if (title.StartsWith(token, StringComparison.Ordinal))
            {
                score = 100;
            }
            else if (title.Contains(" " + token, StringComparison.Ordinal))
            {
                score = 60;
            }
            else if (title.Contains(token, StringComparison.Ordinal))
            {
                score = 30;
            }
            else if (extra.Contains(token, StringComparison.Ordinal))
            {
                score = 10;
            }
            else
            {
                return 0;
            }

            total += score;
        }

        return total;
    }
}

/// <summary>
/// Ctrl+K overlay: search box plus ranked results. ↑/↓ move, Enter runs, Esc or a click outside closes.
/// Running an item closes the palette, then executes its <see cref="CommandItem.Command"/> and raises
/// <see cref="Executed"/>. Focus returns to where it was. Place it over the page in the window's root grid.
/// </summary>
[TemplatePart(Name = PartSearch, Type = typeof(TextBox))]
[TemplatePart(Name = PartResults, Type = typeof(ListBox))]
[TemplatePart(Name = PartScrim, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartPanel, Type = typeof(FrameworkElement))]
public class CommandPalette : Control
{
    public const string PartSearch = "PART_Search";
    public const string PartResults = "PART_Results";
    public const string PartScrim = "PART_Scrim";
    public const string PartPanel = "PART_Panel";

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<CommandItem>), typeof(CommandPalette), new FrameworkPropertyMetadata(null, (d, _) => ((CommandPalette)d).Refresh()));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(CommandPalette),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(string), typeof(CommandPalette),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((CommandPalette)d).Refresh()));

    private static readonly DependencyPropertyKey ResultsPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Results), typeof(IReadOnlyList<CommandItem>), typeof(CommandPalette), new FrameworkPropertyMetadata(Array.Empty<CommandItem>()));

    public static readonly DependencyProperty ResultsProperty = ResultsPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey HasResultsPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasResults), typeof(bool), typeof(CommandPalette), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty HasResultsProperty = HasResultsPropertyKey.DependencyProperty;

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(CommandPalette), new FrameworkPropertyMetadata("Type a command or search…"));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(CommandPalette), new FrameworkPropertyMetadata("No results"));

    private TextBox? _search;
    private ListBox? _results;
    private FrameworkElement? _scrim;
    private FrameworkElement? _panel;
    private IInputElement? _previousFocus;

    static CommandPalette()
    {
        FocusableProperty.OverrideMetadata(typeof(CommandPalette), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(CommandPalette), new FrameworkPropertyMetadata(false));
        VisibilityProperty.OverrideMetadata(typeof(CommandPalette), new FrameworkPropertyMetadata(Visibility.Collapsed));
    }

    public event EventHandler<CommandItem>? Executed;

    public IEnumerable<CommandItem>? ItemsSource
    {
        get => (IEnumerable<CommandItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public IReadOnlyList<CommandItem> Results => (IReadOnlyList<CommandItem>)GetValue(ResultsProperty);

    public bool HasResults => (bool)GetValue(HasResultsProperty);

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    internal ListBox? ResultList => _results;

    public override void OnApplyTemplate()
    {
        if (_search is not null)
        {
            _search.PreviewKeyDown -= OnSearchKeyDown;
        }

        if (_results is not null)
        {
            _results.PreviewMouseLeftButtonUp -= OnResultClicked;
        }

        if (_scrim is not null)
        {
            _scrim.MouseLeftButtonDown -= OnScrimClicked;
        }

        base.OnApplyTemplate();
        _search = GetTemplateChild(PartSearch) as TextBox;
        _results = GetTemplateChild(PartResults) as ListBox;
        _scrim = GetTemplateChild(PartScrim) as FrameworkElement;
        _panel = GetTemplateChild(PartPanel) as FrameworkElement;

        if (_search is not null)
        {
            _search.PreviewKeyDown += OnSearchKeyDown;
        }

        if (_results is not null)
        {
            _results.PreviewMouseLeftButtonUp += OnResultClicked;
        }

        if (_scrim is not null)
        {
            _scrim.MouseLeftButtonDown += OnScrimClicked;
        }

        Refresh();
    }

    /// <summary>Runs the highlighted result (Enter). Returns false when there is nothing to run.</summary>
    internal bool ExecuteSelected() =>
        _results?.SelectedItem is CommandItem item ? Execute(item) : Results.Count > 0 && Execute(Results[0]);

    internal void MoveSelection(int delta)
    {
        if (_results is null || Results.Count == 0)
        {
            return;
        }

        // Wraps around, like most launchers.
        var count = Results.Count;
        _results.SelectedIndex = ((_results.SelectedIndex + delta) % count + count) % count;
        _results.ScrollIntoView(_results.SelectedItem);
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var palette = (CommandPalette)d;
        if ((bool)e.NewValue)
        {
            palette.Opened();
        }
        else
        {
            palette.Closed();
        }
    }

    private void Opened()
    {
        _previousFocus = Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        ApplyTemplate(); // A collapsed control has not built its template yet.
        Query = string.Empty;
        Refresh();
        if (_scrim is not null)
        {
            Motion.FadeIn(_scrim);
        }

        if (_panel is not null)
        {
            Motion.Enter(_panel, offsetY: -8, fromScale: 0.98);
        }

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => _search?.Focus());
    }

    private void Closed()
    {
        Visibility = Visibility.Collapsed;
        if (_previousFocus is UIElement { IsVisible: true } element)
        {
            element.Focus();
        }

        _previousFocus = null;
    }

    private void Refresh()
    {
        var results = CommandFilter.Filter(ItemsSource ?? [], Query);
        SetValue(ResultsPropertyKey, results);
        SetValue(HasResultsPropertyKey, results.Count > 0);
        if (_results is not null)
        {
            _results.ItemsSource = results;
            _results.SelectedIndex = results.Count > 0 ? 0 : -1;
            if (_results.SelectedItem is { } first)
            {
                _results.ScrollIntoView(first);
            }
        }
    }

    private bool Execute(CommandItem item)
    {
        IsOpen = false;
        if (item.Command?.CanExecute(item.CommandParameter) == true)
        {
            item.Command.Execute(item.CommandParameter);
        }

        Executed?.Invoke(this, item);
        return true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(_results, source) is ListBoxItem { DataContext: CommandItem item })
        {
            Execute(item);
            e.Handled = true;
        }
    }

    private void OnScrimClicked(object sender, MouseButtonEventArgs e)
    {
        IsOpen = false;
        e.Handled = true;
    }
}

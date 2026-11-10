using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FinanceOS.UI.Theming;

namespace FinanceOS.App.Dev;

/// <summary>Developer page that renders every design token live (M1 deliverable).</summary>
public partial class DesignTokensWindow : Window
{
    private readonly List<(string Name, TextBlock Hex, Func<Color> Read)> _hexLabels = [];

    public DesignTokensWindow()
    {
        InitializeComponent();
        WindowTheming.Track(this);

        BuildThemeChips();
        BuildAccentDots();
        BuildSwatches(PaletteSwatches, ThemeResources.Enumerate(ThemeManager.Current.Palette).Select(t => t.Name),
            name => ThemeResources.Enumerate(ThemeManager.Current.Palette).First(t => t.Name == name).Color);
        BuildSwatches(AccentSwatches, ThemeResources.Enumerate(ThemeManager.Current.AccentColors).Select(t => t.Name),
            name => ThemeResources.Enumerate(ThemeManager.Current.AccentColors).First(t => t.Name == name).Color);
        BuildSpacing();
        BuildShapes();

        ThemeManager.Current.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Current.Changed -= OnThemeChanged;
        Refresh();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var tm = ThemeManager.Current;
        foreach (var (_, hex, read) in _hexLabels)
        {
            var color = read();
            hex.Text = ColorMath.ToHex(color);
        }

        BuildThemeChips();
        BuildAccentDots();
        StateText.Text = $"Activ: {(tm.IsDark ? "întunecat" : "luminos")} (preferință: {tm.Preference}) · accent {tm.Accent.Name} · " +
                         $"animații Windows: {(ThemeManager.ReducedMotion ? "oprite → reduced motion" : "pornite")}";
    }

    private void BuildThemeChips()
    {
        ThemeChips.Children.Clear();
        (ThemePreference Value, string Label)[] options = [(ThemePreference.System, "Sistem"), (ThemePreference.Light, "Luminos"), (ThemePreference.Dark, "Întunecat")];
        foreach (var (value, label) in options)
        {
            var selected = ThemeManager.Current.Preference == value;
            var text = new TextBlock { Text = label, Style = (Style)FindResource("Text.BodyStrong") };
            text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Brush.AccentText" : "Brush.TextSecondary");
            var chip = new Border
            {
                Child = text,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = (CornerRadius)FindResource("Radius.Full"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            chip.SetResourceReference(Border.BackgroundProperty, selected ? "Brush.AccentSubtle" : "Brush.Surface");
            chip.SetResourceReference(Border.BorderBrushProperty, selected ? "Brush.AccentSubtle" : "Brush.BorderStrong");
            chip.MouseLeftButtonUp += (_, _) => ThemeManager.Current.SetPreference(value);
            AutomationProperties.SetAutomationId(chip, $"Theme.{value}");
            ThemeChips.Children.Add(chip);
        }
    }

    private void BuildAccentDots()
    {
        AccentDots.Children.Clear();
        foreach (var accent in Accents.All)
        {
            var selected = ThemeManager.Current.Accent == accent;
            var resolved = AccentColors.Derive(accent, ThemeManager.Current.Palette).Accent;
            var dot = new Ellipse
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 10, 0),
                Fill = new SolidColorBrush(resolved),
                StrokeThickness = selected ? 3 : 0,
                Cursor = Cursors.Hand,
                ToolTip = accent.Name,
            };
            dot.SetResourceReference(Shape.StrokeProperty, "Brush.TextPrimary");
            dot.MouseLeftButtonUp += (_, _) => ThemeManager.Current.SetAccent(accent);
            AutomationProperties.SetAutomationId(dot, $"Accent.{accent.Id}");
            AccentDots.Children.Add(dot);
        }
    }

    private void BuildSwatches(Panel target, IEnumerable<string> names, Func<string, Color> read)
    {
        foreach (var name in names)
        {
            var sample = new Border { Height = 52, CornerRadius = new CornerRadius(9, 9, 0, 0), BorderThickness = new Thickness(0, 0, 0, 1) };
            sample.SetResourceReference(Border.BackgroundProperty, $"Brush.{name}");
            sample.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

            var label = new TextBlock { Text = name, Style = (Style)FindResource("Text.Small"), FontWeight = FontWeights.Medium };
            var hex = new TextBlock { Style = (Style)FindResource("Text.Caption") };
            System.Windows.Documents.Typography.SetNumeralAlignment(hex, FontNumeralAlignment.Tabular);
            _hexLabels.Add((name, hex, () => read(name)));

            var card = new Border
            {
                Width = 164,
                Margin = new Thickness(0, 0, 12, 12),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Children =
                    {
                        sample,
                        new StackPanel { Margin = new Thickness(12, 10, 12, 12), Children = { label, hex } },
                    },
                },
            };
            card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            target.Children.Add(card);
        }
    }

    private void BuildSpacing()
    {
        foreach (var step in new[] { 4, 8, 12, 16, 20, 24, 32, 40, 48 })
        {
            var bar = new Border { Width = step * 4, Height = 10, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 12, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSubtle");
            bar.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentText");
            bar.BorderThickness = new Thickness(1);
            var label = new TextBlock { Text = $"Space.{step}  ·  {step} px", Style = (Style)FindResource("Text.Caption"), Width = 140 };
            SpacingRows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
                Children = { label, bar },
            });
        }
    }

    private void BuildShapes()
    {
        (string Radius, string Elevation, string Label)[] samples =
        [
            ("Radius.Small", "", "Radius.Small 6 · fără umbră"),
            ("Radius.Medium", "Effect.Elevation1", "Radius.Medium 8 · Elevation1 (carduri)"),
            ("Radius.Large", "Effect.Elevation2", "Radius.Large 12 · Elevation2 (popover, meniu)"),
            ("Radius.XLarge", "Effect.Elevation3", "Radius.XLarge 16 · Elevation3 (dialog)"),
        ];
        foreach (var (radius, elevation, text) in samples)
        {
            var label = new TextBlock { Text = text, Style = (Style)FindResource("Text.Small"), TextWrapping = TextWrapping.Wrap };
            var shape = new Border
            {
                Width = 200,
                Height = 120,
                Margin = new Thickness(0, 0, 28, 28),
                Padding = new Thickness(16),
                BorderThickness = new Thickness(1),
                CornerRadius = (CornerRadius)FindResource(radius),
                Child = label,
            };
            shape.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceRaised");
            shape.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            if (elevation.Length > 0)
            {
                shape.SetResourceReference(EffectProperty, elevation);
            }

            ShapeSamples.Children.Add(shape);
        }
    }
}

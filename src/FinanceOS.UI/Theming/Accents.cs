using System.Windows.Media;
using static FinanceOS.UI.Theming.ColorMath;

namespace FinanceOS.UI.Theming;

/// <summary>A user-selectable accent. <see cref="Id"/> is what gets persisted in settings.</summary>
public sealed record AccentOption(string Id, string Name, Color Base);

public static class Accents
{
    public static IReadOnlyList<AccentOption> All { get; } =
    [
        new("indigo", "Indigo", FromHex("#4F46E5")),
        new("blue", "Blue", FromHex("#2563EB")),
        new("teal", "Teal", FromHex("#0E7C7B")),
        new("emerald", "Emerald", FromHex("#15803D")),
        new("violet", "Violet", FromHex("#7C3AED")),
        new("rose", "Rose", FromHex("#C42D5A")),
        new("amber", "Amber", FromHex("#B45309")),
        new("graphite", "Graphite", FromHex("#3F4654")),
    ];

    public static AccentOption Default => All[0];

    /// <summary>Unknown ids (e.g. from an older settings file) fall back to the default accent.</summary>
    public static AccentOption Find(string? id) => All.FirstOrDefault(a => a.Id == id) ?? Default;
}

/// <summary>
/// Accent colors resolved for one theme. Derived so that WCAG contrast holds for every accent:
/// text on the fill ≥ 4.5:1, the fill against surfaces ≥ 3:1, accent text on surfaces ≥ 4.5:1.
/// </summary>
public sealed record AccentColors
{
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentPressed { get; init; }
    public required Color OnAccent { get; init; }
    public required Color AccentText { get; init; }
    public required Color AccentSubtle { get; init; }
    public required Color FocusRing { get; init; }

    private const double TextContrast = 4.5;
    private const double UiContrast = 3.0;
    private static readonly Color White = Colors.White;
    private static readonly Color Black = Colors.Black;

    public static AccentColors Derive(AccentOption accent, ThemePalette palette)
    {
        var darkText = palette.IsDark ? palette.Background : palette.TextPrimary;

        // Fill: start from the base color and move toward the theme's contrast direction until it
        // stands out from the surface (3:1) and some text color reaches 4.5:1 on it.
        var away = palette.IsDark ? White : Black;
        Color fill = accent.Base;
        Color onFill = BestText(fill, darkText);
        for (var t = 0.0; t <= 1.0; t += 0.02)
        {
            fill = Mix(accent.Base, away, t);
            onFill = BestText(fill, darkText);
            if (Contrast(fill, palette.Surface) >= UiContrast && Contrast(fill, onFill) >= TextContrast)
            {
                break;
            }
        }

        // Hover/pressed move away from the text color, so text contrast only improves.
        var shift = onFill == White ? Black : White;
        var hover = Mix(fill, shift, 0.10);
        var pressed = Mix(fill, shift, 0.18);

        var subtle = Mix(accent.Base, palette.Surface, palette.IsDark ? 0.80 : 0.88);

        Color[] textBackgrounds = [.. palette.TextSurfaces, subtle];
        var text = EnsureContrast(accent.Base, away, textBackgrounds, TextContrast);
        var focus = EnsureContrast(text, away, [palette.Background, palette.Surface, palette.SurfaceRaised], UiContrast);

        return new AccentColors
        {
            Accent = fill,
            AccentHover = hover,
            AccentPressed = pressed,
            OnAccent = onFill,
            AccentText = text,
            AccentSubtle = subtle,
            FocusRing = focus,
        };
    }

    private static Color BestText(Color fill, Color darkText) =>
        Contrast(fill, White) >= Contrast(fill, darkText) ? White : darkText;

    private static Color EnsureContrast(Color start, Color toward, IReadOnlyList<Color> backgrounds, double minimum)
    {
        for (var t = 0.0; t <= 1.0; t += 0.02)
        {
            var candidate = Mix(start, toward, t);
            if (backgrounds.All(b => Contrast(candidate, b) >= minimum))
            {
                return candidate;
            }
        }

        return toward;
    }
}

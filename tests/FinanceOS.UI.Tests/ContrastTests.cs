using System.Windows.Media;
using FinanceOS.UI.Theming;
using Xunit;
using static FinanceOS.UI.Theming.ColorMath;

namespace FinanceOS.UI.Tests;

/// <summary>WCAG 2.2 contrast guarantees for both themes and every accent.</summary>
public sealed class ContrastTests
{
    private const double AaText = 4.5;
    private const double AaaText = 7.0;
    private const double NonText = 3.0;

    public static TheoryData<string> Themes => ["light", "dark"];

    public static TheoryData<string, string> AccentsByTheme
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var accent in Accents.All)
            {
                data.Add(accent.Id, "light");
                data.Add(accent.Id, "dark");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Primary_text_reaches_AAA_on_every_surface(string theme)
    {
        var p = Palette(theme);
        foreach (var surface in p.TextSurfaces)
        {
            AssertContrast(p.TextPrimary, surface, AaaText, "TextPrimary");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Secondary_text_reaches_AA_on_every_surface(string theme)
    {
        var p = Palette(theme);
        foreach (var surface in p.TextSurfaces)
        {
            AssertContrast(p.TextSecondary, surface, AaText, "TextSecondary");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Tertiary_text_and_control_strokes_reach_non_text_contrast(string theme)
    {
        var p = Palette(theme);
        foreach (var surface in new[] { p.Background, p.Surface, p.SurfaceRaised })
        {
            AssertContrast(p.TextTertiary, surface, NonText, "TextTertiary");
            AssertContrast(p.ControlStrongStroke, surface, NonText, "ControlStrongStroke");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Semantic_text_is_readable_on_its_subtle_background_and_on_surfaces(string theme)
    {
        var p = Palette(theme);
        (string Name, Color Text, Color Subtle)[] semantic =
        [
            ("Success", p.SuccessText, p.SuccessSubtle),
            ("Warning", p.WarningText, p.WarningSubtle),
            ("Danger", p.DangerText, p.DangerSubtle),
            ("Info", p.InfoText, p.InfoSubtle),
        ];

        foreach (var (name, text, subtle) in semantic)
        {
            AssertContrast(text, subtle, AaText, $"{name}Text on {name}Subtle");
            AssertContrast(text, p.Surface, AaText, $"{name}Text on Surface");
            AssertContrast(text, p.Background, AaText, $"{name}Text on Background");
        }
    }

    [Theory]
    [MemberData(nameof(AccentsByTheme))]
    public void Text_on_accent_fill_is_readable_in_every_state(string accentId, string theme)
    {
        var a = AccentColors.Derive(Accents.Find(accentId), Palette(theme));
        AssertContrast(a.OnAccent, a.Accent, AaText, $"{accentId} OnAccent on Accent");
        AssertContrast(a.OnAccent, a.AccentHover, AaText, $"{accentId} OnAccent on AccentHover");
        AssertContrast(a.OnAccent, a.AccentPressed, AaText, $"{accentId} OnAccent on AccentPressed");
    }

    [Theory]
    [MemberData(nameof(AccentsByTheme))]
    public void Accent_fill_stands_out_from_the_surface(string accentId, string theme)
    {
        var p = Palette(theme);
        var a = AccentColors.Derive(Accents.Find(accentId), p);
        AssertContrast(a.Accent, p.Surface, NonText, $"{accentId} Accent on Surface");
    }

    [Theory]
    [MemberData(nameof(AccentsByTheme))]
    public void Accent_text_and_focus_ring_are_visible(string accentId, string theme)
    {
        var p = Palette(theme);
        var a = AccentColors.Derive(Accents.Find(accentId), p);
        foreach (var surface in p.TextSurfaces.Append(a.AccentSubtle))
        {
            AssertContrast(a.AccentText, surface, AaText, $"{accentId} AccentText");
        }

        foreach (var surface in new[] { p.Background, p.Surface, p.SurfaceRaised })
        {
            AssertContrast(a.FocusRing, surface, NonText, $"{accentId} FocusRing");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Danger_button_text_is_readable_in_every_state_and_the_fill_stands_out(string theme)
    {
        var p = Palette(theme);
        var d = DangerFill.Derive(p);
        AssertContrast(d.OnDanger, d.DangerFillRest, AaText, "OnDanger on DangerFillRest");
        AssertContrast(d.OnDanger, d.DangerFillHover, AaText, "OnDanger on DangerFillHover");
        AssertContrast(d.OnDanger, d.DangerFillPressed, AaText, "OnDanger on DangerFillPressed");
        AssertContrast(d.DangerFillRest, p.Surface, NonText, "DangerFillRest on Surface");
    }

    [Theory]
    [MemberData(nameof(AccentsByTheme))]
    public void Text_on_selected_rows_is_readable(string accentId, string theme)
    {
        // Command palette / time list: the selected row uses AccentSubtle behind primary and secondary text.
        var p = Palette(theme);
        var a = AccentColors.Derive(Accents.Find(accentId), p);
        AssertContrast(p.TextPrimary, a.AccentSubtle, AaText, $"{accentId} TextPrimary on AccentSubtle");
        AssertContrast(p.TextSecondary, a.AccentSubtle, AaText, $"{accentId} TextSecondary on AccentSubtle");
    }

    private static ThemePalette Palette(string theme) => theme == "dark" ? ThemePalette.Dark : ThemePalette.Light;

    private static void AssertContrast(Color foreground, Color background, double minimum, string what)
    {
        var ratio = Contrast(foreground, background);
        Assert.True(ratio >= minimum,
            $"{what}: {ToHex(foreground)} on {ToHex(background)} = {ratio:F2}:1, needs {minimum}:1");
    }
}

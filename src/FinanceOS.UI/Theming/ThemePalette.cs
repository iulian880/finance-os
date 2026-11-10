using System.Windows.Media;
using static FinanceOS.UI.Theming.ColorMath;

namespace FinanceOS.UI.Theming;

/// <summary>
/// Neutral and semantic colors of one theme. Every <see cref="Color"/> property is published as
/// <c>Color.{Name}</c> and <c>Brush.{Name}</c> resources. Contrast rules are enforced by tests.
/// </summary>
public sealed record ThemePalette
{
    public required bool IsDark { get; init; }

    // Surfaces, from the window canvas up to raised layers.
    public required Color Background { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceRaised { get; init; }
    public required Color SurfaceSunken { get; init; }
    public required Color SurfaceHover { get; init; }
    public required Color SurfacePressed { get; init; }

    // Lines. ControlStrongStroke marks input boundaries and must reach 3:1 against surfaces (WCAG 1.4.11).
    public required Color Border { get; init; }
    public required Color BorderStrong { get; init; }
    public required Color ControlStrongStroke { get; init; }

    // Text. Tertiary is for placeholders, disabled text and decoration only.
    public required Color TextPrimary { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextTertiary { get; init; }

    /// <summary>Scrim behind modal dialogs (semi-transparent).</summary>
    public required Color Overlay { get; init; }

    public required Color Success { get; init; }
    public required Color SuccessSubtle { get; init; }
    public required Color SuccessText { get; init; }
    public required Color Warning { get; init; }
    public required Color WarningSubtle { get; init; }
    public required Color WarningText { get; init; }
    public required Color Danger { get; init; }
    public required Color DangerSubtle { get; init; }
    public required Color DangerText { get; init; }
    public required Color Info { get; init; }
    public required Color InfoSubtle { get; init; }
    public required Color InfoText { get; init; }

    public required Color Shadow { get; init; }

    // Loading placeholders: a block color distinct from surfaces, and the lighter sweep across it.
    public required Color Skeleton { get; init; }
    public required Color SkeletonHighlight { get; init; }

    // Translucent fills for items that sit on Mica or on the solid fallback (sidebar, title bar buttons).
    public required Color SubtleHover { get; init; }
    public required Color SubtleSelected { get; init; }

    /// <summary>Multiplier for shadow opacity; dark themes rely more on borders than on shadows.</summary>
    public double ShadowStrength { get; init; } = 1.0;

    public static ThemePalette Light { get; } = new()
    {
        IsDark = false,
        Background = FromHex("#F5F6F8"),
        Surface = FromHex("#FFFFFF"),
        SurfaceRaised = FromHex("#FFFFFF"),
        SurfaceSunken = FromHex("#EDEFF2"),
        SurfaceHover = FromHex("#F0F2F5"),
        SurfacePressed = FromHex("#E6E9ED"),
        Border = FromHex("#E2E5EA"),
        BorderStrong = FromHex("#CDD2D9"),
        ControlStrongStroke = FromHex("#858C99"),
        TextPrimary = FromHex("#15171C"),
        TextSecondary = FromHex("#575E6B"),
        TextTertiary = FromHex("#868D9B"),
        Overlay = FromHex("#66101318"),
        Success = FromHex("#16855A"),
        SuccessSubtle = FromHex("#E6F4EC"),
        SuccessText = FromHex("#116645"),
        Warning = FromHex("#B26A00"),
        WarningSubtle = FromHex("#FCF1DF"),
        WarningText = FromHex("#8A5200"),
        Danger = FromHex("#CF3B3B"),
        DangerSubtle = FromHex("#FCE9E9"),
        DangerText = FromHex("#A82C2C"),
        Info = FromHex("#2563EB"),
        InfoSubtle = FromHex("#E7EEFD"),
        InfoText = FromHex("#1D4FBF"),
        Shadow = FromHex("#0B1220"),
        ShadowStrength = 1.0,
        Skeleton = FromHex("#E8EBEF"),
        SkeletonHighlight = FromHex("#F6F7F9"),
        SubtleHover = FromHex("#0B000000"),
        SubtleSelected = FromHex("#D9FFFFFF"),
    };

    public static ThemePalette Dark { get; } = new()
    {
        IsDark = true,
        Background = FromHex("#0E1014"),
        Surface = FromHex("#16191E"),
        SurfaceRaised = FromHex("#1C2027"),
        SurfaceSunken = FromHex("#0B0D10"),
        SurfaceHover = FromHex("#22262E"),
        SurfacePressed = FromHex("#2A2F38"),
        Border = FromHex("#262B33"),
        BorderStrong = FromHex("#363C47"),
        ControlStrongStroke = FromHex("#6C7381"),
        TextPrimary = FromHex("#ECEEF2"),
        TextSecondary = FromHex("#A1A8B5"),
        TextTertiary = FromHex("#737A88"),
        Overlay = FromHex("#99000000"),
        Success = FromHex("#3DB07A"),
        SuccessSubtle = FromHex("#12261D"),
        SuccessText = FromHex("#5CC793"),
        Warning = FromHex("#E3A33B"),
        WarningSubtle = FromHex("#2A2112"),
        WarningText = FromHex("#EDB65A"),
        Danger = FromHex("#EF6363"),
        DangerSubtle = FromHex("#2D1617"),
        DangerText = FromHex("#F58A8A"),
        Info = FromHex("#5B8EF0"),
        InfoSubtle = FromHex("#142039"),
        InfoText = FromHex("#84A9F5"),
        Shadow = FromHex("#000000"),
        ShadowStrength = 1.6,
        Skeleton = FromHex("#22262E"),
        SkeletonHighlight = FromHex("#2D323C"),
        SubtleHover = FromHex("#0FFFFFFF"),
        SubtleSelected = FromHex("#1AFFFFFF"),
    };

    /// <summary>All opaque surfaces text may be placed on.</summary>
    public IReadOnlyList<Color> TextSurfaces => [Background, Surface, SurfaceRaised, SurfaceSunken, SurfaceHover];
}

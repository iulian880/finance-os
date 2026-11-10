using System.Windows.Media;

namespace FinanceOS.UI.Theming;

/// <summary>
/// Solid fill for destructive buttons. Uses the same derivation as the accent, so text on the fill
/// reaches 4.5:1 in every state and the fill reaches 3:1 against surfaces, in both themes.
/// Published as <c>Brush.DangerFillRest</c>, <c>Brush.DangerFillHover</c>, <c>Brush.DangerFillPressed</c>, <c>Brush.OnDanger</c>.
/// </summary>
public sealed record DangerFill
{
    // Property names become resource keys (Brush.{Name}); "DangerFill" itself would clash with the type name.
    public required Color DangerFillRest { get; init; }
    public required Color DangerFillHover { get; init; }
    public required Color DangerFillPressed { get; init; }
    public required Color OnDanger { get; init; }

    public static DangerFill Derive(ThemePalette palette)
    {
        var derived = AccentColors.Derive(new AccentOption("danger", "Danger", palette.Danger), palette);
        return new DangerFill
        {
            DangerFillRest = derived.Accent,
            DangerFillHover = derived.AccentHover,
            DangerFillPressed = derived.AccentPressed,
            OnDanger = derived.OnAccent,
        };
    }
}

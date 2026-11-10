using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FinanceOS.UI.Theming;

/// <summary>
/// Builds the resource dictionary for one theme + accent. Keys:
/// <c>Color.{Name}</c>, <c>Brush.{Name}</c> for every color, and <c>Effect.Elevation1..3</c>.
/// Everything is frozen: cheaper to render and safe to share.
/// </summary>
public static class ThemeResources
{
    public static ResourceDictionary Build(ThemePalette palette, AccentColors accent)
    {
        var dictionary = new ResourceDictionary();
        AddColors(dictionary, palette);
        AddColors(dictionary, accent);
        AddColors(dictionary, DangerFill.Derive(palette));

        dictionary["Effect.Elevation1"] = Shadow(palette, blur: 8, depth: 1, opacity: 0.06);
        dictionary["Effect.Elevation2"] = Shadow(palette, blur: 24, depth: 6, opacity: 0.12);
        dictionary["Effect.Elevation3"] = Shadow(palette, blur: 40, depth: 12, opacity: 0.18);
        return dictionary;
    }

    /// <summary>Every color token as (name, color), in declaration order. Used by the token gallery and tests.</summary>
    public static IEnumerable<(string Name, Color Color)> Enumerate(object source) =>
        source.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(Color))
            .OrderBy(p => p.MetadataToken)
            .Select(p => (p.Name, (Color)p.GetValue(source)!));

    private static void AddColors(ResourceDictionary dictionary, object source)
    {
        foreach (var (name, color) in Enumerate(source))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary[$"Color.{name}"] = color;
            dictionary[$"Brush.{name}"] = brush;
        }
    }

    private static DropShadowEffect Shadow(ThemePalette palette, double blur, double depth, double opacity)
    {
        var effect = new DropShadowEffect
        {
            Color = palette.Shadow,
            BlurRadius = blur,
            ShadowDepth = depth,
            Direction = 270,
            Opacity = Math.Min(1, opacity * palette.ShadowStrength),
            RenderingBias = RenderingBias.Performance,
        };
        effect.Freeze();
        return effect;
    }
}

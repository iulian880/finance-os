using System.Globalization;
using System.Windows.Media;

namespace FinanceOS.UI.Theming;

/// <summary>sRGB color helpers, including WCAG 2.x relative luminance and contrast ratio.</summary>
public static class ColorMath
{
    public static Color FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        return s.Length switch
        {
            6 => Color.FromRgb(Byte(s, 0), Byte(s, 2), Byte(s, 4)),
            8 => Color.FromArgb(Byte(s, 0), Byte(s, 2), Byte(s, 4), Byte(s, 6)),
            _ => throw new FormatException($"Expected #RRGGBB or #AARRGGBB, got '{hex}'."),
        };

        static byte Byte(string s, int i) => byte.Parse(s.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static string ToHex(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>WCAG relative luminance of the opaque color (alpha is ignored).</summary>
    public static double RelativeLuminance(Color c)
    {
        return (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>WCAG contrast ratio, from 1 (none) to 21 (black on white).</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Linear interpolation in sRGB: <paramref name="amount"/> 0 returns <paramref name="from"/>, 1 returns <paramref name="to"/>.</summary>
    public static Color Mix(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(Lerp(from.R, to.R), Lerp(from.G, to.G), Lerp(from.B, to.B));

        byte Lerp(byte a, byte b) => (byte)Math.Round(a + ((b - a) * t));
    }
}

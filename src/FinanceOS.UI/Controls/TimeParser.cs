using System.Globalization;
using System.Text.RegularExpressions;

namespace FinanceOS.UI.Controls;

/// <summary>
/// Lenient time-of-day parsing for typed input: "9", "930", "09:30", "9.30", "9h30", "14 30",
/// "2pm", "2:30 PM", plus the culture's own AM/PM designators. Ambiguous input ("9:5") is rejected
/// rather than guessed.
/// </summary>
public static partial class TimeParser
{
    // Common designators accepted in every culture, longest first so "p.m." wins over "p".
    private static readonly string[] CommonMorning = ["a.m.", "am", "a"];
    private static readonly string[] CommonEvening = ["p.m.", "pm", "p"];

    public static bool TryParse(string? text, CultureInfo culture, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().ToLower(culture);
        var meridiem = StripMeridiem(ref s, culture);

        int hours, minutes;
        var compact = CompactDigits().Match(s);
        if (compact.Success)
        {
            // "930" → 9:30, "1430" → 14:30.
            var digits = compact.Value;
            hours = int.Parse(digits.AsSpan(0, digits.Length - 2), CultureInfo.InvariantCulture);
            minutes = int.Parse(digits.AsSpan(digits.Length - 2), CultureInfo.InvariantCulture);
        }
        else
        {
            var match = HoursAndMinutes().Match(s);
            if (!match.Success)
            {
                return false;
            }

            hours = int.Parse(match.Groups["h"].ValueSpan, CultureInfo.InvariantCulture);
            minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].ValueSpan, CultureInfo.InvariantCulture) : 0;
        }

        if (minutes > 59)
        {
            return false;
        }

        switch (meridiem)
        {
            case Meridiem.None when hours > 23:
                return false;
            case Meridiem.Morning or Meridiem.Evening when hours is < 1 or > 12:
                return false;
            case Meridiem.Morning:
                hours %= 12; // 12 AM is midnight.
                break;
            case Meridiem.Evening:
                hours = (hours % 12) + 12; // 12 PM is noon.
                break;
        }

        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    /// <summary>Formats with the culture's short time pattern ("09:30" in ro-RO, "9:30 AM" in en-US).</summary>
    public static string Format(TimeSpan time, CultureInfo culture) =>
        DateTime.MinValue.Add(time).ToString(culture.DateTimeFormat.ShortTimePattern, culture);

    private enum Meridiem
    {
        None,
        Morning,
        Evening,
    }

    private static Meridiem StripMeridiem(ref string s, CultureInfo culture)
    {
        var format = culture.DateTimeFormat;
        var morning = Candidates(format.AMDesignator, CommonMorning, culture);
        var evening = Candidates(format.PMDesignator, CommonEvening, culture);

        // Check the longest designators first across both lists, so "a.m." is not read as "a" + ".m.".
        foreach (var (designator, kind) in morning.Select(d => (d, Meridiem.Morning))
                     .Concat(evening.Select(d => (d, Meridiem.Evening)))
                     .OrderByDescending(x => x.d.Length))
        {
            if (s.Length > designator.Length && s.EndsWith(designator, StringComparison.Ordinal))
            {
                var rest = s[..^designator.Length].TrimEnd();
                if (rest.Length > 0 && char.IsDigit(rest[^1]))
                {
                    s = rest;
                    return kind;
                }
            }
        }

        return Meridiem.None;
    }

    private static IEnumerable<string> Candidates(string cultureDesignator, string[] common, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(cultureDesignator)
            ? common
            : common.Prepend(cultureDesignator.Trim().ToLower(culture)).Distinct();

    [GeneratedRegex(@"^\d{3,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactDigits();

    [GeneratedRegex(@"^(?<h>\d{1,2})(?:\s*[:.,hH\s]\s*(?<m>\d{2}))?\s*h?$", RegexOptions.CultureInvariant)]
    private static partial Regex HoursAndMinutes();
}

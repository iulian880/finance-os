using System.Globalization;

namespace FinanceOS.App.Services;

/// <summary>
/// Numeric date format (date pickers and any short date). Month and day names follow the interface
/// language; the numeric pattern follows the user's choice, or Windows' regional setting by default.
/// </summary>
public static class DateFormats
{
    /// <summary>The formats offered in Settings. "/" is a literal slash here, whatever the language.</summary>
    public static IReadOnlyList<string> Patterns { get; } = ["dd.MM.yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "MM/dd/yyyy"];

    /// <summary>Windows' own short date, read once before the app sets its culture.</summary>
    private static readonly DateTimeFormatInfo SystemFormat = (DateTimeFormatInfo)CultureInfo.CurrentCulture.DateTimeFormat.Clone();

    /// <summary>A known pattern, or null (= like Windows) for anything else.</summary>
    public static string? Normalize(string? pattern) => pattern is not null && Patterns.Contains(pattern) ? pattern : null;

    /// <summary>The interface culture with the chosen numeric date format.</summary>
    public static CultureInfo Build(CultureInfo ui, string? pattern)
    {
        var culture = (CultureInfo)ui.Clone();
        if (Normalize(pattern) is { } chosen)
        {
            culture.DateTimeFormat.ShortDatePattern = chosen;
            culture.DateTimeFormat.DateSeparator = chosen.Contains('/') ? "/" : culture.DateTimeFormat.DateSeparator;
        }
        else
        {
            culture.DateTimeFormat.ShortDatePattern = SystemFormat.ShortDatePattern;
            culture.DateTimeFormat.DateSeparator = SystemFormat.DateSeparator;
        }

        return culture;
    }

    /// <summary>"09.11.2026" in the given format (for the choices in Settings).</summary>
    public static string Example(DateTime date, CultureInfo ui, string? pattern) => date.ToString("d", Build(ui, pattern));

    /// <summary>
    /// Makes the WPF date and time pickers use the interface language and this format (they read the
    /// thread culture). Called at start-up and whenever the language or the format changes.
    /// </summary>
    public static void Apply(CultureInfo ui, string? pattern)
    {
        var culture = Build(ui, pattern);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }
}

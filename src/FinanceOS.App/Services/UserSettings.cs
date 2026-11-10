using System.Globalization;
using FinanceOS.Core.Reminders;
using FinanceOS.UI.Theming;

namespace FinanceOS.App.Services;

/// <summary>
/// The user's preferences for the session, saved on every change to <c>preferences.json</c> (read before
/// the database, so the recovery screen already has the language and theme): language, theme, accent,
/// working hours and quiet hours.
/// </summary>
public sealed class UserSettings(PreferencesStore store)
{
    public static readonly TimeOnly DefaultWorkStart = new(9, 0);
    public static readonly TimeOnly DefaultWorkEnd = new(18, 0);

    public Preferences Current { get; private set; } = store.Load();

    public event EventHandler? Changed;

    public TimeOnly WorkStart => ParseTime(Current.WorkStart) ?? DefaultWorkStart;

    public TimeOnly WorkEnd => ParseTime(Current.WorkEnd) ?? DefaultWorkEnd;

    /// <summary>Quiet hours for reminders; null when switched off.</summary>
    public QuietHours? Quiet => ParseTime(Current.QuietStart) is { } start && ParseTime(Current.QuietEnd) is { } end
        ? new QuietHours(start, end, Current.QuietWeekends)
        : null;

    public ThemePreference? Theme => Current.Theme switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        "system" => ThemePreference.System,
        _ => null,
    };

    /// <summary>New users start in Simple; Advanced is one switch away (Settings, palette).</summary>
    public const ExperienceMode DefaultMode = ExperienceMode.Simple;

    public ExperienceMode Mode => Current.Mode switch
    {
        "advanced" => ExperienceMode.Advanced,
        "simple" => ExperienceMode.Simple,
        _ => DefaultMode,
    };

    public Capabilities Capabilities => Capabilities.For(Mode);

    /// <summary>The chosen numeric date format, or null for Windows' own.</summary>
    public string? DateFormat => DateFormats.Normalize(Current.DateFormat);

    public void SetMode(ExperienceMode mode) => Update(p => p with { Mode = mode == ExperienceMode.Advanced ? "advanced" : "simple" });

    public void Update(Func<Preferences, Preferences> change)
    {
        var next = change(Current);
        if (next == Current)
        {
            return;
        }

        Current = next;
        store.Save(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string ThemeCode(ThemePreference theme) => theme.ToString().ToLowerInvariant();

    private static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}

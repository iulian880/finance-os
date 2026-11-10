using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace FinanceOS.UI.Localization;

/// <summary>UI languages. English is the neutral (fallback) language of every string table.</summary>
public enum AppLanguage
{
    English,
    Romanian,
}

/// <summary>
/// UI texts for the chosen language, switchable at runtime (ARCHITECTURE §11: .resx + markup extension).
/// Strings come from one or more <see cref="ResourceManager"/>s (this library's own table is always
/// first; the app registers its table at startup). XAML binds through <see cref="TrExtension"/> to the
/// indexer, so a language change refreshes every bound text; ViewModels listen to <see cref="Changed"/>.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>Indexer property name for <see cref="PropertyChanged"/>: refreshes every <c>[key]</c> binding.</summary>
    public const string IndexerName = "Item[]";

    /// <summary>This library's own texts (screen-reader names of built-in buttons).</summary>
    public static ResourceManager UiStrings { get; } =
        new("FinanceOS.UI.Localization.UiStrings", typeof(Localizer).Assembly);

    private readonly List<ResourceManager> _sources = [UiStrings];

    public Localizer(AppLanguage language = AppLanguage.English, params ResourceManager[] sources)
    {
        _sources.AddRange(sources);
        Language = language;
        Culture = CultureFor(language);
    }

    /// <summary>The application-wide instance used by <see cref="TrExtension"/>.</summary>
    public static Localizer Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed and every binding was told to refresh.</summary>
    public event EventHandler? Changed;

    public AppLanguage Language { get; private set; }

    /// <summary>Culture for texts, dates and numbers in the chosen language.</summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>The text for <paramref name="key"/>; a missing key shows up as <c>⟦key⟧</c> instead of failing.</summary>
    public string this[string key] => TryGet(key, out var text) ? text : $"⟦{key}⟧";

    /// <summary>The text for <paramref name="key"/> if some string table has it (for optional keys with a fallback).</summary>
    public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        foreach (var source in _sources)
        {
            if (source.GetString(key, Culture) is { } found)
            {
                text = found;
                return true;
            }
        }

        text = null;
        return false;
    }

    public static CultureInfo CultureFor(AppLanguage language) => CultureInfo.GetCultureInfo(language switch
    {
        AppLanguage.Romanian => "ro-RO",
        _ => "en-GB",
    });

    /// <summary>Romanian when Windows' display language is Romanian, otherwise English.</summary>
    public static AppLanguage FromCulture(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "ro" ? AppLanguage.Romanian : AppLanguage.English;

    /// <summary>Two-letter code stored in preferences ("en", "ro").</summary>
    public static string CodeFor(AppLanguage language) => language == AppLanguage.Romanian ? "ro" : "en";

    public static AppLanguage? FromCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "en" => AppLanguage.English,
        "ro" => AppLanguage.Romanian,
        _ => null,
    };

    /// <summary>Adds a string table (looked up after the ones already registered).</summary>
    public void Register(ResourceManager source)
    {
        if (!_sources.Contains(source))
        {
            _sources.Add(source);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
        }
    }

    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> over the text for <paramref name="key"/>.</summary>
    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    public void SetLanguage(AppLanguage language)
    {
        if (language == Language)
        {
            return;
        }

        Language = language;
        Culture = CultureFor(language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

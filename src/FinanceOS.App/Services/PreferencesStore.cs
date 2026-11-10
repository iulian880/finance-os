using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinanceOS.App.Services;

/// <summary>UI preferences that must be known before the database opens (the recovery screen needs the language).</summary>
public sealed record Preferences
{
    /// <summary>"en" or "ro"; null until the user picks one (then Windows' display language decides).</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>"system", "light" or "dark".</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("accent")]
    public string? Accent { get; init; }

    /// <summary>Working day, "HH:mm" (calendar day view).</summary>
    [JsonPropertyName("workStart")]
    public string? WorkStart { get; init; }

    [JsonPropertyName("workEnd")]
    public string? WorkEnd { get; init; }

    /// <summary>Quiet hours for reminders, "HH:mm"; null = off.</summary>
    [JsonPropertyName("quietStart")]
    public string? QuietStart { get; init; }

    [JsonPropertyName("quietEnd")]
    public string? QuietEnd { get; init; }

    [JsonPropertyName("quietWeekends")]
    public bool QuietWeekends { get; init; }

    /// <summary>"simple" or "advanced"; null until chosen (onboarding or Settings).</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>Numeric date format, one of <see cref="DateFormats.Patterns"/>; null = like Windows.</summary>
    [JsonPropertyName("dateFormat")]
    public string? DateFormat { get; init; }

    /// <summary>The first-run guide was finished or skipped.</summary>
    [JsonPropertyName("onboarded")]
    public bool Onboarded { get; init; }
}

/// <summary>
/// <c>preferences.json</c> in the data folder: <c>{"language":"ro"}</c>. A small file instead of the
/// database on purpose: it is read before the database starts and must work when the database does not.
/// Any read or write problem means "defaults"; it never blocks start-up.
/// </summary>
public sealed class PreferencesStore(string path)
{
    /// <summary>Only what the user changed is written; everything else stays a default.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public string FilePath { get; } = path;

    public Preferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new Preferences()
                : new Preferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Preferences();
        }
    }

    public void Save(Preferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write-then-rename, so a crash mid-write never leaves a half file behind.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, WriteOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still applies for this session; the next change tries again.
        }
    }
}

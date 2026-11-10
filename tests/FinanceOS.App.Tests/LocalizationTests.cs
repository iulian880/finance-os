using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FinanceOS.App.Services;
using FinanceOS.UI.Localization;
using FinanceOS.UI.Theming;
using Xunit;

namespace FinanceOS.App.Tests;

/// <summary>
/// M5b: every text exists in English and Romanian, every key the code asks for exists, no UI text is
/// left hard-coded in XAML, and the language choice survives a restart.
/// </summary>
public sealed partial class LocalizationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    public static TheoryData<string> StringTables => new()
    {
        "src/FinanceOS.App/Localization/Strings",
        "src/FinanceOS.UI/Localization/UiStrings",
    };

    [Theory]
    [MemberData(nameof(StringTables))]
    public void Every_text_exists_in_both_languages_with_the_same_placeholders(string table)
    {
        var english = ReadResx($"{table}.resx");
        var romanian = ReadResx($"{table}.ro.resx");

        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), romanian.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{key} is empty in English");
            Assert.False(string.IsNullOrWhiteSpace(romanian[key]), $"{key} is empty in Romanian");
            Assert.Equal(Placeholders(text), Placeholders(romanian[key]));
        }
    }

    [Fact]
    public void Every_key_used_in_code_and_xaml_exists()
    {
        var keys = ReadResx("src/FinanceOS.App/Localization/Strings.resx").Keys
            .Concat(ReadResx("src/FinanceOS.UI/Localization/UiStrings.resx").Keys)
            .ToHashSet(StringComparer.Ordinal);

        var used = new List<(string File, string Key)>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            used.AddRange(TrKey().Matches(File.ReadAllText(file)).Select(m => (file, m.Groups["key"].Value)));
        }

        // In C#, any string literal that starts like a key ("Recovery.Error.Retry", also inside a switch
        // or passed as an argument) must exist. Interpolated keys ("Section.{Section}.Title") are covered
        // by the section and accent tests.
        var prefixes = keys.Select(k => k[..k.IndexOf('.', StringComparison.Ordinal)]).Distinct().Select(Regex.Escape);
        var keyLiteral = new Regex($@"""(?<key>(?:{string.Join('|', prefixes)})\.[\w.]+)""");
        // Only the projects that show text: Core and Data have names like "Common.Snapshots" that are not keys.
        string[] uiProjects = [$"{Path.DirectorySeparatorChar}FinanceOS.App{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}FinanceOS.UI{Path.DirectorySeparatorChar}"];
        foreach (var file in SourceFiles("*.cs").Where(f => uiProjects.Any(p => f.Contains(p, StringComparison.Ordinal))))
        {
            used.AddRange(keyLiteral.Matches(File.ReadAllText(file)).Select(m => (file, m.Groups["key"].Value)));
        }

        Assert.True(used.Count > 100, $"expected the scan to find the keys in use, found {used.Count}");
        var missing = used.Where(u => !keys.Contains(u.Key)).Select(u => $"{Path.GetFileName(u.File)}: {u.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Missing keys:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void No_ui_text_is_hard_coded_in_the_app_xaml()
    {
        // Brand name, keyboard hints and punctuation are the same in every language.
        string[] allowed = ["Finance OS", "Ctrl K", "Esc", "–", "."];
        var dev = $"{Path.DirectorySeparatorChar}Dev{Path.DirectorySeparatorChar}";
        var offenders = new List<string>();
        foreach (var file in SourceFiles("*.xaml").Where(f => !f.Contains(dev, StringComparison.Ordinal)))
        {
            foreach (Match m in TextAttribute().Matches(File.ReadAllText(file)))
            {
                var value = m.Groups["value"].Value;
                // Icon-font glyphs (&#xE921;) are not words.
                var words = GlyphReference().Replace(value, "");
                if (!value.StartsWith('{') && words.Any(char.IsLetter) && !allowed.Contains(value))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Hard-coded texts:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_accent_has_a_name_in_both_languages()
    {
        foreach (var loc in new[] { TestLoc.Ro(), TestLoc.En() })
        {
            Assert.All(Accents.All, a => Assert.DoesNotContain("⟦", loc[$"Accent.{a.Id}"]));
        }

        Assert.Equal("Verde petrol", TestLoc.Ro()["Accent.teal"]);
        Assert.Equal("Teal", TestLoc.En()["Accent.teal"]);
    }

    [Fact]
    public void Missing_key_is_visible_instead_of_crashing()
    {
        Assert.Equal("⟦No.Such.Key⟧", TestLoc.En()["No.Such.Key"]);
    }

    [Theory]
    [InlineData("ro-RO", AppLanguage.Romanian)]
    [InlineData("ro-MD", AppLanguage.Romanian)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("de-DE", AppLanguage.English)]
    public void Default_language_follows_windows_and_falls_back_to_english(string windows, AppLanguage expected)
    {
        Assert.Equal(expected, Localizer.FromCulture(CultureInfo.GetCultureInfo(windows)));
    }

    [Theory]
    [InlineData("ro", AppLanguage.Romanian)]
    [InlineData(" RO ", AppLanguage.Romanian)]
    [InlineData("en", AppLanguage.English)]
    [InlineData("fr", null)]
    [InlineData(null, null)]
    public void Language_codes_round_trip(string? code, AppLanguage? expected)
    {
        Assert.Equal(expected, Localizer.FromCode(code));
        if (expected is { } language)
        {
            Assert.Equal(language, Localizer.FromCode(Localizer.CodeFor(language)));
        }
    }

    [Fact]
    public void Startup_switch_sets_the_language_for_the_session()
    {
        Assert.Equal(AppLanguage.English, StartupOptions.Parse(["--lang=en"]).Language);
        Assert.Equal(AppLanguage.Romanian, StartupOptions.Parse(["--lang=ro"]).Language);
        Assert.Null(StartupOptions.Parse(["--lang=xx"]).Language);
        Assert.Null(StartupOptions.Parse([]).Language);
    }

    [Fact]
    public void Preferences_keep_the_language_between_sessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PreferencesStore(Path.Combine(root, "preferences.json"));
            Assert.Null(store.Load().Language);

            store.Save(new Preferences { Language = "ro" });

            Assert.Equal("ro", new PreferencesStore(store.FilePath).Load().Language);
            Assert.Equal("{\"language\":\"ro\"}", File.ReadAllText(store.FilePath));
            Assert.False(File.Exists(store.FilePath + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Unreadable_preferences_mean_defaults_not_a_failed_start()
    {
        var root = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "preferences.json");
            File.WriteAllText(path, "{ not json");
            Assert.Null(new PreferencesStore(path).Load().Language);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, string> ReadResx(string relativePath) =>
        XDocument.Load(Path.Combine(RepoRoot, relativePath)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "", StringComparer.Ordinal);

    private static string[] Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    private static IEnumerable<string> SourceFiles(string pattern)
    {
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        return Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(obj, StringComparison.Ordinal) && !f.Contains(bin, StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FinanceOS.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (FinanceOS.slnx) not found.");
    }

    [GeneratedRegex(@"&#x[0-9A-Fa-f]+;")]
    private static partial Regex GlyphReference();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{loc:Tr\s+(Key=)?(?<key>[\w.]+)\s*\}")]
    private static partial Regex TrKey();

    /// <summary>Attributes that show text to the user or to a screen reader.</summary>
    [GeneratedRegex(@"\b(?:Text|Content|Header|Description|Title|ToolTip|Placeholder|EmptyText|AutomationProperties\.Name|ui:FieldAssist\.Header|ui:FieldAssist\.HelperText|MinimizeText|MaximizeText|RestoreText|CloseText)=""(?<value>[^""]*)""")]
    private static partial Regex TextAttribute();
}

namespace FinanceOS.App;

/// <summary>Command-line switches. Unknown arguments (e.g. Windows' "-ToastActivated") are ignored.</summary>
/// <param name="Probe">Report scheduled/delivered toasts to <see cref="OutputPath"/> and exit (no UI).</param>
/// <param name="Benchmark">Measure startup and memory, write to <see cref="OutputPath"/>, exit. Skips single-instance and toast registration.</param>
/// <param name="BenchIdleSeconds">With <see cref="Benchmark"/>: also measure after N seconds idle with the window open, then N seconds in tray.</param>
/// <param name="OutputPath">File that probe/benchmark results are appended to, one JSON object per line.</param>
/// <param name="StartInTray">Start without showing the main window (used later by "start with Windows").</param>
/// <param name="ShowTokens">Debug builds only: open the design-token page instead of the main window.</param>
/// <param name="Theme">Optional override: "light", "dark" or "system" (screenshots, design reviews). Persisted settings come in M12.</param>
/// <param name="AccentId">Optional accent override, e.g. "teal".</param>
/// <param name="ShowGallery">Debug builds only: open the component gallery instead of the main window.</param>
/// <param name="ScreenshotPath">With a developer page: render the whole page to this PNG, then exit.</param>
/// <param name="ScreenshotScale">Pixel density of the screenshot (1 = 100 %, 1.5 = 150 %).</param>
internal sealed record StartupOptions(
    bool Probe, bool Benchmark, int? BenchIdleSeconds, string? OutputPath, bool StartInTray, bool ShowTokens,
    string? Theme = null, string? AccentId = null, bool ShowGallery = false, string? ScreenshotPath = null, double ScreenshotScale = 1.0)
{
    /// <summary>Debug builds only: open the M0 spike window (toast tests) instead of the shell.</summary>
    public bool ShowSpike { get; init; }

    /// <summary>Section to open first (<c>--section=tasks</c>); null keeps the default (Overview).</summary>
    public Shell.Section? Section { get; init; }

    /// <summary>Right panel to open at start: "details" or "notifications".</summary>
    public string? Panel { get; init; }

    /// <summary>
    /// Debug builds only: use another data folder (<c>--data-root=D:/temp/fos</c>), so recovery and
    /// migration scenarios can be tried and screenshotted without touching the real data.
    /// </summary>
    public string? DataRoot { get; init; }

    /// <summary>
    /// UI language for this session only (<c>--lang=en</c> / <c>--lang=ro</c>), for screenshots and reviews.
    /// Not saved; a change made in Settings during the session is.
    /// </summary>
    public UI.Localization.AppLanguage? Language { get; init; }

    /// <summary>Debug builds only: fill an empty database with sample tasks (<c>--seed</c>, with <c>--data-root</c>).</summary>
    public bool Seed { get; init; }

    /// <summary>Debug builds only: show the first-run guide even when it was done (<c>--onboarding</c>).</summary>
    public bool ShowOnboarding { get; init; }

    /// <summary>A developer page (tokens, gallery) was requested; it runs without tray, toasts or single-instance.</summary>
    public bool IsDevPage => ShowTokens || ShowGallery;

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        bool probe = false, bench = false, tray = false, tokens = false, gallery = false;
        int? idle = null;
        double scale = 1.0;
        string? output = null, theme = null, accent = null, screenshot = null;

        foreach (var arg in args)
        {
            if (arg == "--probe")
            {
                probe = true;
            }
            else if (arg == "--bench")
            {
                bench = true;
            }
            else if (arg == "--tray")
            {
                tray = true;
            }
            else if (arg == "--tokens")
            {
                tokens = true;
            }
            else if (arg == "--gallery")
            {
                gallery = true;
            }
            else if (arg.StartsWith("--screenshot=", StringComparison.Ordinal))
            {
                screenshot = arg["--screenshot=".Length..].Trim('"');
            }
            else if (arg.StartsWith("--scale=", StringComparison.Ordinal)
                && double.TryParse(arg.AsSpan("--scale=".Length), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s) && s is >= 0.5 and <= 4)
            {
                scale = s;
            }
            else if (arg.StartsWith("--theme=", StringComparison.Ordinal))
            {
                theme = arg["--theme=".Length..];
            }
            else if (arg.StartsWith("--accent=", StringComparison.Ordinal))
            {
                accent = arg["--accent=".Length..];
            }
            else if (arg.StartsWith("--bench-idle=", StringComparison.Ordinal)
                && int.TryParse(arg.AsSpan("--bench-idle=".Length), out var seconds) && seconds > 0)
            {
                bench = true;
                idle = seconds;
            }
            else if (arg.StartsWith("--out=", StringComparison.Ordinal))
            {
                output = arg["--out=".Length..].Trim('"');
            }
        }

        return new StartupOptions(probe, bench, idle, output, tray, tokens, theme, accent, gallery, screenshot, scale)
        {
            ShowSpike = args.Contains("--spike"),
            Section = args.Select(a => a.StartsWith("--section=", StringComparison.Ordinal) ? a["--section=".Length..] : null)
                .Select(s => Enum.TryParse<Shell.Section>(s, ignoreCase: true, out var section) ? section : (Shell.Section?)null)
                .LastOrDefault(s => s is not null),
            Panel = args.LastOrDefault(a => a.StartsWith("--panel=", StringComparison.Ordinal))?["--panel=".Length..],
            Language = UI.Localization.Localizer.FromCode(args.LastOrDefault(a => a.StartsWith("--lang=", StringComparison.Ordinal))?["--lang=".Length..]),
#if DEBUG
            Seed = args.Contains("--seed"),
            ShowOnboarding = args.Contains("--onboarding"),
            DataRoot = args.LastOrDefault(a => a.StartsWith("--data-root=", StringComparison.Ordinal))?["--data-root=".Length..].Trim('"'),
#endif
        };
    }
}

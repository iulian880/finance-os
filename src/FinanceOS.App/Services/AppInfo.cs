using System.Diagnostics;
using System.Globalization;
using FinanceOS.UI.Localization;

namespace FinanceOS.App.Services;

/// <summary>Credits and the contact line for suggestions and bug reports (shown in Settings → Despre).</summary>
public static class AppInfo
{
    public const string Author = "Petcu Iulian";

    /// <summary>First year of publication, for the copyright line (same as in Directory.Build.props).</summary>
    public const int CopyrightYear = 2026;

    public const string AuthorRole = "App Author";

    // TODO: set a real contact address before distributing a build. Placeholder in the public repository.
    public const string ContactEmail = "contact@example.com";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// <c>mailto:</c> link with a prefilled subject and the technical facts a bug report needs (app and
    /// Windows version). Nothing about the user's data goes into it; the user writes the rest.
    /// </summary>
    public static Uri FeedbackMailto(Localizer l)
    {
        var subject = l.Format("Feedback.Subject", Version);
        var body = string.Join("\r\n",
            l["Feedback.Describe"],
            "",
            "",
            l["Feedback.Steps"],
            "1. ",
            "",
            "---",
            $"{l["Feedback.Version"]}: {Version}",
            $"Windows: {Environment.OSVersion.Version}",
            $"{l["Feedback.Language"]}: {l.Culture.Name} (Windows: {CultureInfo.CurrentUICulture.Name})");
        return new Uri($"mailto:{ContactEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}");
    }
}

/// <summary>Opens links and folders with the user's default programs. Behind an interface for tests.</summary>
public interface ILauncher
{
    void Open(Uri uri);
}

public sealed class ShellLauncher : ILauncher
{
    public void Open(Uri uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

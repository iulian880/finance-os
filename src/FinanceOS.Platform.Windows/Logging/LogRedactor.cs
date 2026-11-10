using System.Globalization;
using System.Text.RegularExpressions;

namespace FinanceOS.Platform.Windows.Logging;

/// <summary>
/// Keeps work content out of the log (ARCHITECTURE §11): logs carry ids and event types, never titles,
/// descriptions or invoice references. Two layers:
/// <list type="number">
/// <item>Structured values whose name says "content" (<c>{Title}</c>, <c>{Notes}</c>, <c>{Query}</c>, …) are
/// replaced by <c>[redacted]</c> whatever they contain.</item>
/// <item>Every other value and all exception text is scrubbed for e-mail addresses, IBANs, long digit runs
/// (CNP, card and account numbers, invoice numbers) and the Windows user name in paths. Known-safe
/// identifiers (<c>{TaskId}</c>, <c>{Tag}</c>, <c>{Count}</c>, …) only lose the user name.</item>
/// </list>
/// The template text is scrubbed too, so an interpolated string logged by mistake (<c>$"…{title}"</c>) is still covered.
/// </summary>
public static partial class LogRedactor
{
    public const string Redacted = "[redacted]";

    private static readonly HashSet<string> ContentNames =
    [
        "title", "description", "notes", "note", "body", "text", "content", "name", "subject",
        "requester", "responsible", "contact", "email", "ref", "externalref", "reference", "url",
        "query", "search", "comment", "value", "before", "after",
    ];

    private static readonly string[] SafeSuffixes =
    [
        "id", "ids", "tag", "tags", "count", "ms", "version", "kind", "state", "status", "action", "reason",
        "code", "bytes", "level", "mode", "minutes", "seconds", "utc", "type", "crashed", "new", "check",
    ];

    public static bool IsContent(string name) => ContentNames.Contains(Normalize(name));

    public static bool IsSafeIdentifier(string name)
    {
        var normalized = Normalize(name);
        return !ContentNames.Contains(normalized) && SafeSuffixes.Any(s => normalized.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>The text to write for structured value <paramref name="name"/>.</summary>
    public static string Value(string name, object? value, string? format = null)
    {
        if (value is null)
        {
            return "null";
        }

        if (IsContent(name))
        {
            return Redacted;
        }

        var text = value switch
        {
            DateTimeOffset d => d.ToString(format ?? "O", CultureInfo.InvariantCulture),
            DateTime d => d.ToString(format ?? "O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        return IsSafeIdentifier(name) ? ScrubPaths(text) : Scrub(text);
    }

    /// <summary>Removes personal and financial identifiers from free text.</summary>
    public static string Scrub(string text)
    {
        text = ScrubPaths(text);
        text = EmailPattern().Replace(text, "[email]");
        text = IbanPattern().Replace(text, "[iban]");
        return LongNumberPattern().Replace(text, "[number]");
    }

    /// <summary>Replaces the user-name segment of Windows profile paths.</summary>
    public static string ScrubPaths(string text) => UserPathPattern().Replace(text, "$1[user]");

    private static string Normalize(string name) =>
        name.TrimStart('@', '$').Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    // Country code, 2 check digits, then 11–30 alphanumerics, optionally grouped by spaces (RO49 AAAA 1B31 …).
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b")]
    private static partial Regex IbanPattern();

    // 9+ digits, optionally separated by single spaces or dashes (4111 1111 1111 1111, CNP, account numbers).
    [GeneratedRegex(@"(?<![\w])\d(?:[ -]?\d){8,}(?![\w])")]
    private static partial Regex LongNumberPattern();

    [GeneratedRegex(@"([A-Za-z]:\\(?:Users|Documents and Settings)\\)[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserPathPattern();
}

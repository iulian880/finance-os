using System.Globalization;

namespace FinanceOS.Data.Backup;

/// <summary>Why a backup was made. Decides how long it is kept (<see cref="BackupRotation"/>).</summary>
public enum BackupKind
{
    /// <summary>Automatic, once per local day. Kept: 7 daily + 4 weekly.</summary>
    Daily,

    /// <summary>Right before a schema migration. Kept: last 5.</summary>
    PreMigration,

    /// <summary>Of the current state, right before a restore replaces it. Kept: last 5.</summary>
    PreRestore,

    /// <summary>Before an import writes anything (M15). Kept: last 5.</summary>
    PreImport,

    /// <summary>Asked for by the user. Never deleted automatically.</summary>
    Manual,

    /// <summary>Raw copy of a database that failed its integrity check, kept for analysis. Never deleted automatically.</summary>
    Damaged,
}

/// <summary>
/// One backup file: <c>financeos-20261105T083000123Z-daily.db</c>. Everything needed is in the name
/// (UTC moment and kind), so listing backups never has to open them.
/// </summary>
public sealed record BackupInfo(string FilePath, BackupKind Kind, DateTimeOffset CreatedUtc, long SizeBytes)
{
    private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    public string FileName => Path.GetFileName(FilePath);

    public static string FileNameFor(BackupKind kind, DateTimeOffset createdUtc) =>
        $"financeos-{createdUtc.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}-{Slug(kind)}.db";

    /// <summary>Parses a backup file name; null for anything else (those files are never touched).</summary>
    public static BackupInfo? TryParse(string filePath, long sizeBytes = 0)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (!Path.GetExtension(filePath).Equals(".db", StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("financeos-", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name["financeos-".Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 ||
            !DateTime.TryParseExact(rest[..dash], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp) ||
            ParseSlug(rest[(dash + 1)..]) is not { } kind)
        {
            return null;
        }

        return new BackupInfo(filePath, kind, new DateTimeOffset(stamp, TimeSpan.Zero), sizeBytes);
    }

    private static string Slug(BackupKind kind) => kind switch
    {
        BackupKind.Daily => "daily",
        BackupKind.PreMigration => "pre-migration",
        BackupKind.PreRestore => "pre-restore",
        BackupKind.PreImport => "pre-import",
        BackupKind.Manual => "manual",
        BackupKind.Damaged => "damaged",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static BackupKind? ParseSlug(string slug) => slug switch
    {
        "daily" => BackupKind.Daily,
        "pre-migration" => BackupKind.PreMigration,
        "pre-restore" => BackupKind.PreRestore,
        "pre-import" => BackupKind.PreImport,
        "manual" => BackupKind.Manual,
        "damaged" => BackupKind.Damaged,
        _ => null,
    };
}

/// <summary>
/// Retention (ARCHITECTURE §10): newest daily backup of each of the last 7 days that have one, plus the
/// newest of each of the 4 ISO weeks before those; last 5 of each "before X" kind; manual and damaged
/// copies are never deleted. Pure function, so it is tested without files.
/// </summary>
public static class BackupRotation
{
    public const int DailyKept = 7;
    public const int WeeklyKept = 4;
    public const int EventKept = 5;

    public static IReadOnlyList<BackupInfo> SelectForDeletion(IEnumerable<BackupInfo> backups, TimeZoneInfo zone)
    {
        var all = backups.ToList();
        var keep = new HashSet<BackupInfo>();

        // Newest first, so First() of each group is the newest of that day / week.
        var newestPerDay = all.Where(b => b.Kind == BackupKind.Daily)
            .OrderByDescending(b => b.CreatedUtc)
            .GroupBy(b => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(b.CreatedUtc, zone).DateTime))
            .Select(g => g.First())
            .ToList();
        keep.UnionWith(newestPerDay.Take(DailyKept));

        var newestPerWeek = newestPerDay.Skip(DailyKept)
            .GroupBy(b =>
            {
                var local = TimeZoneInfo.ConvertTime(b.CreatedUtc, zone).DateTime;
                return (ISOWeek.GetYear(local), ISOWeek.GetWeekOfYear(local));
            })
            .Select(g => g.First())
            .Take(WeeklyKept);
        keep.UnionWith(newestPerWeek);

        foreach (var kind in new[] { BackupKind.PreMigration, BackupKind.PreRestore, BackupKind.PreImport })
        {
            keep.UnionWith(all.Where(b => b.Kind == kind).OrderByDescending(b => b.CreatedUtc).Take(EventKept));
        }

        keep.UnionWith(all.Where(b => b.Kind is BackupKind.Manual or BackupKind.Damaged));
        return all.Where(b => !keep.Contains(b)).ToList();
    }
}

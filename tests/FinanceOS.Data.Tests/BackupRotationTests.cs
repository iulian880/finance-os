using FinanceOS.Data.Backup;
using Xunit;

namespace FinanceOS.Data.Tests;

public sealed class BackupRotationTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    // Thursday 5 November 2026, 10:30 local.
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Keeps_seven_daily_and_four_weekly_backups()
    {
        // One daily backup per day for 60 days.
        var backups = Enumerable.Range(0, 60).Select(d => Backup(BackupKind.Daily, Now.AddDays(-d))).ToList();

        var deleted = BackupRotation.SelectForDeletion(backups, Zone);
        var kept = backups.Except(deleted).OrderByDescending(b => b.CreatedUtc).ToList();

        Assert.Equal(BackupRotation.DailyKept + BackupRotation.WeeklyKept, kept.Count);
        Assert.Equal(backups.Take(7), kept.Take(7)); // the last 7 days
        var weeks = kept.Skip(7).Select(b => System.Globalization.ISOWeek.GetWeekOfYear(b.CreatedUtc.UtcDateTime)).ToList();
        Assert.Equal(weeks.Distinct().Count(), weeks.Count); // one per week
        Assert.All(kept.Skip(7), b => Assert.True(b.CreatedUtc < kept[6].CreatedUtc));
    }

    [Fact]
    public void Several_backups_on_one_day_count_as_one_day_keeping_the_newest()
    {
        var morning = Backup(BackupKind.Daily, Now.AddHours(-2));
        var evening = Backup(BackupKind.Daily, Now);

        Assert.Equal([morning], BackupRotation.SelectForDeletion([morning, evening], Zone));
    }

    [Fact]
    public void Days_are_local_days()
    {
        // 23:30 and 00:30 local are different days even though both are on the same UTC date.
        var lateEvening = Backup(BackupKind.Daily, new DateTimeOffset(2026, 11, 4, 21, 30, 0, TimeSpan.Zero));
        var afterMidnight = Backup(BackupKind.Daily, new DateTimeOffset(2026, 11, 4, 22, 30, 0, TimeSpan.Zero));

        Assert.Empty(BackupRotation.SelectForDeletion([lateEvening, afterMidnight], Zone));
    }

    [Fact]
    public void Event_backups_keep_the_last_five_of_each_kind()
    {
        var migrations = Enumerable.Range(0, 8).Select(i => Backup(BackupKind.PreMigration, Now.AddDays(-i))).ToList();
        var restores = Enumerable.Range(0, 3).Select(i => Backup(BackupKind.PreRestore, Now.AddDays(-i))).ToList();

        var deleted = BackupRotation.SelectForDeletion(migrations.Concat(restores), Zone);

        Assert.Equal(migrations.Skip(5), deleted);
    }

    [Fact]
    public void Manual_and_damaged_copies_are_never_deleted()
    {
        var old = Enumerable.Range(0, 30).SelectMany(i => new[]
        {
            Backup(BackupKind.Manual, Now.AddDays(-i * 30)),
            Backup(BackupKind.Damaged, Now.AddDays(-i * 30)),
        });

        Assert.Empty(BackupRotation.SelectForDeletion(old, Zone));
    }

    [Theory]
    [InlineData(BackupKind.Daily)]
    [InlineData(BackupKind.PreMigration)]
    [InlineData(BackupKind.PreRestore)]
    [InlineData(BackupKind.PreImport)]
    [InlineData(BackupKind.Manual)]
    [InlineData(BackupKind.Damaged)]
    public void File_name_round_trips(BackupKind kind)
    {
        var at = new DateTimeOffset(2026, 11, 5, 8, 30, 0, 123, TimeSpan.Zero);
        var name = BackupInfo.FileNameFor(kind, at);

        var parsed = BackupInfo.TryParse(Path.Combine("C:\\b", name));

        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(at, parsed.CreatedUtc);
    }

    [Theory]
    [InlineData("financeos.db")]
    [InlineData("financeos-20261105T083000123Z-weird.db")]
    [InlineData("financeos-yesterday-daily.db")]
    [InlineData("financeos-20261105T083000123Z-daily.db-wal")]
    [InlineData("other-20261105T083000123Z-daily.db")]
    public void Unknown_names_are_not_backups(string name)
    {
        Assert.Null(BackupInfo.TryParse(Path.Combine("C:\\b", name)));
    }

    private static BackupInfo Backup(BackupKind kind, DateTimeOffset at) =>
        new($"C:\\b\\{BackupInfo.FileNameFor(kind, at)}", kind, at, 1024);
}

using System.IO;
using FinanceOS.Platform.Windows.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FinanceOS.App.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "FinanceOS.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 10, 30, 0, TimeSpan.FromHours(2)));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Content_values_are_redacted_and_ids_kept()
    {
        var (logger, writer) = Create();

        logger.LogInformation("Task {TaskId} renamed to {Title} with notes {Notes}", "0193f2a1-7c3e", "Plată furnizor Alfa", "IBAN în note");

        var line = Assert.Single(Lines(writer));
        Assert.Contains("Task 0193f2a1-7c3e renamed to [redacted] with notes [redacted]", line);
        Assert.DoesNotContain("Alfa", line);
    }

    [Fact]
    public void Line_format_stays_compatible_with_the_m0_check_script()
    {
        var (logger, writer) = Create();

        logger.LogInformation("forwarded args=[{Args}]", "--section=tasks");

        // build/spike-m0-check.ps1: first 23 characters parse as the time, then "[pid] message".
        var line = Assert.Single(Lines(writer));
        Assert.StartsWith("2026-11-05 10:30:00.000 +02:00 INF Shell [", line);
        Assert.Contains($"[{Environment.ProcessId}] forwarded args=[--section=tasks]", line);
        Assert.EndsWith(Path.Combine(_folder, "financeos-20261105.log"), writer.CurrentFilePath);
    }

    [Theory]
    [InlineData("Contact: ana.popescu@firma.ro", "[email]")]
    [InlineData("IBAN RO49AAAA1B31007593840000", "[iban]")]
    [InlineData("IBAN RO49 AAAA 1B31 0075 9384 0000", "[iban]")]
    [InlineData("Card 4111 1111 1111 1111", "[number]")]
    [InlineData("CNP 1850101123456", "[number]")]
    [InlineData(@"C:\Users\ana.popescu\AppData\Local\FinanceOS", @"C:\Users\[user]\AppData\Local\FinanceOS")]
    public void Free_text_is_scrubbed(string text, string expected)
    {
        var scrubbed = LogRedactor.Scrub(text);
        Assert.Contains(expected, scrubbed);
        Assert.DoesNotContain("ana.popescu", scrubbed);
        Assert.DoesNotContain("1111 1111", scrubbed);
        Assert.DoesNotContain("1850101123456", scrubbed);
        Assert.DoesNotContain("9384", scrubbed);
    }

    [Theory]
    [InlineData("financeos-20261105T083000123Z-daily.db")]
    [InlineData("startup_ms=412")]
    [InlineData("2026-11-05T08:30:00.0000000+00:00")]
    [InlineData("0193f2a1-7c3e-7b4a-9f10-2a3b4c5d6e7f")]
    [InlineData("Migration 1 failed")]
    public void Ordinary_technical_text_is_left_alone(string text)
    {
        Assert.Equal(text, LogRedactor.Scrub(text));
    }

    [Fact]
    public void Unnamed_values_are_scrubbed_and_safe_identifiers_only_lose_the_user_name()
    {
        Assert.Equal("[email]", LogRedactor.Value("Recipient", "ana@firma.ro"));
        Assert.Equal("s-1730803800123", LogRedactor.Value("Tag", "s-1730803800123"));
        Assert.Equal("1730803800123", LogRedactor.Value("ElapsedMs", 1730803800123L));
        Assert.Equal(@"C:\Users\[user]\x", LogRedactor.Value("BackupId", @"C:\Users\ana\x"));
        Assert.Equal(LogRedactor.Redacted, LogRedactor.Value("title", "x"));
        Assert.Equal(LogRedactor.Redacted, LogRedactor.Value("@External_Ref", "FV-001"));
    }

    [Fact]
    public void Plain_messages_without_a_template_are_scrubbed()
    {
        var (logger, writer) = Create();

#pragma warning disable CA2254 // deliberately not a template: the sink must still protect it
        logger.LogWarning($"Factura pentru ana@firma.ro, cont RO49AAAA1B31007593840000");
#pragma warning restore CA2254

        var line = Assert.Single(Lines(writer));
        Assert.Contains("[email]", line);
        Assert.Contains("[iban]", line);
    }

    [Fact]
    public void Exceptions_are_written_scrubbed_on_indented_lines()
    {
        var (logger, writer) = Create();

        logger.LogError(new IOException(@"Access denied: C:\Users\ana\AppData\Local\FinanceOS\data\financeos.db"), "Backup {Kind} failed", "daily");

        var text = File.ReadAllText(writer.CurrentFilePath);
        Assert.Contains("ERR Shell", text);
        Assert.Contains("Backup daily failed", text);
        Assert.Contains(@"    System.IO.IOException: Access denied: C:\Users\[user]\AppData", text);
        Assert.DoesNotContain(@"\ana\", text);
    }

    [Fact]
    public void Format_specifiers_in_the_template_are_applied()
    {
        var (logger, writer) = Create();
        logger.LogInformation("first-frame startup_ms={StartupMs:F0} ratio={Ratio:0.00}", 1002.505, 0.5);
        Assert.Contains("startup_ms=1003 ratio=0.50", Assert.Single(Lines(writer)));
    }

    [Fact]
    public void Multi_line_messages_stay_on_one_line()
    {
        var (logger, writer) = Create();
        logger.LogInformation("first{NewLine}second", Environment.NewLine);
        Assert.Single(Lines(writer));
    }

    [Fact]
    public void Levels_below_the_minimum_are_dropped()
    {
        var (logger, writer) = Create(LogLevel.Information);
        logger.LogDebug("noise {Count}", 1);
        logger.LogInformation("kept {Count}", 2);
        Assert.Single(Lines(writer));
    }

    [Fact]
    public void Files_roll_daily_and_old_ones_are_deleted()
    {
        var (logger, writer) = Create();
        Directory.CreateDirectory(_folder);
        var old = Path.Combine(_folder, "financeos-20261001.log");
        var recent = Path.Combine(_folder, "financeos-20261030.log");
        var other = Path.Combine(_folder, "notes.log");
        File.WriteAllText(old, "");
        File.WriteAllText(recent, "");
        File.WriteAllText(other, "");

        logger.LogInformation("day one {Count}", 1);
        _clock.Advance(TimeSpan.FromDays(1));
        logger.LogInformation("day two {Count}", 2);

        Assert.False(File.Exists(old)); // older than 14 days
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other)); // not ours
        Assert.True(File.Exists(Path.Combine(_folder, "financeos-20261105.log")));
        Assert.EndsWith("financeos-20261106.log", writer.CurrentFilePath);
        Assert.Single(Lines(writer));
    }

    [Fact]
    public void A_day_is_capped_so_a_loop_cannot_fill_the_disk()
    {
        var writer = new FileLogWriter(_folder, _clock, maxBytesPerDay: 2_000);
        var logger = new FileLoggerProvider(writer, _clock).CreateLogger("Loop");

        for (var i = 0; i < 200; i++)
        {
            logger.LogInformation("entry {Count}", i);
        }

        Assert.True(new FileInfo(writer.CurrentFilePath).Length < 2_000 + 200);
        Assert.Contains("log limit reached", File.ReadAllText(writer.CurrentFilePath));
    }

    [Fact]
    public void Unwritable_folder_never_throws()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "");
        var writer = new FileLogWriter(Path.Combine(blocker, "logs"), _clock);
        var logger = new FileLoggerProvider(writer, _clock).CreateLogger("X");

        logger.LogError("still running {Count}", 1);

        Assert.Empty(writer.Tail(10));
    }

    private (ILogger Logger, FileLogWriter Writer) Create(LogLevel minimum = LogLevel.Trace)
    {
        var writer = new FileLogWriter(_folder, _clock);
        var provider = new FileLoggerProvider(writer, _clock, minimum);
        return (provider.CreateLogger("FinanceOS.App.Shell"), writer);
    }

    private static IReadOnlyList<string> Lines(FileLogWriter writer) => writer.Tail(1000);
}

/// <summary>Clock moved by hand, in the offset of the start value (no DST).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("Test", start.Offset, "Test", "Test");

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    public void Advance(TimeSpan by) => _now += by;
}

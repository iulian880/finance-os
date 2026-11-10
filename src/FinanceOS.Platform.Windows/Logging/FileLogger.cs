using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FinanceOS.Platform.Windows.Logging;

/// <summary>
/// Daily log files in <c>%LOCALAPPDATA%\FinanceOS\logs</c>: <c>financeos-20261105.log</c>, one line per
/// entry, kept 14 days, at most 10 MB per day (a runaway loop cannot fill the disk). Writing never throws:
/// a log that cannot be written is lost, the app keeps working.
/// </summary>
/// <remarks>
/// Line format (kept compatible with build/spike-m0-check.ps1):
/// <c>2026-11-05 10:30:00.123 +02:00 INF Category [pid] message</c>.
/// </remarks>
public sealed class FileLogWriter(string directory, TimeProvider time, int retentionDays = 14, long maxBytesPerDay = 10 * 1024 * 1024)
{
    private readonly Lock _gate = new();
    private DateOnly _cleanedFor;

    public string Directory { get; } = directory;

    public string CurrentFilePath => PathFor(Today());

    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var today = Today();
                if (_cleanedFor != today)
                {
                    _cleanedFor = today;
                    DeleteOldFiles(today);
                }

                var path = PathFor(today);
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= maxBytesPerDay)
                {
                    return;
                }

                var text = line + Environment.NewLine;
                if (info.Exists && info.Length + Encoding.UTF8.GetByteCount(text) >= maxBytesPerDay)
                {
                    text = "... log limit reached for today; further entries dropped" + Environment.NewLine;
                }

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process may hold the file for a moment, or the disk is full: a lost line is acceptable.
        }
    }

    /// <summary>Last lines of today's file (developer pages).</summary>
    public IReadOnlyList<string> Tail(int maxLines)
    {
        try
        {
            using var stream = new FileStream(CurrentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == maxLines)
                {
                    lines.Dequeue();
                }

                lines.Enqueue(line);
            }

            return [.. lines];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    private string PathFor(DateOnly day) =>
        Path.Combine(Directory, $"financeos-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    private void DeleteOldFiles(DateOnly today)
    {
        var oldest = today.AddDays(-(retentionDays - 1));
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "financeos-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["financeos-".Length..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

/// <summary><see cref="ILoggerProvider"/> writing redacted lines through one <see cref="FileLogWriter"/>.</summary>
public sealed class FileLoggerProvider(FileLogWriter writer, TimeProvider time, LogLevel minimum = LogLevel.Information) : ILoggerProvider
{
    public FileLogWriter Writer { get; } = writer;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= minimum;

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = new StringBuilder(160)
            .Append(time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelCode(level))
            .Append(' ').Append(category)
            .Append(" [").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append("] ")
            .Append(message.ReplaceLineEndings(" ⏎ "));

        if (exception is not null)
        {
            line.Append(Environment.NewLine).Append("    ").Append(
                LogRedactor.Scrub(exception.ToString()).ReplaceLineEndings(Environment.NewLine + "    "));
        }

        Writer.Write(line.ToString());
    }

    /// <summary>"FinanceOS.Data.Backup.BackupService" → "BackupService": the namespace adds nothing in one app.</summary>
    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private static string LevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}

internal sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        provider.Write(category, logLevel, Render(state, exception, formatter), exception);
    }

    /// <summary>
    /// Re-renders the message template with redacted values. Without a template (a plain string was
    /// logged) the whole text is scrubbed, since nothing tells which parts are content.
    /// </summary>
    internal static string Render<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values ||
            values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value is not string template)
        {
            return LogRedactor.Scrub(formatter(state, exception));
        }

        var lookup = values.Where(v => v.Key != "{OriginalFormat}").ToList();
        var result = new StringBuilder(template.Length + 32);
        var index = 0;
        var position = 0;
        while (position < template.Length)
        {
            var open = template.IndexOf('{', position);
            if (open < 0)
            {
                result.Append(LogRedactor.Scrub(template[position..]));
                break;
            }

            if (open + 1 < template.Length && template[open + 1] == '{')
            {
                result.Append(LogRedactor.Scrub(template[position..(open + 1)]));
                position = open + 2;
                continue;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                result.Append(LogRedactor.Scrub(template[position..]));
                break;
            }

            result.Append(LogRedactor.Scrub(template[position..open]));
            var hole = template[(open + 1)..close];
            var name = hole.Split(':', ',')[0];
            var format = hole.Contains(':', StringComparison.Ordinal) ? hole[(hole.IndexOf(':', StringComparison.Ordinal) + 1)..] : null;
            var value = lookup.FirstOrDefault(v => v.Key == name);
            if (value.Key is null && index < lookup.Count)
            {
                value = lookup[index];
            }

            result.Append(LogRedactor.Value(name, value.Value, format));
            index++;
            position = close + 1;
        }

        return result.ToString().Replace("}}", "}", StringComparison.Ordinal);
    }
}

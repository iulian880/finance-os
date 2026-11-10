using FinanceOS.Platform.Windows;
using FinanceOS.Platform.Windows.Logging;
using Microsoft.Extensions.Logging;

namespace FinanceOS.App.Services;

/// <summary>
/// Process-wide logging, created first thing at start-up (before DI, so start-up problems are logged too).
/// Files: <c>%LOCALAPPDATA%\FinanceOS\logs\financeos-yyyyMMdd.log</c>, redacted (ARCHITECTURE §11).
/// </summary>
public static class AppLog
{
    public static FileLogWriter Writer { get; } = new(AppPaths.LogsDirectory, TimeProvider.System);

    public static ILoggerFactory Factory { get; } = LoggerFactory.Create(builder => builder
        .SetMinimumLevel(MinimumLevel)
        .AddProvider(new FileLoggerProvider(Writer, TimeProvider.System, MinimumLevel)));

    /// <summary>Developer pages only (M0 spike window).</summary>
    public static ILogger Spike { get; } = Factory.CreateLogger("Spike");

#if DEBUG
    private static LogLevel MinimumLevel => LogLevel.Debug;
#else
    private static LogLevel MinimumLevel => LogLevel.Information;
#endif
}

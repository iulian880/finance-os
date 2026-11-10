namespace FinanceOS.Platform.Windows;

/// <summary>
/// Per-user data locations. Data never lives next to the executable, so replacing or
/// uninstalling the app cannot delete it (ARCHITECTURE §10).
/// </summary>
/// <remarks>
/// The installer (Velopack, M31) must use a different folder name (package id
/// <c>FinanceOS.Desktop</c> → <c>%LOCALAPPDATA%\FinanceOS.Desktop</c>), because uninstall
/// removes the install folder.
/// </remarks>
public static class AppPaths
{
    /// <summary>
    /// Environment variable that moves all data (database, backups, logs, preferences) to another
    /// folder: release smoke tests, a second profile. Ignored unless it is an absolute path.
    /// </summary>
    public const string DataRootVariable = "FINANCEOS_DATA_ROOT";

    public static string DefaultDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FinanceOS");

    public static string DataRoot { get; } = Resolve(Environment.GetEnvironmentVariable(DataRootVariable), DefaultDataRoot);

    /// <summary>False when <see cref="DataRootVariable"/> points somewhere else (a test or second data folder).</summary>
    public static bool IsDefaultDataRoot => string.Equals(DataRoot, DefaultDataRoot, StringComparison.OrdinalIgnoreCase);

    public static string LogsDirectory { get; } = Path.Combine(DataRoot, "logs");

    /// <summary>The override when it is an absolute path, otherwise the default folder.</summary>
    public static string Resolve(string? overridePath, string defaultRoot) =>
        !string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath.Trim().Trim('"'))
            ? Path.GetFullPath(overridePath.Trim().Trim('"'))
            : defaultRoot;
}

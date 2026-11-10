using System.IO;
using Microsoft.Win32;

namespace FinanceOS.App.Services;

/// <summary>"Start with Windows" (opt-in). Behind an interface for tests.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    /// <summary>Returns false when Windows refused (e.g. blocked by policy); the setting then stays as it was.</summary>
    bool Set(bool enabled);
}

/// <summary>
/// Per-user Run key (no admin rights): <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>,
/// value <c>FinanceOS</c> = <c>"…\FinanceOS.exe" --tray</c>. Starting in the tray keeps reminders
/// on time all day (ARCHITECTURE §6.3).
/// </summary>
public sealed class StartupRegistration(string exePath) : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FinanceOS";

    public string Command => $"\"{exePath}\" --tray";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}

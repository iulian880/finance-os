using System.Diagnostics;
using System.IO;
using FinanceOS.Data.Backup;

namespace FinanceOS.App.Services;

/// <summary>Data actions offered in Settings, behind an interface so ViewModels are tested without files.</summary>
public interface IDataMaintenance
{
    string DataFolder { get; }

    /// <summary>Newest usable backup (any kind except damaged copies), or null.</summary>
    DateTimeOffset? LastBackupUtc { get; }

    /// <summary>Manual backup on a worker thread. Throws <see cref="BackupException"/> on failure.</summary>
    Task<BackupInfo> BackupNowAsync();

    void OpenDataFolder();

    /// <summary>A backup was made (automatic or manual), possibly on a worker thread.</summary>
    event EventHandler? BackupsChanged;
}

public sealed class DataMaintenance(BackupService backups) : IDataMaintenance
{
    public event EventHandler? BackupsChanged
    {
        add => backups.Created += Forward(value);
        remove => backups.Created -= Forward(value);
    }

    public string DataFolder => backups.Location.Root;

    public DateTimeOffset? LastBackupUtc =>
        backups.List().FirstOrDefault(b => b.Kind != BackupKind.Damaged)?.CreatedUtc;

    public Task<BackupInfo> BackupNowAsync() => Task.Run(() => backups.Create(BackupKind.Manual));

    public void OpenDataFolder() => OpenInExplorer(backups.Location.Root);

    // Same wrapper instance per handler, so remove finds what add registered.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EventHandler, EventHandler<BackupInfo>> Wrappers = [];

    private static EventHandler<BackupInfo> Forward(EventHandler? handler) =>
        handler is null ? (_, _) => { } : Wrappers.GetValue(handler, h => (s, _) => h(s, EventArgs.Empty));

    internal static void OpenInExplorer(string folder)
    {
        Directory.CreateDirectory(folder);
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}

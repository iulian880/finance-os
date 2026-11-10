namespace FinanceOS.Data;

/// <summary>
/// Where the database and its backups live (ARCHITECTURE §10). The root is chosen by the composition
/// root (normally <c>%LOCALAPPDATA%\FinanceOS</c>); tests pass a temporary folder.
/// </summary>
public sealed class DataLocation(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string DataDirectory => Path.Combine(Root, "data");

    public string DatabasePath => Path.Combine(DataDirectory, "financeos.db");

    public string BackupsDirectory => Path.Combine(Root, "backups");

    /// <summary>Exists while the app has the database open. Found at start-up = the last session crashed.</summary>
    public string SessionMarkerPath => Path.Combine(DataDirectory, "session.open");

    /// <summary>The database plus its WAL companions, which belong to it and move with it.</summary>
    public IEnumerable<string> DatabaseFiles => [DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm"];
}

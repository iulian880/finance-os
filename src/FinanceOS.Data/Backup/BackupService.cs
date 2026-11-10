using Dapper;
using FinanceOS.Data.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinanceOS.Data.Backup;

/// <summary>A backup could not be made, or a backup file cannot be restored. Nothing was changed.</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Backups with <c>VACUUM INTO</c> (a consistent snapshot, safe while the app writes), restore and
/// quarantine of a damaged database (ARCHITECTURE §10). A backup only counts once it has been
/// re-opened and passed <c>quick_check</c>; until then it is a <c>.tmp</c> file that listing ignores.
/// </summary>
public sealed class BackupService(DataLocation location, TimeProvider time, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();

    public DataLocation Location { get; } = location;

    /// <summary>Raised after a backup was created and verified (on the thread that made it).</summary>
    public event EventHandler<BackupInfo>? Created;

    /// <summary>All recognised backups, newest first. Files with other names are ignored, never deleted.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(Location.BackupsDirectory))
        {
            return [];
        }

        return new DirectoryInfo(Location.BackupsDirectory).EnumerateFiles("financeos-*.db")
            .Select(f => BackupInfo.TryParse(f.FullName, f.Length))
            .OfType<BackupInfo>()
            .OrderByDescending(b => b.CreatedUtc)
            .ToList();
    }

    /// <summary>Snapshot of the current database. Throws <see cref="BackupException"/> if it cannot be made and verified.</summary>
    public BackupInfo Create(BackupKind kind)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Location.BackupsDirectory);
            var target = UniqueTarget(kind);
            var temp = target + ".tmp";

            try
            {
                DeleteIfExists(temp);
                using (var source = SqliteDatabase.OpenExisting(Location.DatabasePath, wal: true))
                {
                    source.Execute("VACUUM INTO @Path;", new { Path = temp });
                }

                // A backup is one self-contained file (no -wal beside it), verified before it counts.
                using (var copy = SqliteDatabase.OpenExisting(temp, wal: false))
                {
                    copy.ExecuteScalar<string>("PRAGMA journal_mode = DELETE;");
                    var problems = DatabaseInspector.Check(copy, IntegrityLevel.Quick);
                    if (problems.Count > 0)
                    {
                        throw new BackupException("The new backup failed its integrity check: " + string.Join("; ", problems));
                    }
                }

                File.Move(temp, target);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                DeleteIfExists(temp);
                _logger.LogError(ex, "Backup {Kind} failed", kind);
                throw new BackupException("The backup could not be created.", ex);
            }
            catch (BackupException)
            {
                DeleteIfExists(temp);
                throw;
            }

            var info = BackupInfo.TryParse(target, new FileInfo(target).Length)!;
            _logger.LogInformation("Backup {Kind} created ({SizeBytes} bytes)", kind, info.SizeBytes);
            Rotate();
            Created?.Invoke(this, info);
            return info;
        }
    }

    /// <summary>Creates today's automatic backup unless one already exists for the local date. Returns it, or null.</summary>
    public BackupInfo? EnsureDaily()
    {
        var zone = time.LocalTimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        var hasToday = List().Any(b => b.Kind == BackupKind.Daily &&
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(b.CreatedUtc, zone).DateTime) == today);
        return hasToday ? null : Create(BackupKind.Daily);
    }

    /// <summary>Deletes backups outside the retention policy. Returns how many were deleted.</summary>
    public int Rotate()
    {
        var deleted = 0;
        foreach (var backup in BackupRotation.SelectForDeletion(List(), time.LocalTimeZone))
        {
            try
            {
                File.Delete(backup.FilePath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept for now (e.g. open in a viewer); the next rotation tries again.
                _logger.LogWarning(ex, "Could not delete old backup {Kind}", backup.Kind);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Replaces the current database with <paramref name="backup"/>. The current state is backed up first
    /// (or, if it is too damaged to back up, kept as a raw "damaged" copy), so a restore never loses data.
    /// The caller must have closed every connection and runs start-up again afterwards (migrations, checks).
    /// </summary>
    public void Restore(BackupInfo backup, int supportedVersion)
    {
        lock (_gate)
        {
            var health = DatabaseInspector.Inspect(backup.FilePath);
            if (!health.IsHealthy)
            {
                throw new BackupException($"The backup cannot be used: {health.Problem}.");
            }

            if (health.SchemaVersion > supportedVersion)
            {
                throw new DatabaseTooNewException(health.SchemaVersion, supportedVersion);
            }

            if (File.Exists(Location.DatabasePath))
            {
                try
                {
                    Create(BackupKind.PreRestore);
                }
                catch (BackupException)
                {
                    SaveDamagedCopy();
                }
            }

            ReplaceDatabaseWith(backup.FilePath);
            _logger.LogWarning("Database restored from backup {Kind} of {CreatedUtc:O}", backup.Kind, backup.CreatedUtc);
        }
    }

    /// <summary>
    /// "Start with an empty database": keeps the damaged file as a backup of kind Damaged, then removes it,
    /// so the next start-up creates a new database. Nothing is deleted without a copy.
    /// </summary>
    public BackupInfo? QuarantineCurrent()
    {
        lock (_gate)
        {
            var copy = SaveDamagedCopy();
            foreach (var file in Location.DatabaseFiles)
            {
                DeleteIfExists(file);
            }

            _logger.LogWarning("Damaged database moved aside; a new one will be created");
            return copy;
        }
    }

    private BackupInfo? SaveDamagedCopy()
    {
        if (!File.Exists(Location.DatabasePath))
        {
            return null;
        }

        Directory.CreateDirectory(Location.BackupsDirectory);
        var target = UniqueTarget(BackupKind.Damaged);
        File.Copy(Location.DatabasePath, target);
        var wal = Location.DatabasePath + "-wal";
        if (File.Exists(wal))
        {
            File.Copy(wal, target + "-wal"); // recent commits may live only here
        }

        _logger.LogWarning("Raw copy of the damaged database kept as backup {Kind}", BackupKind.Damaged);
        return BackupInfo.TryParse(target, new FileInfo(target).Length);
    }

    private void ReplaceDatabaseWith(string source)
    {
        Directory.CreateDirectory(Location.DataDirectory);
        var temp = Location.DatabasePath + ".restore";
        File.Copy(source, temp, overwrite: true);

        // The old WAL belongs to the old file: left in place, SQLite would replay it onto the restored one.
        DeleteIfExists(Location.DatabasePath + "-wal");
        DeleteIfExists(Location.DatabasePath + "-shm");
        File.Move(temp, Location.DatabasePath, overwrite: true);
    }

    private string UniqueTarget(BackupKind kind)
    {
        var at = time.GetUtcNow();
        string target;
        while (File.Exists(target = Path.Combine(Location.BackupsDirectory, BackupInfo.FileNameFor(kind, at))))
        {
            at = at.AddMilliseconds(1);
        }

        return target;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

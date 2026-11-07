using Microsoft.Data.Sqlite;

namespace EgyChina.Pos.Data;

public sealed record BackupResult(
    bool Created,
    string? Path,
    int RemovedOld,
    string? Error);

/// <summary>
/// Consistent SQLite snapshots via the backup API. Never copies <c>device.cred</c>.
/// </summary>
public static class DatabaseBackup
{
    public const int DefaultKeepCount = 14;

    public static BackupResult Create(
        PosDatabase database,
        string? backupDirectory = null,
        int keepCount = DefaultKeepCount,
        DateTimeOffset? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (keepCount < 1)
            throw new ArgumentOutOfRangeException(nameof(keepCount));

        var directory = string.IsNullOrWhiteSpace(backupDirectory)
            ? PosPaths.BackupDirectory()
            : backupDirectory;
        Directory.CreateDirectory(directory);

        var when = stamp ?? DateTimeOffset.Now;
        var fileName = "pos-" + when.ToString("yyyyMMdd-HHmmss") + ".db";
        var destination = Path.Combine(directory, fileName);

        try
        {
            database.BackupTo(destination);
            var removed = Prune(directory, keepCount);
            WriteSettingSafe(database, when, destination);
            PosLog.Info($"backup created path={destination} removedOld={removed}");
            return new BackupResult(true, destination, removed, null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PosLog.Error("backup failed", ex);
            return new BackupResult(false, null, 0, ex.Message);
        }
    }

    public static int Prune(string backupDirectory, int keepCount = DefaultKeepCount)
    {
        if (!Directory.Exists(backupDirectory))
            return 0;

        var files = Directory.EnumerateFiles(backupDirectory, "pos-*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();

        var removed = 0;
        for (var i = keepCount; i < files.Count; i++)
        {
            try
            {
                files[i].Delete();
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static void WriteSettingSafe(PosDatabase database, DateTimeOffset when, string path)
    {
        try
        {
            database.WriteSetting("backup.last_at", when.ToString("O"));
            database.WriteSetting("backup.last_path", path);
        }
        catch (Exception ex)
        {
            PosLog.Error("backup setting write failed", ex);
        }
    }
}

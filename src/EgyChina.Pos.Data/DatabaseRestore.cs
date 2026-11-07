using Microsoft.Data.Sqlite;

namespace EgyChina.Pos.Data;

public sealed record RestoreResult(
    bool Restored,
    string? SafetyCopyPath,
    string? Error);

/// <summary>
/// Replaces the live SQLite file from a Phase-9 backup. Requires the POS process to be stopped.
/// Never touches <c>device.cred</c>.
/// </summary>
public static class DatabaseRestore
{
    public static RestoreResult RestoreFromBackup(
        string backupPath,
        string liveDatabasePath,
        bool requireNoOpenShift = true)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            return new RestoreResult(false, null, "ملف النسخة الاحتياطية غير موجود.");
        if (string.IsNullOrWhiteSpace(liveDatabasePath))
            return new RestoreResult(false, null, "مسار قاعدة البيانات مطلوب.");

        var fullBackup = Path.GetFullPath(backupPath);
        var fullLive = Path.GetFullPath(liveDatabasePath);
        if (string.Equals(fullBackup, fullLive, StringComparison.OrdinalIgnoreCase))
            return new RestoreResult(false, null, "لا يمكن الاسترجاع من نفس ملف القاعدة الحي.");

        try
        {
            if (!LooksLikePosDatabase(fullBackup))
                return new RestoreResult(false, null, "ملف النسخة ليس قاعدة POS صالحة.");

            if (requireNoOpenShift && File.Exists(fullLive) && HasOpenShift(fullLive))
                return new RestoreResult(false, null, "توجد وردية مفتوحة. أغلقها أو أوقف الاسترجاع.");

            // Drop pooled handles so the live file can be replaced while tests/tools still hold types.
            SqliteConnection.ClearAllPools();

            string? safety = null;
            if (File.Exists(fullLive))
            {
                safety = fullLive + ".pre-restore-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(fullLive, safety, overwrite: false);
                TryDeleteSidecars(fullLive);
                File.Delete(fullLive);
            }

            var liveDir = Path.GetDirectoryName(fullLive);
            if (!string.IsNullOrEmpty(liveDir))
                Directory.CreateDirectory(liveDir);

            File.Copy(fullBackup, fullLive, overwrite: false);
            TryDeleteSidecars(fullLive);

            if (!LooksLikePosDatabase(fullLive))
            {
                if (safety is not null && File.Exists(safety))
                {
                    File.Copy(safety, fullLive, overwrite: true);
                    return new RestoreResult(false, safety, "فشل التحقق بعد النسخ؛ أُعيد الملف السابق.");
                }

                return new RestoreResult(false, safety, "فشل التحقق بعد النسخ.");
            }

            PosLog.Info($"database restored from={fullBackup} safety={safety}");
            return new RestoreResult(true, safety, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            PosLog.Error("database restore failed", ex);
            return new RestoreResult(false, null, ex.Message);
        }
    }

    public static bool HasOpenShift(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM shift WHERE status = 'open';
            """;
        try
        {
            return (long)command.ExecuteScalar()! > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public static bool LooksLikePosDatabase(string databasePath)
    {
        try
        {
            using var connection = OpenReadOnly(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name IN ('schema_migration', 'product', 'sale', 'outbox');
                """;
            return (long)command.ExecuteScalar()! >= 4;
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void TryDeleteSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = databasePath + suffix;
            try
            {
                if (File.Exists(side))
                    File.Delete(side);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

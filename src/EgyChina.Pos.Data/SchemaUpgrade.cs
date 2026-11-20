namespace EgyChina.Pos.Data;

/// <summary>
/// Startup schema upgrade. Takes a backup when the file is behind
/// <see cref="PosDatabase.LatestSchemaVersion"/>, applies migrations inside
/// <see cref="PosDatabase.Initialize()"/>, and on failure restores that backup
/// so the cashier still opens on the previous schema.
/// </summary>
public static class SchemaUpgrade
{
    public static void ApplyKeepingPreviousSchema(PosDatabase database, string backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);

        string? backupPath = null;
        if (File.Exists(database.DatabasePath) && database.HasPendingMigrations())
        {
            var backup = DatabaseBackup.Create(database, backupDirectory);
            if (backup.Created && !string.IsNullOrWhiteSpace(backup.Path))
            {
                backupPath = backup.Path;
                PosLog.Info($"pre-migration backup path={backup.Path}");
            }
            else
            {
                PosLog.Error($"pre-migration backup skipped: {LogRedactor.Redact(backup.Error)}");
            }
        }

        try
        {
            database.Initialize();
        }
        catch (Exception ex)
        {
            PosLog.Error("migration failed", ex);
            if (backupPath is not null)
            {
                var restored = DatabaseRestore.RestoreFromBackup(
                    backupPath,
                    database.DatabasePath,
                    requireNoOpenShift: false);
                if (restored.Restored)
                    PosLog.Info($"migration restored backup path={backupPath}");
                else
                    PosLog.Error($"migration restore failed: {LogRedactor.Redact(restored.Error)}");
            }

            if (PosDatabase.PeekSchemaVersion(database.DatabasePath) < 1)
                throw;

            database.Initialize(applyMigrations: false);
        }
    }
}

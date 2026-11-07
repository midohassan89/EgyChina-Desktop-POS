using EgyChina.Pos.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class BackupAndLogTests : IDisposable
{
    private readonly string _directory;

    public BackupAndLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "egychina-pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        PosLog.SetDirectory(Path.Combine(_directory, "logs"));
    }

    public void Dispose()
    {
        PosLog.Reset();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void LogRedactor_strips_bearer_token_and_named_secrets()
    {
        var redacted = LogRedactor.Redact(
            "Authorization: Bearer super-secret-device-token-value password=hunter2 pin=246810 EGYCHINA_POS_DEVICE_TOKEN=abc123");

        Assert.DoesNotContain("super-secret-device-token-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("246810", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void PosLog_redacts_secrets_on_write()
    {
        PosLog.Info("sync Authorization: Bearer tok_should_never_land");
        var logPath = Path.Combine(_directory, "logs", "pos.log");
        Assert.True(File.Exists(logPath));
        var text = File.ReadAllText(logPath);
        Assert.DoesNotContain("tok_should_never_land", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PosLog_purges_files_older_than_retention()
    {
        var logs = Path.Combine(_directory, "logs");
        var stale = Path.Combine(logs, "pos-20200101-010101.log");
        File.WriteAllText(stale, "old");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-20));

        var removed = PosLog.PurgeOldFiles(DateTimeOffset.UtcNow);
        Assert.True(removed >= 1);
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void DatabaseBackup_creates_restorable_copy_and_prunes()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        Assert.Equal(3, database.GetSchemaVersion());

        var backups = Path.Combine(_directory, "backups");
        var first = DatabaseBackup.Create(
            database,
            backups,
            keepCount: 100,
            stamp: new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        Assert.True(first.Created, first.Error);
        Assert.True(File.Exists(first.Path));

        Assert.True(DatabaseBackup.Create(
            database,
            backups,
            keepCount: 100,
            stamp: new DateTimeOffset(2026, 11, 7, 11, 0, 0, TimeSpan.Zero)).Created);
        var third = DatabaseBackup.Create(
            database,
            backups,
            keepCount: 100,
            stamp: new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        Assert.True(third.Created, third.Error);
        Assert.Equal(3, Directory.GetFiles(backups, "pos-*.db").Length);

        var pruned = DatabaseBackup.Prune(backups, keepCount: 2);
        Assert.Equal(1, pruned);
        Assert.Equal(2, Directory.GetFiles(backups, "pos-*.db").Length);
        Assert.NotNull(database.GetSetting("backup.last_at"));
        Assert.EndsWith("pos-20261107-120000.db", database.GetSetting("backup.last_path")!, StringComparison.Ordinal);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = third.Path!,
            Mode = SqliteOpenMode.ReadOnly,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM product";
        Assert.Equal(3L, (long)command.ExecuteScalar()!);
        Assert.False(File.Exists(Path.Combine(backups, "device.cred")));
    }

    [Fact]
    public void DatabaseBackup_rejects_live_path_as_destination()
    {
        var path = Path.Combine(_directory, "live.db");
        var database = new PosDatabase(path);
        database.Initialize();
        Assert.Throws<InvalidOperationException>(() => database.BackupTo(path));
    }
}

using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

[Collection("PosLog")]
public sealed class PackagingAndRestoreTests : IDisposable
{
    private readonly string _directory;

    public PackagingAndRestoreTests()
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
    public void PosInstallPaths_rejects_traversal_in_version()
    {
        Assert.Throws<ArgumentException>(() => PosInstallPaths.VersionDirectory(@"..\evil"));
        Assert.Throws<ArgumentException>(() => PosInstallPaths.VersionDirectory("a/b"));
        var path = PosInstallPaths.VersionDirectory("0.1.0", _directory);
        Assert.Equal(Path.Combine(_directory, "app", "0.1.0"), path);
    }

    [Fact]
    public void InstallLayout_tracks_previous_version_pointer()
    {
        InstallLayout.EnsureRoot(_directory);
        InstallLayout.WritePreviousVersion(_directory, "0.1.0");
        Assert.Equal("0.1.0", InstallLayout.ReadPreviousVersion(_directory));

        var versionDir = PosInstallPaths.VersionDirectory("0.1.0", _directory);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(PosInstallPaths.VersionExe("0.1.0", _directory), "stub");
        Assert.True(InstallLayout.VersionLooksInstalled(_directory, "0.1.0"));
    }

    [Fact]
    public void UpdateGate_blocks_running_process_or_open_shift()
    {
        Assert.False(UpdateGate.CanApplyUpdate(posProcessRunning: true, hasOpenShift: false));
        Assert.False(UpdateGate.CanApplyUpdate(posProcessRunning: false, hasOpenShift: true));
        Assert.True(UpdateGate.CanApplyUpdate(posProcessRunning: false, hasOpenShift: false));
        Assert.Contains("وردية", UpdateGate.BlockReason(false, true), StringComparison.Ordinal);
    }

    [Fact]
    public void AppHeartbeat_roundtrips_and_matches_fresh_version()
    {
        var dataRoot = Path.Combine(_directory, "data");
        var now = DateTimeOffset.Parse("2026-11-07T10:00:00Z");
        AppHeartbeat.Write(dataRoot, "0.1.0", now);
        var payload = AppHeartbeat.TryRead(dataRoot);
        Assert.NotNull(payload);
        Assert.Equal("0.1.0", payload.Version);
        Assert.True(AppHeartbeat.MatchesVersion(payload, "0.1.0", TimeSpan.FromMinutes(2), now.AddSeconds(30)));
        Assert.False(AppHeartbeat.MatchesVersion(payload, "0.2.0", TimeSpan.FromMinutes(2), now.AddSeconds(30)));
        Assert.False(AppHeartbeat.MatchesVersion(payload, "0.1.0", TimeSpan.FromSeconds(10), now.AddMinutes(5)));
    }

    [Fact]
    public void DatabaseRestore_replaces_live_db_and_keeps_safety_copy()
    {
        var live = Path.Combine(_directory, "pos.db");
        var liveDb = new PosDatabase(live);
        liveDb.Initialize();
        liveDb.WriteSetting("marker", "live");

        var backupDir = Path.Combine(_directory, "backups");
        var backup = DatabaseBackup.Create(liveDb, backupDir, keepCount: 5);
        Assert.True(backup.Created, backup.Error);

        liveDb.WriteSetting("marker", "changed-after-backup");
        Assert.Equal("changed-after-backup", liveDb.GetSetting("marker"));

        // Dispose gate by not holding connections — Backup/restore use their own.
        var result = DatabaseRestore.RestoreFromBackup(backup.Path!, live, requireNoOpenShift: true);
        Assert.True(result.Restored, result.Error);
        Assert.NotNull(result.SafetyCopyPath);
        Assert.True(File.Exists(result.SafetyCopyPath));

        var restored = new PosDatabase(live);
        restored.Initialize();
        Assert.Equal("live", restored.GetSetting("marker"));
    }

    [Fact]
    public void DatabaseRestore_refuses_when_shift_is_open()
    {
        var live = Path.Combine(_directory, "live.db");
        var database = new PosDatabase(live);
        database.Initialize();
        TestShift.Open(database);

        var backupDir = Path.Combine(_directory, "backups");
        var backup = DatabaseBackup.Create(database, backupDir);
        Assert.True(backup.Created, backup.Error);

        var result = DatabaseRestore.RestoreFromBackup(backup.Path!, live, requireNoOpenShift: true);
        Assert.False(result.Restored);
        Assert.Contains("وردية", result.Error!, StringComparison.Ordinal);
    }
}

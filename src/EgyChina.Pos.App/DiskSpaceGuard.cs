using EgyChina.Pos.Data;

namespace EgyChina.Pos.App;

/// <summary>
/// Soft gate: warn (and optionally block new sales) when the SQLite volume is nearly full.
/// </summary>
internal static class DiskSpaceGuard
{
    public const long WarnBytes = 500L * 1024 * 1024;
    public const long BlockBytes = 200L * 1024 * 1024;

    public static DiskSpaceStatus Check(string databasePath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(databasePath));
            if (string.IsNullOrWhiteSpace(root))
                return DiskSpaceStatus.Unknown;

            var info = new DriveInfo(root);
            if (!info.IsReady)
                return DiskSpaceStatus.Unknown;

            var free = info.AvailableFreeSpace;
            if (free < BlockBytes)
                return new DiskSpaceStatus(true, true, free, "القرص ممتلئ تقريباً. أوقف المبيعات الجديدة واطبع الإيصالات المحفوظة فقط.");
            if (free < WarnBytes)
                return new DiskSpaceStatus(true, false, free, "مساحة القرص منخفضة. انقل النسخ الاحتياطية أو حرّر مساحة قريباً.");
            return new DiskSpaceStatus(false, false, free, null);
        }
        catch (Exception ex)
        {
            PosLog.Error("disk space check failed", ex);
            return DiskSpaceStatus.Unknown;
        }
    }
}

internal readonly record struct DiskSpaceStatus(bool HasWarning, bool BlockSales, long FreeBytes, string? Message)
{
    public static DiskSpaceStatus Unknown { get; } = new(false, false, -1, null);
}

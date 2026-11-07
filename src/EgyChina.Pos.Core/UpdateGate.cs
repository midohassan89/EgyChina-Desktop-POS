namespace EgyChina.Pos.Core;

/// <summary>
/// Local rules for applying a till binary update. ERP release feed is out of scope until it exists.
/// </summary>
public static class UpdateGate
{
    public static bool CanApplyUpdate(bool posProcessRunning, bool hasOpenShift) =>
        !posProcessRunning && !hasOpenShift;

    public static string BlockReason(bool posProcessRunning, bool hasOpenShift)
    {
        if (posProcessRunning)
            return "أغلق برنامج نقطة البيع قبل التحديث.";
        if (hasOpenShift)
            return "أغلق الوردية المفتوحة قبل التحديث.";
        return string.Empty;
    }
}

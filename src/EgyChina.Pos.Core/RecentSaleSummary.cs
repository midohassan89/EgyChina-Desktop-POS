namespace EgyChina.Pos.Core;

public enum SaleSyncTone
{
    Pending = 0,
    Synced = 1,
    Failed = 2,
}

/// <summary>One of the last completed till sales for the cashier status strip.</summary>
public sealed record RecentSaleSummary(
    string SaleId,
    int ReceiptNo,
    long TotalMinor,
    SaleSyncTone SyncTone)
{
    public string AmountText => Money.FormatAmount(Math.Abs(TotalMinor));

    public string AmountWithCurrencyText => Money.FormatEgp(Math.Abs(TotalMinor));

    public bool IsSynced => SyncTone == SaleSyncTone.Synced;

    public bool IsPending => SyncTone == SaleSyncTone.Pending;

    public bool IsFailed => SyncTone == SaleSyncTone.Failed;

    public string SyncToolTip => SyncTone switch
    {
        SaleSyncTone.Synced => "متزامنة مع السيرفر",
        SaleSyncTone.Failed => "فشل الرفع — راجع صندوق الصادر",
        _ => "بانتظار المزامنة",
    };
}

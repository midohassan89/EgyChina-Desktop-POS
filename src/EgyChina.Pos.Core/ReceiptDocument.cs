namespace EgyChina.Pos.Core;

/// <summary>
/// Receipt payload for the ESC/POS raster printer. Rendered as a bitmap — never as a code page.
/// </summary>
public sealed record ReceiptLine(
    string NameAr,
    string? NameZh,
    long QtyMilli,
    long UnitPriceMinor,
    long LineTotalMinor);

public sealed record ReceiptDocument(
    string StoreNameAr,
    string StoreNameZh,
    int ReceiptNo,
    DateTimeOffset CompletedAt,
    string CashierName,
    IReadOnlyList<ReceiptLine> Lines,
    long TotalMinor,
    long TenderMinor,
    long ChangeMinor,
    string FooterAr,
    bool IsReprint = false,
    bool KickDrawer = false);

/// <summary>Z-report payload for thermal print at shift close.</summary>
public sealed record ZReportDocument(
    string StoreNameAr,
    string StoreNameZh,
    string CashierName,
    string ShiftOpenedAt,
    DateTimeOffset ClosedAt,
    long OpeningFloatMinor,
    long CashSalesMinor,
    long VisaSalesMinor,
    long WalletSalesMinor,
    long InstaPaySalesMinor,
    long WeChatSalesMinor,
    long CashRefundsMinor,
    long TotalSalesMinor,
    long ExpectedCashMinor,
    long ActualClosingCashMinor,
    long VarianceMinor,
    int TicketCount,
    int ReturnTicketCount);

public interface IReceiptPrinter
{
    /// <summary>True when a real transport is configured (not a silent null sink).</summary>
    bool IsConfigured { get; }

    Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken);

    /// <summary>Prints the end-of-shift Z report (sales totals + drawer variance).</summary>
    Task PrintZReportAsync(ZReportDocument document, CancellationToken cancellationToken);

    /// <summary>Pulse the cash drawer without printing a receipt.</summary>
    Task KickDrawerAsync(CancellationToken cancellationToken);
}

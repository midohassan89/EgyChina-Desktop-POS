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

public interface IReceiptPrinter
{
    /// <summary>True when a real transport is configured (not a silent null sink).</summary>
    bool IsConfigured { get; }

    Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken);
}

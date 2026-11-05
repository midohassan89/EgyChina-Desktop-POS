namespace EgyChina.Pos.Core;

/// <summary>
/// Seam for the phase-1 raster ESC/POS printer. The shell does not print.
/// </summary>
public sealed record ReceiptLine(string NameAr, long QtyMilli, long UnitPriceMinor, long LineTotalMinor);

public sealed record ReceiptDocument(
    string StoreNameAr,
    int ReceiptNo,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ReceiptLine> Lines,
    long TotalMinor,
    string FooterAr);

public interface IReceiptPrinter
{
    Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken);
}

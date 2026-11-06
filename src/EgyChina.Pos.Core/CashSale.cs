namespace EgyChina.Pos.Core;

/// <summary>
/// One cart line at the price the cashier saw. Quantity is a whole number; stored as milli-units.
/// </summary>
public sealed record CashSaleLine(
    string ProductId,
    string? Barcode,
    string NameAr,
    int Qty,
    long UnitPriceMinor)
{
    public long LineTotalMinor => checked(UnitPriceMinor * Qty);

    public long QtyMilli => checked((long)Qty * 1000);
}

/// <summary>
/// A committed offline cash sale. The outbox row uses <see cref="IdempotencyKey"/> and is not posted here.
/// </summary>
public sealed record CompletedCashSale(
    string SaleId,
    string TerminalId,
    string CashierId,
    int ReceiptNo,
    long TotalMinor,
    long TenderMinor,
    long ChangeMinor,
    string IdempotencyKey);

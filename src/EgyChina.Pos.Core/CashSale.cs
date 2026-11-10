namespace EgyChina.Pos.Core;

/// <summary>
/// One cart line at the price the cashier saw. Quantity is a whole number; stored as milli-units.
/// DiscountMinor is the line discount already converted to piasters.
/// </summary>
public sealed record CashSaleLine(
    string ProductId,
    string? Barcode,
    string NameAr,
    int Qty,
    long UnitPriceMinor,
    long DiscountMinor = 0)
{
    public long LineTotalMinor
    {
        get
        {
            var gross = checked(UnitPriceMinor * Qty);
            var disc = Math.Clamp(DiscountMinor, 0, gross);
            return checked(gross - disc);
        }
    }

    public long QtyMilli => checked((long)Qty * 1000);
}

/// <summary>
/// A committed offline cash sale. The outbox row uses <see cref="IdempotencyKey"/> and is not posted here.
/// </summary>
public sealed record CompletedCashSale(
    string SaleId,
    string TerminalId,
    string CashierId,
    string ShiftId,
    int ReceiptNo,
    long TotalMinor,
    long TenderMinor,
    long ChangeMinor,
    string IdempotencyKey);

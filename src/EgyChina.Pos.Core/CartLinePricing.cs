namespace EgyChina.Pos.Core;

/// <summary>Integer money helpers for cart line totals after optional discount.</summary>
public static class CartLinePricing
{
    /// <summary>
    /// For <see cref="LineDiscountKind.Percent"/>, <paramref name="discountValue"/> is whole percent (0–100).
    /// For <see cref="LineDiscountKind.Flat"/>, <paramref name="discountValue"/> is EGP piasters.
    /// </summary>
    public static long ComputeDiscountMinor(
        int qty,
        long unitPriceMinor,
        LineDiscountKind kind,
        long discountValue)
    {
        if (qty < 1 || unitPriceMinor < 0 || discountValue <= 0 || kind == LineDiscountKind.None)
            return 0;

        var subtotal = checked(unitPriceMinor * (long)qty);
        return kind switch
        {
            LineDiscountKind.Percent =>
                checked((subtotal * Math.Clamp(discountValue, 0, 100) + 50) / 100),
            LineDiscountKind.Flat =>
                Math.Min(subtotal, discountValue),
            _ => 0,
        };
    }

    public static long ComputeLineTotalMinor(
        int qty,
        long unitPriceMinor,
        LineDiscountKind kind,
        long discountValue)
    {
        var subtotal = checked(Math.Max(0, unitPriceMinor) * (long)Math.Max(0, qty));
        var discount = ComputeDiscountMinor(qty, unitPriceMinor, kind, discountValue);
        return checked(subtotal - discount);
    }

    public static string? ToStoredKind(LineDiscountKind kind) =>
        kind switch
        {
            LineDiscountKind.Percent => "percent",
            LineDiscountKind.Flat => "flat",
            _ => null,
        };

    public static LineDiscountKind ParseStoredKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "percent" => LineDiscountKind.Percent,
            "flat" => LineDiscountKind.Flat,
            _ => LineDiscountKind.None,
        };
}

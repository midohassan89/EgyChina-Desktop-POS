namespace EgyChina.Pos.Core;

/// <summary>
/// Minor-unit money. EGP piasters and CNY fen. No binary floating point.
/// </summary>
public static class Money
{
    public static string FormatEgp(long minor)
    {
        var negative = minor < 0;
        var abs = negative ? -minor : minor;
        var number = $"{(negative ? "-" : string.Empty)}{abs / 100}.{abs % 100:00}";
        // Isolates keep the Western digits and "ج.م" from reversing each other.
        return $"\u2066{number}\u2069\u00A0\u2067ج.م\u2069";
    }

    /// <summary>
    /// Half-up on the absolute value, then restore the sign.
    /// <c>sign * ((abs * numerator + denominator / 2) / denominator)</c>.
    /// The ERP must use this same definition. See docs/erp-sync-contract.md.
    /// </summary>
    public static long ApplyRate(long amountMinor, long numerator, long denominator)
    {
        if (denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), "Rate denominator must be positive.");
        if (numerator < 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "Rate numerator must be zero or positive.");

        var negative = amountMinor < 0;
        var abs = negative ? -amountMinor : amountMinor;
        checked
        {
            var rounded = (abs * numerator + denominator / 2) / denominator;
            return negative ? -rounded : rounded;
        }
    }
}

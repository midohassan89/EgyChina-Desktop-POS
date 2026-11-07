namespace EgyChina.Pos.Core;

/// <summary>Egyptian mobile loyalty card scans (same rules as ERP <c>loyaltyScan.ts</c>).</summary>
public static class LoyaltyScan
{
    /// <summary>Digits only. A 12-digit 201… code becomes the local 01… mobile.</summary>
    public static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        // Country code 20 + national 1xxxxxxxxx → local 01xxxxxxxxx (11 digits).
        if (digits.StartsWith("201", StringComparison.Ordinal) && digits.Length == 12)
            digits = "0" + digits[2..];
        return digits;
    }

    /// <summary>Loyalty card: Egyptian mobile, exactly 11 digits starting with 01.</summary>
    public static bool IsLoyaltyPhone(string? raw)
    {
        var code = NormalizePhone(raw);
        return code.StartsWith("01", StringComparison.Ordinal) && code.Length == 11;
    }

    /// <summary>1 EGP discount per 1000 points, capped by cart major pounds.</summary>
    public static long DiscountMinorFromPoints(int pointsBalance, long cartSubtotalMinor)
    {
        if (pointsBalance < 1000 || cartSubtotalMinor <= 0)
            return 0;
        var maxByPoints = pointsBalance / 1000;
        var maxByCart = (int)(cartSubtotalMinor / 100);
        var egp = Math.Min(maxByPoints, maxByCart);
        return egp * 100L;
    }

    public static int PointsRedeemedForDiscount(long discountMinor) =>
        (int)(discountMinor / 100) * 1000;

    /// <summary>10 points per payable EGP (floor), matching web POS.</summary>
    public static int PointsEarnedForPayable(long payableMinor) =>
        (int)(Math.Max(0, payableMinor) / 100) * 10;
}

public sealed record LoyaltyCustomer(
    string Id,
    string? Name,
    string Phone,
    int PointsBalance);

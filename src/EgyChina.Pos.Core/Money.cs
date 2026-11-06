namespace EgyChina.Pos.Core;

/// <summary>
/// Minor-unit money. EGP piasters and CNY fen. No binary floating point.
/// </summary>
public static class Money
{
    public static string FormatAmount(long minor)
    {
        var negative = minor < 0;
        var abs = negative ? -minor : minor;
        var number = $"{abs / 100}.{abs % 100:00}";
        return negative ? "-" + number : number;
    }

    public static string FormatEgp(long minor)
    {
        var number = FormatAmount(minor);
        // Isolates keep the Western digits and "ج.م" from reversing each other.
        return $"\u2066{number}\u2069\u00A0\u2067ج.م\u2069";
    }

    /// <summary>
    /// Parses a Western-digit pound amount ("200", "200.5", "200.50") into piasters.
    /// More than two decimal places is rejected. No floats.
    /// </summary>
    public static bool TryParseEgp(string? text, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var source = text.Trim();
        var dot = source.IndexOf('.');
        if (dot != source.LastIndexOf('.'))
            return false;

        var whole = dot < 0 ? source : source[..dot];
        var fraction = dot < 0 ? string.Empty : source[(dot + 1)..];
        if (fraction.Length > 2)
            return false;
        if (whole.Length == 0)
            whole = "0";
        if (!whole.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit))
            return false;

        if (!long.TryParse(whole, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major))
            return false;

        var cents = fraction.Length switch
        {
            0 => 0,
            1 => (fraction[0] - '0') * 10,
            _ => (fraction[0] - '0') * 10 + (fraction[1] - '0'),
        };

        try
        {
            minor = checked(major * 100 + cents);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
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

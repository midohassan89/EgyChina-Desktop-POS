namespace EgyChina.Pos.Core;

/// <summary>
/// In-store scale labels matching ERP <c>resolveBarcode.ts</c>:
/// 20 + 5-digit PLU + 5-digit price (piasters) + check digit.
/// </summary>
public static class ScaleBarcode
{
    public const string Prefix = "20";

    public static bool IsScaleBarcode(string? code)
    {
        var digits = DigitsOnly(code);
        return digits.Length == 13 && digits.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public static bool TryParse(string? code, out ScaleBarcodeParse parse)
    {
        parse = default!;
        var digits = DigitsOnly(code);
        if (digits.Length != 13 || !digits.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var plu = digits.Substring(2, 5);
        if (!long.TryParse(digits.AsSpan(7, 5), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pricePiasters)
            || pricePiasters <= 0)
        {
            return false;
        }

        parse = new ScaleBarcodeParse(digits, plu, pricePiasters);
        return true;
    }

    public static string DigitsOnly(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;
        Span<char> buffer = stackalloc char[code.Length];
        var n = 0;
        foreach (var c in code)
        {
            if (c is >= '0' and <= '9')
                buffer[n++] = c;
        }

        return n == 0 ? string.Empty : new string(buffer[..n]);
    }
}

public sealed record ScaleBarcodeParse(string Digits, string Plu, long EmbeddedTotalMinor);

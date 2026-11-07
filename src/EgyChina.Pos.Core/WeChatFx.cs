namespace EgyChina.Pos.Core;

/// <summary>
/// WeChat helper: invoice stays EGP; CNY is a display conversion.
/// Matches web checkout: default 7 EGP/CNY and a 4% safety haircut on live rates.
/// </summary>
public static class WeChatFx
{
    public const decimal DefaultEgpPerCny = 7m;
    public const decimal SafetyMargin = 0.04m;

    public static decimal ApplySafetyMargin(decimal rawEgpPerCny)
    {
        if (rawEgpPerCny <= 0)
            throw new ArgumentOutOfRangeException(nameof(rawEgpPerCny));
        var safe = rawEgpPerCny - (rawEgpPerCny * SafetyMargin);
        return decimal.Round(safe, 2, MidpointRounding.AwayFromZero);
    }

    public static bool TryParseRate(string? text, out decimal egpPerCny)
    {
        egpPerCny = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!decimal.TryParse(
                text.Trim(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rate))
        {
            return false;
        }

        if (rate <= 0)
            return false;
        egpPerCny = decimal.Round(rate, 4, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>CNY major units for an EGP minor total at <paramref name="egpPerCny"/>.</summary>
    public static decimal EgpMinorToCnyMajor(long egpMinor, decimal egpPerCny)
    {
        if (egpPerCny <= 0)
            throw new ArgumentOutOfRangeException(nameof(egpPerCny));
        var egpMajor = egpMinor / 100m;
        return decimal.Round(egpMajor / egpPerCny, 2, MidpointRounding.AwayFromZero);
    }

    public static string FormatCny(decimal cnyMajor) =>
        "¥ " + cnyMajor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

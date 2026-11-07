using System.Linq;
using System.Text.RegularExpressions;

namespace EgyChina.Pos.Core;

/// <summary>
/// Shelf-facing labels: drop barcode-lookup junk (Buycott / UPC Lookup / bare EANs).
/// </summary>
public static partial class ProductDisplayNames
{
    private static readonly Regex JunkPattern = JunkRegex();
    private static readonly Regex MostlyDigits = DigitsRegex();

    public static bool LooksSuspicious(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var trimmed = Collapse(name);
        if (trimmed.Length < 2)
            return true;
        if (JunkPattern.IsMatch(trimmed))
            return true;
        if (MostlyDigits.IsMatch(trimmed) && trimmed.Length >= 8)
            return true;
        if (trimmed.Contains("buycott", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.Contains("upc lookup", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.Contains("ean harvest", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Best customer/cashier title from catalog fields.</summary>
    public static string ForShelf(string? nameAr, string? nameZh, string? barcodeOrSku = null)
    {
        var primary = Collapse(nameAr);
        if (!LooksSuspicious(primary) && primary.Length > 0)
            return primary;

        var zh = Collapse(nameZh);
        if (!LooksSuspicious(zh) && zh.Length > 0 && !string.Equals(zh, primary, StringComparison.Ordinal))
            return zh;

        var code = Collapse(barcodeOrSku);
        if (code.Length > 0)
            return "صنف " + code;

        if (primary.Length > 0)
            return primary.Length > 42 ? primary[..42].Trim() + "…" : primary;

        return "صنف";
    }

    public static bool ShowSecondaryZh(string? nameAr, string? nameZh)
    {
        var zh = Collapse(nameZh);
        if (zh.Length == 0 || LooksSuspicious(zh))
            return false;
        var primary = ForShelf(nameAr, nameZh, null);
        return !string.Equals(zh, primary, StringComparison.Ordinal);
    }

    /// <summary>Prefer Arabic script for category chips; fall back to Latin name.</summary>
    public static string PreferLocalized(string? primary, string? secondaryEn, string? secondaryZh)
    {
        var a = Collapse(primary);
        if (HasArabic(a))
            return a;
        var en = Collapse(secondaryEn);
        var zh = Collapse(secondaryZh);
        if (HasArabic(en))
            return en;
        if (zh.Length > 0)
            return zh;
        if (a.Length > 0)
            return a;
        return en.Length > 0 ? en : "قسم";
    }

    public static string Collapse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool HasArabic(string? value) =>
        !string.IsNullOrEmpty(value) && value.Any(static c => c is >= '\u0600' and <= '\u06FF');

    [GeneratedRegex(
        @"\b(upc\s*lookup|ean\s*lookup|barcode\s*lookup|buycott(?:\.com)?|openfoodfacts|duckduckgo)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JunkRegex();

    [GeneratedRegex(@"^[\d\s\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsRegex();
}

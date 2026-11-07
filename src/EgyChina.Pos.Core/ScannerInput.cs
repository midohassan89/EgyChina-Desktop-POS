namespace EgyChina.Pos.Core;

/// <summary>
/// USB keyboard-wedge scanners type into whichever box has focus.
/// While the cashier is typing Arabic search, Latin/digit wedges are stolen into the barcode box.
/// </summary>
public static class ScannerInput
{
    public static bool LooksLikeScannerGlyph(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length != 1)
            return false;

        var c = text[0];
        return char.IsAsciiDigit(c)
            || char.IsAsciiLetter(c)
            || c is '-' or '_' or '.' or '/';
    }

    public static bool ShouldStealFromSearch(bool searchFocused, bool barcodeFocused, bool overlayOpen, string? text) =>
        searchFocused
        && !barcodeFocused
        && !overlayOpen
        && LooksLikeScannerGlyph(text);
}

namespace EgyChina.Pos.Core;

/// <summary>
/// USB keyboard-wedge scanners type into whichever control has focus and end with Enter.
/// Retail tills capture ASCII code glyphs globally into a scan buffer whenever the sale lane
/// is idle (no PIN / tender / dialog overlay), then commit on Enter — even if focus is on a
/// product tile or elsewhere, not on the search box.
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

    /// <summary>
    /// Capture wedge glyphs into the global scan buffer (not into the focused control).
    /// Skips overlays and any focused TextBox so the physical keyboard can type digits/letters
    /// into search / PIN / tender fields normally.
    /// </summary>
    public static bool ShouldCaptureGlobally(bool overlayOpen, bool inputFieldFocused, string? text) =>
        !overlayOpen && !inputFieldFocused && LooksLikeScannerGlyph(text);

    /// <summary>Backward-compatible overload (no focused-field check).</summary>
    public static bool ShouldCaptureGlobally(bool overlayOpen, string? text) =>
        ShouldCaptureGlobally(overlayOpen, inputFieldFocused: false, text);

    /// <summary>Legacy helper kept for Arabic-search steal scenarios / older tests.</summary>
    public static bool ShouldStealFromSearch(bool searchFocused, bool barcodeFocused, bool overlayOpen, string? text) =>
        searchFocused
        && !barcodeFocused
        && !overlayOpen
        && LooksLikeScannerGlyph(text);
}

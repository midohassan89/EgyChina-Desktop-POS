using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class ScannerInputTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("A", true)]
    [InlineData("-", true)]
    [InlineData("أ", false)]
    [InlineData("12", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeScannerGlyph(string? text, bool expected) =>
        Assert.Equal(expected, ScannerInput.LooksLikeScannerGlyph(text));

    [Fact]
    public void Captures_globally_when_lane_is_idle()
    {
        Assert.True(ScannerInput.ShouldCaptureGlobally(overlayOpen: false, text: "4"));
        Assert.True(ScannerInput.ShouldCaptureGlobally(overlayOpen: false, text: "Q"));
        Assert.False(ScannerInput.ShouldCaptureGlobally(overlayOpen: true, text: "4"));
        Assert.False(ScannerInput.ShouldCaptureGlobally(overlayOpen: false, text: "ش"));
    }

    [Fact]
    public void Does_not_capture_while_a_text_field_is_focused()
    {
        Assert.False(ScannerInput.ShouldCaptureGlobally(
            overlayOpen: false,
            inputFieldFocused: true,
            text: "4"));
        Assert.True(ScannerInput.ShouldCaptureGlobally(
            overlayOpen: false,
            inputFieldFocused: false,
            text: "4"));
    }

    [Fact]
    public void Steals_only_from_search_when_lane_is_idle()
    {
        Assert.True(ScannerInput.ShouldStealFromSearch(
            searchFocused: true,
            barcodeFocused: false,
            overlayOpen: false,
            text: "4"));

        Assert.False(ScannerInput.ShouldStealFromSearch(
            searchFocused: true,
            barcodeFocused: false,
            overlayOpen: true,
            text: "4"));

        Assert.False(ScannerInput.ShouldStealFromSearch(
            searchFocused: true,
            barcodeFocused: true,
            overlayOpen: false,
            text: "4"));

        Assert.False(ScannerInput.ShouldStealFromSearch(
            searchFocused: true,
            barcodeFocused: false,
            overlayOpen: false,
            text: "ش"));
    }
}

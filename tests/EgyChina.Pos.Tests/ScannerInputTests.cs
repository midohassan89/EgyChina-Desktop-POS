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

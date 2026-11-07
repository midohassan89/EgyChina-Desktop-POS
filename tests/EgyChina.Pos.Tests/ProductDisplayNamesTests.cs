using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class ProductDisplayNamesTests
{
    [Theory]
    [InlineData("UPC Lookup Buycott.com", true)]
    [InlineData("EAN Harvest Foods Lookup", true)]
    [InlineData("1234567890123", true)]
    [InlineData("أوريو 5+1", false)]
    [InlineData("LUX Soft Touch", false)]
    public void LooksSuspicious_detects_scraper_junk(string name, bool expected) =>
        Assert.Equal(expected, ProductDisplayNames.LooksSuspicious(name));

    [Fact]
    public void ForShelf_falls_back_to_zh_or_code()
    {
        Assert.Equal("奥利奥", ProductDisplayNames.ForShelf("UPC Lookup Buycott", "奥利奥", "123"));
        Assert.Equal("صنف 999", ProductDisplayNames.ForShelf("buycott junk", null, "999"));
    }

    [Fact]
    public void PreferLocalized_keeps_arabic_primary()
    {
        Assert.Equal("مشروبات", ProductDisplayNames.PreferLocalized("مشروبات", "Soft Drinks", null));
        Assert.Equal("乳品", ProductDisplayNames.PreferLocalized("Dairy", "Dairy", "乳品"));
    }
}

public sealed class ScaleBarcodeTests
{
    [Fact]
    public void TryParse_reads_plu_and_embedded_total()
    {
        Assert.True(ScaleBarcode.TryParse("2080024026130", out var parse));
        Assert.Equal("80024", parse.Plu);
        Assert.Equal(2613, parse.EmbeddedTotalMinor);
        Assert.True(ScaleBarcode.IsScaleBarcode("2080024026130"));
        Assert.False(ScaleBarcode.IsScaleBarcode("6224001234567"));
    }
}

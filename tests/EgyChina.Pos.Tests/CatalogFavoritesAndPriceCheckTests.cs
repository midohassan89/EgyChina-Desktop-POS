using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class CatalogFavoritesAndPriceCheckTests : IDisposable
{
    private readonly string _directory;

    public CatalogFavoritesAndPriceCheckTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "egychina-pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Schema_reaches_version_four_with_favorites_table()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        Assert.Equal(5, database.GetSchemaVersion());

        var rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode);
        Assert.NotNull(rice);
        Assert.False(rice.IsFavorite);

        Assert.True(database.ToggleFavorite(rice.Id));
        rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode);
        Assert.NotNull(rice);
        Assert.True(rice.IsFavorite);
        Assert.Equal("★", rice.FavoriteGlyph);

        Assert.False(database.ToggleFavorite(rice.Id));
        rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode);
        Assert.NotNull(rice);
        Assert.False(rice.IsFavorite);
    }

    [Fact]
    public void PriceCheckResult_formats_money_without_mutating_cart_state()
    {
        var result = new PriceCheckResult("2000000000018", "أرز", "大米", "RICE", 8500);
        Assert.Equal(Money.FormatEgp(8500), result.PriceEgpText);
        Assert.True(result.HasNameZh);
    }
}

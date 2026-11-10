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
    public void Schema_reaches_version_nine_with_stable_favorites()
    {
        var database = new PosDatabase(Path.Combine(_directory, "fav-schema.db"));
        database.Initialize();
        Assert.Equal(9, database.GetSchemaVersion());

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
    public void Multiple_favorites_accumulate_and_survive_reopen()
    {
        var path = Path.Combine(_directory, "fav-multi.db");
        var database = new PosDatabase(path);
        database.Initialize();

        var rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode);
        var oil = database.FindByBarcode(PosDatabase.DemoOilBarcode);
        var tea = database.FindByBarcode(PosDatabase.DemoTeaBarcode);
        Assert.NotNull(rice);
        Assert.NotNull(oil);
        Assert.NotNull(tea);

        Assert.True(database.ToggleFavorite(rice.Id));
        Assert.True(database.ToggleFavorite(oil.Id));
        Assert.True(database.ToggleFavorite(tea.Id));

        var favorites = database.ListActiveProducts().Where(p => p.IsFavorite).Select(p => p.Id).ToHashSet();
        Assert.Equal(3, favorites.Count);
        Assert.Contains(rice.Id, favorites);
        Assert.Contains(oil.Id, favorites);
        Assert.Contains(tea.Id, favorites);

        // Simulate power-off: reopen the same SQLite file.
        var reopened = new PosDatabase(path);
        reopened.Initialize();
        var again = reopened.ListActiveProducts().Where(p => p.IsFavorite).Select(p => p.Barcode).ToHashSet();
        Assert.Equal(3, again.Count);
        Assert.Contains(PosDatabase.DemoRiceBarcode, again);
        Assert.Contains(PosDatabase.DemoOilBarcode, again);
        Assert.Contains(PosDatabase.DemoTeaBarcode, again);
    }

    [Fact]
    public void Favorites_survive_catalog_id_remap_via_barcode()
    {
        var database = new PosDatabase(Path.Combine(_directory, "fav-remap.db"));
        database.Initialize();

        var rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode)!;
        Assert.True(database.ToggleFavorite(rice.Id));

        // Remap: insert a replacement ERP row with the same barcode, deactivate the demo id.
        var json = """
            {
              "serverTime":"2026-11-10T00:00:00.000Z",
              "products":[
                {
                  "id":9042,
                  "wcId":9042,
                  "prismaId":"clrice-remap",
                  "posKey":"clrice-remap",
                  "sku":"RICE-LIVE",
                  "name":"أرز مصري",
                  "price":"12.50",
                  "regular_price":"12.50",
                  "sale_price":"",
                  "meta_data":[{"key":"_op_barcode","value":"2000000000018"}]
                }
              ],
              "removedPrismaIds":[]
            }
            """;
        database.ApplyCatalogFromErpJson(json);
        database.DeactivateProductsNotIn(["clrice-remap"]);
        database.RebindFavorites();

        var favorites = database.ListActiveProducts().Where(p => p.IsFavorite).ToList();
        Assert.Single(favorites);
        Assert.Equal("2000000000018", favorites[0].Barcode);
        Assert.Equal("clrice-remap", favorites[0].Id);
    }

    [Fact]
    public void PriceCheckResult_formats_money_without_mutating_cart_state()
    {
        var result = new PriceCheckResult("2000000000018", "أرز", "大米", "RICE", 8500);
        Assert.Equal(Money.FormatEgp(8500), result.PriceEgpText);
        Assert.True(result.HasNameZh);
    }
}

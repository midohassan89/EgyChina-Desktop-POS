using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class CatalogCategoryTests
{
    [Fact]
    public void UpsertCategories_and_product_category_link_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "egychina-pos-cat-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var database = new PosDatabase(path);
            database.Initialize();
            Assert.Equal(9, database.GetSchemaVersion());

            database.UpsertCategories(
            [
                new CatalogCategoryDraft("cat-dairy", "ألبان", "Dairy", "乳品", "dairy", null, 2),
                new CatalogCategoryDraft("cat-rice", "أرز", "Rice", "米", "rice", null, 1),
            ]);

            var categories = database.ListCategories();
            Assert.Equal(2, categories.Count);
            Assert.Equal("cat-dairy", categories[0].Id);
            Assert.Contains("ألبان", categories[0].ChipText, StringComparison.Ordinal);

            var json = """
                {
                  "serverTime": "2026-11-07T12:00:00Z",
                  "products": [
                    {
                      "id": 900001,
                      "prismaId": "prod-milk",
                      "posKey": "prod-milk",
                      "sku": "MILK-1",
                      "name": "لبن",
                      "price": "25",
                      "categoryId": "cat-dairy",
                      "images": [],
                      "meta_data": [],
                      "isPackage": false
                    }
                  ],
                  "removedPrismaIds": []
                }
                """;
            database.ApplyCatalogFromErpJson(json);
            var product = database.ListActiveProducts().First(p => p.Id == "prod-milk");
            Assert.Equal("cat-dairy", product.CategoryId);
            Assert.Equal("ألبان", product.CategoryNameAr);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}

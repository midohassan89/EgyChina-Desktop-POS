using System.Text.Json;
using EgyChina.Pos.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

/// <summary>
/// Catalog link must follow the live ERP shapes in <c>mapProduct.ts</c> / products route,
/// not the earlier mistaken assumption that POS <c>id</c> is the Prisma cuid.
/// </summary>
public sealed class CatalogLinkTests : IDisposable
{
    private readonly string _directory;

    public CatalogLinkTests()
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
    public void TryReadCatalogLink_reads_pos_cached_product_shape()
    {
        // prismaProductToCached: id = wcId (number), prismaId = cuid, sku string.
        using var doc = JsonDocument.Parse("""
            {"id":9042,"sku":"RICE-5KG","prismaId":"clxyz001","name":"أرز","price":"85.00"}
            """);

        Assert.True(PosDatabase.TryReadCatalogLink(doc.RootElement, out var sku, out var numericId, out var prismaId));
        Assert.Equal("RICE-5KG", sku);
        Assert.Equal(9042, numericId);
        Assert.Equal("clxyz001", prismaId);
    }

    [Fact]
    public void TryReadCatalogLink_prefers_wcId_on_admin_page_shape()
    {
        // Paginated admin JSON: id is cuid string, wcId is the checkout number.
        using var doc = JsonDocument.Parse("""
            {"id":"clxyz001","wcId":9042,"sku":"RICE-5KG","name":"أرز","price":85}
            """);

        Assert.True(PosDatabase.TryReadCatalogLink(doc.RootElement, out var sku, out var numericId, out var prismaId));
        Assert.Equal("RICE-5KG", sku);
        Assert.Equal(9042, numericId);
        Assert.Equal("clxyz001", prismaId);
    }

    [Fact]
    public void TryReadCatalogLink_rejects_empty_sku_and_cuid_only_id()
    {
        using var emptySku = JsonDocument.Parse("""{"id":9042,"sku":"","prismaId":"clxyz001"}""");
        Assert.False(PosDatabase.TryReadCatalogLink(emptySku.RootElement, out _, out _, out _));

        using var cuidOnly = JsonDocument.Parse("""{"id":"clxyz001","sku":"RICE-5KG"}""");
        Assert.False(PosDatabase.TryReadCatalogLink(cuidOnly.RootElement, out _, out _, out _));

        using var badWc = JsonDocument.Parse("""{"id":"clxyz001","wcId":0,"sku":"RICE-5KG"}""");
        Assert.False(PosDatabase.TryReadCatalogLink(badWc.RootElement, out _, out _, out _));
    }

    [Fact]
    public void Pos_shape_links_by_sku_without_changing_shelf_price()
    {
        var database = OpenDb();
        InsertStoreProduct(database, "rice-1", "RICE-5KG", "2000000099999", "أرز اختبار", 12_345);

        var linked = database.LinkCatalogFromErpJson("""
            {
              "serverTime":"2026-11-07T00:00:00.000Z",
              "products":[
                {"id":9042,"sku":"RICE-5KG","prismaId":"clxyz001","name":"أرز","price":"99.99","regular_price":"99.99"}
              ],
              "removedPrismaIds":[]
            }
            """);

        Assert.Equal(1, linked);
        Assert.Equal(9042, database.TryGetErpNumericId("rice-1"));
        Assert.Equal(12_345, ReadPriceMinor(database, "rice-1"));
        Assert.Equal("clxyz001", ReadPrismaId(database, "rice-1"));
    }

    [Fact]
    public void Admin_shape_links_via_wcId_not_string_id()
    {
        var database = OpenDb();
        InsertStoreProduct(database, "oil-1", "OIL-1L", "2000000088888", "زيت اختبار", 6_250);

        var linked = database.LinkCatalogFromErpJson("""
            {
              "products":[
                {"id":"cladminoil","wcId":7711,"sku":"OIL-1L","name":"زيت","price":50}
              ]
            }
            """);

        Assert.Equal(1, linked);
        Assert.Equal(7711, database.TryGetErpNumericId("oil-1"));
        Assert.Equal("cladminoil", ReadPrismaId(database, "oil-1"));
        Assert.Equal(6_250, ReadPriceMinor(database, "oil-1"));
    }

    [Fact]
    public void Demo_sku_is_never_linked_even_when_erp_returns_same_sku()
    {
        var database = OpenDb();
        Assert.Null(database.TryGetErpNumericId("demo-rice"));

        var linked = database.LinkCatalogFromErpJson("""
            {
              "products":[
                {"id":55,"sku":"DEMO-RICE","prismaId":"p-rice","name":"أرز"}
              ]
            }
            """);

        Assert.Equal(0, linked);
        Assert.Null(database.TryGetErpNumericId("demo-rice"));
        Assert.Equal(8_500, ReadPriceMinor(database, "demo-rice"));
    }

    [Fact]
    public void Unknown_sku_and_package_empty_sku_do_not_touch_local_rows()
    {
        var database = OpenDb();
        InsertStoreProduct(database, "tea-1", "TEA-400", "2000000077777", "شاي اختبار", 4_500);
        var before = ReadPriceMinor(database, "tea-1");

        var linked = database.LinkCatalogFromErpJson("""
            {
              "products":[
                {"id":100,"sku":"OTHER-SKU","prismaId":"x","name":"غير موجود"},
                {"id":100,"sku":"","prismaId":"x","name":"عبوة","isPackage":true}
              ]
            }
            """);

        Assert.Equal(0, linked);
        Assert.Null(database.TryGetErpNumericId("tea-1"));
        Assert.Equal(before, ReadPriceMinor(database, "tea-1"));
    }

    private PosDatabase OpenDb()
    {
        var database = new PosDatabase(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }

    private static void InsertStoreProduct(
        PosDatabase database,
        string id,
        string sku,
        string barcode,
        string nameAr,
        long priceMinor)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var product = connection.CreateCommand())
        {
            product.CommandText = """
                INSERT INTO product (
                  id, sku, barcode, name_ar, name_en, name_zh, unit_ar, tax_rate_bps, is_active, updated_at
                ) VALUES (
                  $id, $sku, $barcode, $nameAr, null, null, 'قطعة', 0, 1, $now
                );
                """;
            product.Parameters.AddWithValue("$id", id);
            product.Parameters.AddWithValue("$sku", sku);
            product.Parameters.AddWithValue("$barcode", barcode);
            product.Parameters.AddWithValue("$nameAr", nameAr);
            product.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            product.ExecuteNonQuery();
        }

        using (var price = connection.CreateCommand())
        {
            price.CommandText = """
                INSERT INTO product_price (product_id, currency, amount_minor)
                VALUES ($id, 'EGP', $amount);
                """;
            price.Parameters.AddWithValue("$id", id);
            price.Parameters.AddWithValue("$amount", priceMinor);
            price.ExecuteNonQuery();
        }
    }

    private static long ReadPriceMinor(PosDatabase database, string productId)
    {
        using var connection = OpenRead(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT amount_minor FROM product_price WHERE product_id = $id AND currency = 'EGP'";
        command.Parameters.AddWithValue("$id", productId);
        return (long)command.ExecuteScalar()!;
    }

    private static string? ReadPrismaId(PosDatabase database, string productId)
    {
        using var connection = OpenRead(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT prisma_id FROM product WHERE id = $id";
        command.Parameters.AddWithValue("$id", productId);
        return command.ExecuteScalar() as string;
    }

    private static SqliteConnection OpenRead(PosDatabase database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}

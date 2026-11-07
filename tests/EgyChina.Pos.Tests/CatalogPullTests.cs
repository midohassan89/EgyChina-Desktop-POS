using System.Text.Json;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

/// <summary>
/// Phase 5: live catalog pull updates shelf prices/barcodes, flips source to erp,
/// purges unused DEMO rows, and never rewrites committed sale_line snapshots.
/// </summary>
public sealed class CatalogPullTests : IDisposable
{
    private readonly string _directory;

    public CatalogPullTests()
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
    public void Apply_upserts_price_barcode_and_sets_erp_source()
    {
        var database = OpenDb();
        Assert.Equal("demo", database.GetSetting("catalog.source"));
        Assert.Equal(3, database.CountProducts());

        var result = database.ApplyCatalogFromErpJson("""
            {
              "serverTime":"2026-11-07T12:00:00.000Z",
              "products":[
                {
                  "id":9042,
                  "sku":"RICE-5KG",
                  "prismaId":"clrice001",
                  "name":"أرز مصري",
                  "nameEn":"Egyptian rice",
                  "nameZh":"大米",
                  "price":"99.50",
                  "regular_price":"99.50",
                  "sale_price":"",
                  "meta_data":[{"key":"_op_barcode","value":"6223000000001"}],
                  "images":[{"id":0,"src":"/uploads/products/rice.jpg"}]
                }
              ],
              "removedPrismaIds":[]
            }
            """);

        Assert.Equal(1, result.Upserted);
        Assert.Equal(3, result.DemoRemoved);
        Assert.Equal("2026-11-07T12:00:00.000Z", result.ServerTime);
        Assert.Equal("erp", database.GetSetting("catalog.source"));
        Assert.Equal(0, CountActiveDemo(database));

        var product = Assert.Single(database.ListActiveProducts());
        Assert.Equal("clrice001", product.Id);
        Assert.Equal("RICE-5KG", product.Sku);
        Assert.Equal("6223000000001", product.Barcode);
        Assert.Equal(9_950, product.PriceEgpMinor);
        Assert.Equal("/uploads/products/rice.jpg", product.ImageUrl);
        Assert.Equal(9042, database.TryGetErpNumericId("clrice001"));
    }

    [Fact]
    public void Apply_updates_existing_sku_without_rewriting_sale_line()
    {
        var database = OpenDb();
        InsertStoreProduct(database, "rice-1", "RICE-5KG", "2000000099999", "أرز قديم", 8_500);
        var sale = TestShift.Sell(
            database,
            [new CashSaleLine("rice-1", "2000000099999", "أرز قديم", 2, 8_500)],
            20_000);

        database.ApplyCatalogFromErpJson("""
            {
              "serverTime":"2026-11-07T12:00:00.000Z",
              "products":[
                {
                  "id":9042,
                  "sku":"RICE-5KG",
                  "prismaId":"clrice001",
                  "name":"أرز جديد",
                  "price":"120.00",
                  "meta_data":[{"key":"_op_barcode","value":"6223000000099"}]
                }
              ],
              "removedPrismaIds":[]
            }
            """);

        Assert.Equal(12_000, ReadPriceMinor(database, "rice-1"));
        Assert.Equal(9042, database.TryGetErpNumericId("rice-1"));
        Assert.Equal("أرز جديد", ReadProductName(database, "rice-1"));

        using var connection = OpenRead(database);
        using var line = connection.CreateCommand();
        line.CommandText = """
            SELECT name_ar, unit_price_minor, line_total_minor, product_id
            FROM sale_line WHERE sale_id = $sale
            """;
        line.Parameters.AddWithValue("$sale", sale.SaleId);
        using var reader = line.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("أرز قديم", reader.GetString(0));
        Assert.Equal(8_500, reader.GetInt64(1));
        Assert.Equal(17_000, reader.GetInt64(2));
        Assert.Equal("rice-1", reader.GetString(3));
    }

    [Fact]
    public void Apply_keeps_demo_row_when_referenced_by_sale_but_hides_it()
    {
        var database = OpenDb();
        TestShift.Sell(
            database,
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8_500)],
            10_000);

        database.ApplyCatalogFromErpJson("""
            {
              "serverTime":"2026-11-07T12:00:00.000Z",
              "products":[
                {
                  "id":1001,
                  "sku":"TEA-400",
                  "prismaId":"cltea",
                  "name":"شاي",
                  "price":"45.00",
                  "meta_data":[{"key":"_op_barcode","value":"6223000000555"}]
                }
              ],
              "removedPrismaIds":[]
            }
            """);

        Assert.Equal("erp", database.GetSetting("catalog.source"));
        Assert.Equal(0, CountActiveDemo(database));
        Assert.True(DemoRowExists(database, "demo-rice"));
        Assert.False(IsActive(database, "demo-rice"));

        using var connection = OpenRead(database);
        using var line = connection.CreateCommand();
        line.CommandText = "SELECT name_ar, unit_price_minor FROM sale_line WHERE product_id = 'demo-rice'";
        using var reader = line.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("أرز مصري ٥ كجم", reader.GetString(0));
        Assert.Equal(8_500, reader.GetInt64(1));
    }

    [Fact]
    public void Apply_deactivates_removed_prisma_ids_including_packages()
    {
        var database = OpenDb();
        database.ApplyCatalogFromErpJson("""
            {
              "serverTime":"2026-11-07T10:00:00.000Z",
              "products":[
                {
                  "id":77,
                  "sku":"OIL-1L",
                  "prismaId":"cloil",
                  "name":"زيت",
                  "price":"62.50",
                  "meta_data":[{"key":"_op_barcode","value":"6223000000777"}]
                },
                {
                  "id":77,
                  "sku":"",
                  "prismaId":"cloil",
                  "posKey":"cloil-pkg-p1",
                  "isPackage":true,
                  "name":"زيت - كرتونة",
                  "unit":"كرتونة",
                  "price":"600.00",
                  "meta_data":[{"key":"_op_barcode","value":"6223000000888"}]
                }
              ],
              "removedPrismaIds":[]
            }
            """);

        Assert.Equal(2, database.ListActiveProducts().Count);

        database.ApplyCatalogFromErpJson("""
            {
              "serverTime":"2026-11-07T11:00:00.000Z",
              "products":[],
              "removedPrismaIds":["cloil"]
            }
            """);

        Assert.Empty(database.ListActiveProducts());
        Assert.False(IsActive(database, "cloil"));
        Assert.False(IsActive(database, "cloil-pkg-p1"));
    }

    [Fact]
    public void Seed_does_not_return_after_erp_source_even_if_catalog_empty()
    {
        var path = Path.Combine(_directory, "empty-erp.db");
        var database = new PosDatabase(path);
        database.Initialize();
        database.ApplyCatalogFromErpJson("""
            {"serverTime":"2026-11-07T12:00:00.000Z","products":[],"removedPrismaIds":[]}
            """);
        Assert.Equal("erp", database.GetSetting("catalog.source"));

        database = new PosDatabase(path);
        database.Initialize();
        Assert.Equal("erp", database.GetSetting("catalog.source"));
        Assert.Equal(0, database.CountProducts());
    }

    [Fact]
    public void TryReadCatalogProduct_prefers_sale_price_and_meta_barcode()
    {
        using var doc = JsonDocument.Parse("""
            {
              "id":9042,
              "sku":"RICE-5KG",
              "prismaId":"clrice001",
              "name":"أرز",
              "price":"100.00",
              "sale_price":"85.00",
              "on_sale":true,
              "meta_data":[{"key":"_op_barcode","value":"6223000000001"}]
            }
            """);

        Assert.True(PosDatabase.TryReadCatalogProduct(doc.RootElement, out var draft));
        Assert.Equal("clrice001", draft.LocalId);
        Assert.Equal(8_500, draft.PriceEgpMinor);
        Assert.Equal("6223000000001", draft.Barcode);
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

    private static string ReadProductName(PosDatabase database, string productId)
    {
        using var connection = OpenRead(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name_ar FROM product WHERE id = $id";
        command.Parameters.AddWithValue("$id", productId);
        return (string)command.ExecuteScalar()!;
    }

    private static int CountActiveDemo(PosDatabase database)
    {
        using var connection = OpenRead(database);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM product
            WHERE is_active = 1
              AND (id LIKE 'demo-%' OR sku GLOB 'DEMO-*' OR sku GLOB 'demo-*')
            """;
        return (int)(long)command.ExecuteScalar()!;
    }

    private static bool DemoRowExists(PosDatabase database, string id)
    {
        using var connection = OpenRead(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM product WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()! == 1;
    }

    private static bool IsActive(PosDatabase database, string id)
    {
        using var connection = OpenRead(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_active FROM product WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is not null and not DBNull && Convert.ToInt32(value) == 1;
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

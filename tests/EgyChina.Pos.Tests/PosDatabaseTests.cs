using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class PosDatabaseTests : IDisposable
{
    private readonly string _directory;

    public PosDatabaseTests()
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
    public void Initialize_creates_wal_database_and_demo_catalog_once()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        database.Initialize();

        Assert.Equal("wal", database.JournalMode());
        Assert.Equal(1, database.ForeignKeysEnabled());
        Assert.Equal(3, database.CountProducts());
        Assert.Equal("demo", database.GetSetting("catalog.source"));

        var rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode);
        Assert.NotNull(rice);
        Assert.Equal("أرز مصري ٥ كجم", rice.NameAr);
        Assert.Equal(8500, rice.PriceEgpMinor);
        Assert.Equal(Money.FormatEgp(8500), rice.PriceEgpText);

        using var connection = OpenRaw(database.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT next_no FROM receipt_counter WHERE id = 1";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Outbox_idempotency_key_is_inserted_once()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();

        const string key = "term:sale:1";
        Assert.True(database.TryEnqueueOutbox("sale", "1", "SaleCompleted", """{"demo":true}""", key));
        Assert.False(database.TryEnqueueOutbox("sale", "1", "SaleCompleted", """{"demo":true}""", key));
        Assert.Equal(1, database.CountPendingOutbox());
    }

    [Fact]
    public void Schema_rejects_an_orphan_sale_line_and_a_duplicate_barcode()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();

        using var connection = OpenRaw(database.DatabasePath);
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        using (var orphan = connection.CreateCommand())
        {
            orphan.CommandText = """
                INSERT INTO sale_line (
                  id, sale_id, line_no, name_ar, qty_milli, unit_price_minor, line_total_minor
                ) VALUES ('line-1', 'missing-sale', 1, 'اختبار', 1000, 100, 100);
                """;
            Assert.Throws<SqliteException>(() => orphan.ExecuteNonQuery());
        }

        using var duplicate = connection.CreateCommand();
        duplicate.CommandText = """
            INSERT INTO product (
              id, sku, barcode, name_ar, unit_ar, tax_rate_bps, is_active, updated_at
            ) VALUES (
              'other', 'DEMO-OTHER', $barcode, 'نسخة', 'قطعة', 0, 1, $now
            );
            """;
        duplicate.Parameters.AddWithValue("$barcode", PosDatabase.DemoRiceBarcode);
        duplicate.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Throws<SqliteException>(() => duplicate.ExecuteNonQuery());
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        return connection;
    }
}

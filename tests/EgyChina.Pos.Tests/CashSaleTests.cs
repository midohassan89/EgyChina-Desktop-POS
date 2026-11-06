using System.Text.Json;
using EgyChina.Pos.Core;
using Xunit;
using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;
using Microsoft.Data.Sqlite;

namespace EgyChina.Pos.Tests;

public sealed class CashSaleTests : IDisposable
{
    private readonly string _directory;

    public CashSaleTests()
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
    public void Cash_sale_persists_lines_payment_and_pending_outbox()
    {
        var database = NewDatabase();
        var sale = database.CompleteCashSale(
            [
                new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8500),
                new CashSaleLine("demo-oil", PosDatabase.DemoOilBarcode, "زيت ذرة ١ لتر", 2, 6250),
            ],
            30_000);

        Assert.Equal(1, sale.ReceiptNo);
        Assert.Equal(8500 + 12_500, sale.TotalMinor);
        Assert.Equal(30_000, sale.TenderMinor);
        Assert.Equal(9_000, sale.ChangeMinor);
        Assert.Equal(PosDatabase.LocalCashierId, sale.CashierId);
        Assert.Equal($"{sale.TerminalId}:sale:{sale.SaleId}", sale.IdempotencyKey);
        Assert.Equal(1, database.CountPendingOutbox());

        using var connection = Open(database.DatabasePath);
        using var saleRow = connection.CreateCommand();
        saleRow.CommandText = """
            SELECT status, sync_status, currency, total_minor, receipt_no, cashier_id
            FROM sale WHERE id = $id
            """;
        saleRow.Parameters.AddWithValue("$id", sale.SaleId);
        using var reader = saleRow.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("completed", reader.GetString(0));
        Assert.Equal("pending", reader.GetString(1));
        Assert.Equal("EGP", reader.GetString(2));
        Assert.Equal(21_000, reader.GetInt64(3));
        Assert.Equal(1, reader.GetInt64(4));
        Assert.Equal(PosDatabase.LocalCashierId, reader.GetString(5));
        reader.Close();

        using var line = connection.CreateCommand();
        line.CommandText = "SELECT qty_milli, line_total_minor, name_ar FROM sale_line WHERE sale_id = $id ORDER BY line_no";
        line.Parameters.AddWithValue("$id", sale.SaleId);
        using var lines = line.ExecuteReader();
        Assert.True(lines.Read());
        Assert.Equal(1000, lines.GetInt64(0));
        Assert.Equal(8500, lines.GetInt64(1));
        Assert.Equal("أرز مصري ٥ كجم", lines.GetString(2));
        Assert.True(lines.Read());
        Assert.Equal(2000, lines.GetInt64(0));
        Assert.Equal(12_500, lines.GetInt64(1));
        lines.Close();

        using var payment = connection.CreateCommand();
        payment.CommandText = """
            SELECT method, currency, amount_minor, amount_egp_minor
            FROM payment WHERE sale_id = $id
            """;
        payment.Parameters.AddWithValue("$id", sale.SaleId);
        using var paid = payment.ExecuteReader();
        Assert.True(paid.Read());
        Assert.Equal("cash_egp", paid.GetString(0));
        Assert.Equal("EGP", paid.GetString(1));
        Assert.Equal(30_000, paid.GetInt64(2));
        Assert.Equal(30_000, paid.GetInt64(3));
        paid.Close();

        using var outbox = connection.CreateCommand();
        outbox.CommandText = """
            SELECT event_type, status, idempotency_key, payload_json
            FROM outbox WHERE aggregate_id = $id
            """;
        outbox.Parameters.AddWithValue("$id", sale.SaleId);
        using var queued = outbox.ExecuteReader();
        Assert.True(queued.Read());
        Assert.Equal("SaleCompleted", queued.GetString(0));
        Assert.Equal("pending", queued.GetString(1));
        Assert.Equal(sale.IdempotencyKey, queued.GetString(2));
        using var payload = JsonDocument.Parse(queued.GetString(3));
        var root = payload.RootElement;
        Assert.Equal(sale.SaleId, root.GetProperty("saleId").GetString());
        Assert.Equal(21_000, root.GetProperty("totalMinor").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fx").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("shiftId").ValueKind);
        Assert.Equal("cash_egp", root.GetProperty("payments")[0].GetProperty("method").GetString());
        Assert.Equal(30_000, root.GetProperty("payments")[0].GetProperty("amountMinor").GetInt64());
        Assert.Equal(2000, root.GetProperty("lines")[1].GetProperty("qtyMilli").GetInt64());

        using var counter = connection.CreateCommand();
        counter.CommandText = "SELECT next_no FROM receipt_counter WHERE id = 1";
        Assert.Equal(2L, (long)counter.ExecuteScalar()!);
    }

    [Fact]
    public void Short_tender_writes_nothing()
    {
        var database = NewDatabase();
        Assert.Throws<InvalidOperationException>(() => database.CompleteCashSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8500)],
            1000));

        Assert.Equal(0, database.CountPendingOutbox());
        using var connection = Open(database.DatabasePath);
        using var sales = connection.CreateCommand();
        sales.CommandText = "SELECT COUNT(*) FROM sale";
        Assert.Equal(0L, (long)sales.ExecuteScalar()!);
        using var counter = connection.CreateCommand();
        counter.CommandText = "SELECT next_no FROM receipt_counter WHERE id = 1";
        Assert.Equal(1L, (long)counter.ExecuteScalar()!);
    }

    [Fact]
    public void Failed_line_rolls_back_the_receipt_number()
    {
        var database = NewDatabase();
        Assert.ThrowsAny<SqliteException>(() => database.CompleteCashSale(
            [new CashSaleLine("missing-product", "000", "صنف غير موجود", 1, 100)],
            100));

        Assert.Equal(0, database.CountPendingOutbox());
        using var connection = Open(database.DatabasePath);
        using var sales = connection.CreateCommand();
        sales.CommandText = "SELECT COUNT(*) FROM sale";
        Assert.Equal(0L, (long)sales.ExecuteScalar()!);
        using var counter = connection.CreateCommand();
        counter.CommandText = "SELECT next_no FROM receipt_counter WHERE id = 1";
        Assert.Equal(1L, (long)counter.ExecuteScalar()!);
    }

    [Fact]
    public async Task Sync_tick_leaves_the_sale_pending()
    {
        var database = NewDatabase();
        database.CompleteCashSale(
            [new CashSaleLine("demo-tea", PosDatabase.DemoTeaBarcode, "شاي أخضر ٤٠٠ جم", 1, 4500)],
            4500);

        var status = new SyncStatus();
        var worker = new SyncWorker(database, status, new SyncOptions
        {
            BaseAddress = null,
            Interval = TimeSpan.FromHours(1),
        });
        await worker.TickAsync(CancellationToken.None);
        await worker.DisposeAsync();

        Assert.Equal(1, database.CountPendingOutbox());
        Assert.Equal(SyncWorker.NotConfiguredCursor, database.ReadCursor(SyncWorker.CursorStream)?.Cursor);
        Assert.False(status.Read().IsOnline);
    }

    [Fact]
    public void Receipt_numbers_advance()
    {
        var database = NewDatabase();
        var first = database.CompleteCashSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8500)],
            8500);
        var second = database.CompleteCashSale(
            [new CashSaleLine("demo-tea", PosDatabase.DemoTeaBarcode, "شاي أخضر ٤٠٠ جم", 1, 4500)],
            5000);

        Assert.Equal(1, first.ReceiptNo);
        Assert.Equal(2, second.ReceiptNo);
        Assert.Equal(500, second.ChangeMinor);
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal(2, database.CountPendingOutbox());
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        return database;
    }

    private static SqliteConnection Open(string path)
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

using System.Text.Json;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Printing;
using EgyChina.Pos.Sync;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class SupervisorReturnTests : IDisposable
{
    private readonly string _directory;

    public SupervisorReturnTests()
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
    public void ReceiptCodes_accept_pos_prefix_and_guid_only()
    {
        Assert.True(ReceiptCodes.LooksLikeReceiptCode("POS-12"));
        Assert.True(ReceiptCodes.LooksLikeReceiptCode("pos-99"));
        Assert.True(ReceiptCodes.LooksLikeReceiptCode(Guid.NewGuid().ToString("D")));
        Assert.False(ReceiptCodes.LooksLikeReceiptCode("6223001234567"));
        Assert.False(ReceiptCodes.LooksLikeReceiptCode("POS-"));
        Assert.False(ReceiptCodes.LooksLikeReceiptCode("Q-12"));
        Assert.True(ReceiptCodes.TryParseReceiptNumber("POS-42", out var no));
        Assert.Equal(42, no);
    }

    [Fact]
    public void SupervisorAuth_marks_supervisor_and_manager_only()
    {
        Assert.True(SupervisorAuth.IsPrivileged(CashierRole.Supervisor));
        Assert.True(SupervisorAuth.IsPrivileged(CashierRole.Manager));
        Assert.False(SupervisorAuth.IsPrivileged(CashierRole.Cashier));
        Assert.Equal("حذف صنف — إذن مشرف", SupervisorAuth.TitleAr(SupervisorActionKind.DeleteItem));
    }

    [Fact]
    public void FindSaleLinesByCode_loads_original_sale_and_rejects_returns()
    {
        var database = NewDatabase();
        var original = TestShift.Sell(
            database,
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 2, 8500)],
            20_000);

        var byReceipt = database.FindSaleLinesByCode($"POS-{original.ReceiptNo}");
        Assert.NotNull(byReceipt);
        Assert.Single(byReceipt);
        Assert.Equal("demo-rice", byReceipt[0].ProductId);
        Assert.Equal(2, byReceipt[0].Qty);
        Assert.Equal(8500, byReceipt[0].UnitPriceMinor);

        var byGuid = database.FindSaleLinesByCode(original.SaleId);
        Assert.NotNull(byGuid);
        Assert.Equal(byReceipt[0].ProductId, byGuid[0].ProductId);

        var ret = database.CompleteSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 2, 8500)],
            17_000,
            original.CashierId,
            original.ShiftId,
            PosPaymentMethod.Cash,
            isReturn: true);
        Assert.Equal(-17_000, ret.TotalMinor);
        Assert.Null(database.FindSaleLinesByCode($"POS-{ret.ReceiptNo}"));
        Assert.Null(database.FindSaleLinesByCode(ret.SaleId));
    }

    [Fact]
    public void Return_sale_writes_signed_totals_and_isReturn_payload()
    {
        var database = NewDatabase();
        var shift = TestShift.Open(database);
        var sale = database.CompleteSale(
            [new CashSaleLine("demo-oil", PosDatabase.DemoOilBarcode, "زيت ذرة ١ لتر", 1, 6250)],
            6250,
            shift.CashierId,
            shift.Id,
            PosPaymentMethod.Cash,
            isReturn: true);

        Assert.Equal(-6250, sale.TotalMinor);
        Assert.Equal(-6250, sale.TenderMinor);
        Assert.Equal(0, sale.ChangeMinor);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = database.DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ConnectionString);
        connection.Open();
        using var saleRow = connection.CreateCommand();
        saleRow.CommandText = "SELECT total_minor FROM sale WHERE id = $id";
        saleRow.Parameters.AddWithValue("$id", sale.SaleId);
        Assert.Equal(6250L, (long)saleRow.ExecuteScalar()!);

        using var note = connection.CreateCommand();
        note.CommandText = "SELECT note FROM sale WHERE id = $id";
        note.Parameters.AddWithValue("$id", sale.SaleId);
        Assert.Equal("return", (string)note.ExecuteScalar()!);

        using var pay = connection.CreateCommand();
        pay.CommandText = "SELECT amount_minor FROM payment WHERE sale_id = $id";
        pay.Parameters.AddWithValue("$id", sale.SaleId);
        Assert.Equal(6250L, (long)pay.ExecuteScalar()!);

        using var outbox = connection.CreateCommand();
        outbox.CommandText = "SELECT payload_json FROM outbox WHERE aggregate_id = $id";
        outbox.Parameters.AddWithValue("$id", sale.SaleId);
        using var payload = JsonDocument.Parse((string)outbox.ExecuteScalar()!);
        var root = payload.RootElement;
        Assert.True(root.GetProperty("isReturn").GetBoolean());
        Assert.Equal(-6250, root.GetProperty("totalMinor").GetInt64());
        Assert.Equal(6250, root.GetProperty("lines")[0].GetProperty("lineTotalMinor").GetInt64());
    }

    [Fact]
    public void ErpCheckoutMapper_maps_return_flag_and_negative_total()
    {
        const string payload = """
            {
              "saleId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
              "totalMinor": -8500,
              "isReturn": true,
              "lines": [
                {
                  "productId": "demo-rice",
                  "nameAr": "أرز",
                  "qtyMilli": 1000,
                  "unitPriceMinor": 8500,
                  "lineTotalMinor": 8500
                }
              ]
            }
            """;

        Assert.True(ErpCheckoutMapper.TryMap(
            payload,
            ErpMoneyScale.MajorUnits,
            "cash",
            _ => 55,
            out var json,
            out var block));
        Assert.Null(block);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("isReturn").GetBoolean());
        Assert.Equal(-85.00m, root.GetProperty("total").GetDecimal());
        Assert.Equal(85.00m, root.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public void DrawerKickOnly_emits_esc_p_pulse()
    {
        var bytes = EscPosRasterEncoder.DrawerKickOnly();
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, bytes);
    }

    [Fact]
    public async Task KickDrawerAsync_writes_pulse_through_file_transport()
    {
        var path = Path.Combine(_directory, "drawer.bin");
        var printer = EscPosReceiptPrinter.Create(new PrinterOptions
        {
            Transport = PrinterTransportKind.File,
            FilePath = path,
        });
        await printer.KickDrawerAsync(CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, bytes);
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }
}

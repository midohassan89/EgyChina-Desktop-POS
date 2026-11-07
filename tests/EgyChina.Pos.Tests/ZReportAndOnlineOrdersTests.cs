using System.Net;
using System.Text;
using System.Text.Json;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class ZReportAndOnlineOrdersTests : IDisposable
{
    private readonly string _directory;

    public ZReportAndOnlineOrdersTests()
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
    public void BuildShiftZReport_nets_cash_returns_and_buckets_methods()
    {
        var database = NewDatabase();
        var shift = TestShift.Open(database, openingFloatMinor: 10_000);

        database.CompleteSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز", 1, 8500)],
            10_000,
            shift.CashierId,
            shift.Id,
            PosPaymentMethod.Cash);

        database.CompleteSale(
            [new CashSaleLine("demo-oil", PosDatabase.DemoOilBarcode, "زيت", 1, 6250)],
            6250,
            shift.CashierId,
            shift.Id,
            PosPaymentMethod.Visa);

        database.CompleteSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز", 1, 8500)],
            8500,
            shift.CashierId,
            shift.Id,
            PosPaymentMethod.Cash,
            isReturn: true);

        var report = database.BuildShiftZReport(shift.Id);
        Assert.NotNull(report);
        Assert.Equal(3, report!.TicketCount);
        Assert.Equal(1, report.ReturnTicketCount);
        Assert.Equal(0, report.CashSalesMinor); // 8500 - 8500
        Assert.Equal(6250, report.VisaSalesMinor);
        Assert.Equal(8500, report.CashRefundsMinor);
        Assert.Equal(10_000, report.OpeningFloatMinor);
        Assert.Equal(10_000, report.ExpectedCashMinor); // opening + net cash 0
        Assert.Equal(6250, report.TotalSalesMinor);
        Assert.Equal(500, report.VarianceMinor(10_500));
    }

    [Fact]
    public async Task PosErpGateway_lists_online_orders_from_stub()
    {
        await using var server = await StubHttp.Start(async context =>
        {
            Assert.Equal("GET", context.Request.HttpMethod);
            Assert.StartsWith("/api/pos/online-orders", context.Request.RawUrl, StringComparison.Ordinal);
            var body = """
                {
                  "orders": [
                    {
                      "id": "ord1",
                      "orderNumber": "WEB-100",
                      "customerName": "عميل",
                      "phone": "01000000000",
                      "address": "العبور",
                      "notes": null,
                      "totalAmount": 120.5,
                      "status": "PREPARING",
                      "createdAt": "2026-11-07T10:00:00Z",
                      "assignedDriverName": "سائق",
                      "items": [
                        { "id": "i1", "name": "أرز", "quantity": 2, "price": 60.25 }
                      ]
                    }
                  ],
                  "drivers": []
                }
                """;
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var gateway = new PosErpGateway(server.BaseAddress, "test-device-token");
        var (orders, error) = await gateway.ListOnlineOrdersAsync(CancellationToken.None);
        Assert.Null(error);
        Assert.Single(orders);
        Assert.Equal("WEB-100", orders[0].OrderNumber);
        Assert.Equal("جاري التجهيز", orders[0].StatusLabelAr);
        Assert.True(orders[0].HasAssignedDriver);
        Assert.Single(orders[0].Items);
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }
}

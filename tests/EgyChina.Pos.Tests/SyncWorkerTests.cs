using EgyChina.Pos.Core;
using Xunit;
using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;

namespace EgyChina.Pos.Tests;

public sealed class SyncWorkerTests : IDisposable
{
    private readonly string _directory;

    public SyncWorkerTests()
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
    public async Task Tick_without_an_endpoint_stays_local()
    {
        var database = NewDatabase();
        database.TryEnqueueOutbox("sale", "1", "SaleCompleted", "{}", "term:sale:1");
        var status = new SyncStatus();
        var worker = new SyncWorker(database, status, new SyncOptions
        {
            BaseAddress = null,
            Interval = TimeSpan.FromHours(1),
        });

        await worker.TickAsync(CancellationToken.None);

        var snap = status.Read();
        Assert.False(snap.IsOnline);
        Assert.Equal(1, snap.PendingOutbox);
        Assert.Contains("EGYCHINA_POS_API", snap.Message, StringComparison.Ordinal);
        Assert.Equal(SyncWorker.NotConfiguredCursor, database.ReadCursor(SyncWorker.CursorStream)?.Cursor);
        await worker.DisposeAsync();
    }

    [Fact]
    public void Checkout_uri_is_the_live_browser_route()
    {
        var uri = SyncWorker.CheckoutUri(new Uri("https://erp.echinamart.com"));
        Assert.Equal("https://erp.echinamart.com/api/checkout", uri.ToString());
    }

    [Fact]
    public async Task Tick_with_only_a_base_address_does_not_open_a_socket()
    {
        var database = NewDatabase();
        TestShift.Sell(
            database,
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8500)],
            8500);
        var status = new SyncStatus();
        var worker = new SyncWorker(database, status, new SyncOptions
        {
            BaseAddress = new Uri("https://erp.echinamart.com/"),
            Interval = TimeSpan.FromHours(1),
        });

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal("auth-missing", database.ReadCursor(SyncWorker.CursorStream)?.Cursor);
        Assert.Equal(1, database.CountPendingOutbox());
        Assert.Contains("ربط الجهاز", status.Read().Message, StringComparison.Ordinal);
        await worker.DisposeAsync();
    }

    [Fact]
    public async Task Tick_catalog_pull_runs_without_money_or_payment_gates()
    {
        var database = NewDatabase();
        Assert.Equal("demo", database.GetSetting("catalog.source"));

        await using var server = await StubHttp.Start(async context =>
        {
            Assert.Equal("GET", context.Request.HttpMethod);
            Assert.Contains("/api/products", context.Request.RawUrl, StringComparison.Ordinal);
            var body = """
                {"serverTime":"2026-11-07T12:00:00.000Z","products":[{"id":9042,"sku":"RICE-5KG","prismaId":"clrice001","name":"أرز","price":"12.50","regular_price":"12.50","sale_price":"","meta_data":[{"key":"_op_barcode","value":"6223000000001"}]}],"removedPrismaIds":[]}
                """;
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        var status = new SyncStatus();
        var worker = new SyncWorker(database, status, new SyncOptions
        {
            BaseAddress = server.BaseAddress,
            DeviceToken = "test-device-token",
            CatalogPull = true,
            Interval = TimeSpan.FromHours(1),
            HttpTimeout = TimeSpan.FromSeconds(5),
        });

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal("erp", database.GetSetting("catalog.source"));
        Assert.Equal("idle", database.ReadCursor(SyncWorker.CursorStream)?.Cursor);
        Assert.Contains("الكتالوج محدَّث", status.Read().Message, StringComparison.Ordinal);
        await worker.DisposeAsync();
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        return database;
    }

}

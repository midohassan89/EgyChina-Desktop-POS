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
        Assert.Contains("غير مضبوط", snap.Message, StringComparison.Ordinal);
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
        database.CompleteCashSale(
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
        Assert.Contains("Auth.js", status.Read().Message, StringComparison.Ordinal);
        await worker.DisposeAsync();
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        return database;
    }

}

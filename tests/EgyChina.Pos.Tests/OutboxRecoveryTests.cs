using System.Net;
using System.Net.Sockets;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class OutboxRecoveryTests : IDisposable
{
    private readonly string _directory;

    public OutboxRecoveryTests()
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
    public async Task RecoverInflight_at_worker_start_does_not_drop_sale()
    {
        var database = SeedSale();
        var row = Assert.Single(database.ListPendingSales(5));
        Assert.True(database.MarkOutboxInflight(row.Id));
        Assert.Empty(database.ListPendingSales(5));

        var status = new SyncStatus();
        var worker = new SyncWorker(database, status, new SyncOptions
        {
            BaseAddress = null,
            Interval = TimeSpan.FromHours(1),
        });
        worker.Start();

        Assert.Single(database.ListPendingSales(5));
        AssertSaleIntact(database, synced: false);
        Assert.Equal(1, status.Read().PendingOutbox);
        await worker.DisposeAsync();
    }

    [Fact]
    public void Poison_payload_becomes_dead_and_sale_stays()
    {
        var database = SeedSale();
        var row = Assert.Single(database.ListPendingSales(5));
        // Corrupt qty so mapper rejects permanently.
        RewritePayload(database, row.Id, """
            {"saleId":"x","totalMinor":8500,"lines":[
              {"productId":"demo-rice","nameAr":"أرز","qtyMilli":500,"unitPriceMinor":8500,"lineTotalMinor":8500}
            ]}
            """);

        Assert.True(SaleOutboxSync.IsPoisonBlock("qty-not-whole"));
        database.MarkOutboxDead(row.Id, "qty-not-whole");

        Assert.Equal(0, database.CountPendingOutbox());
        Assert.Equal(1, database.CountDeadOutbox());
        AssertSaleIntact(database, synced: false);
        var snap = database.ReadOutboxSnapshot();
        Assert.Equal(1, snap.Dead);
        Assert.Equal("qty-not-whole", snap.LastError);
    }

    [Fact]
    public async Task Unlinked_product_stays_pending_not_dead()
    {
        var database = SeedSale(link: false);
        var row0 = Assert.Single(database.ListPendingSales(5));
        // Non-demo local id: wait for catalog link. (demo-* is poison and goes dead.)
        RewritePayload(database, row0.Id, $$"""
            {"saleId":"{{row0.AggregateId}}","paymentMethod":"cash","totalMinor":8500,"lines":[
              {"productId":"local-orphan","nameAr":"أرز","qtyMilli":1000,"unitPriceMinor":8500,"lineTotalMinor":8500}
            ]}
            """);
        await using var server = await StubServer.Start(context =>
        {
            Assert.Fail("must not POST an unlinked product");
            return Task.CompletedTask;
        });

        await Tick(database, server.BaseAddress);
        Assert.Equal(1, database.CountPendingOutbox());
        Assert.Equal(0, database.CountDeadOutbox());
        AssertSaleIntact(database, synced: false);
        var row = Assert.Single(database.ListPendingSales(5));
        // After first failure with immediate retry window, force due and confirm still pending.
        database.ForceOutboxDueNow(row.Id);
        Assert.Equal("product-id-not-numeric", ReadError(database, row.Id));
    }

    [Fact]
    public void Backoff_hides_row_until_due()
    {
        var database = SeedSale();
        var row = Assert.Single(database.ListPendingSales(5));
        database.RecordOutboxFailure(row.Id, "transport:blip");
        // First failure is due immediately.
        Assert.Single(database.ListPendingSales(5));

        database.RecordOutboxFailure(row.Id, "transport:blip");
        // Second failure schedules backoff > now.
        Assert.Empty(database.ListPendingSales(5));
        Assert.Equal(1, database.CountPendingOutbox());
        AssertSaleIntact(database, synced: false);

        database.ForceOutboxDueNow(row.Id);
        Assert.Single(database.ListPendingSales(5));
    }

    [Fact]
    public async Task Tick_marks_poison_dead_via_mapper()
    {
        var database = SeedSale();
        var row = Assert.Single(database.ListPendingSales(5));
        RewritePayload(database, row.Id, """
            {"saleId":"sale-1","totalMinor":8500,"lines":[]}
            """);

        await using var server = await StubServer.Start(_ => Task.CompletedTask);
        await Tick(database, server.BaseAddress);

        Assert.Equal(0, database.CountPendingOutbox());
        Assert.Equal(1, database.CountDeadOutbox());
        AssertSaleIntact(database, synced: false);
    }

    private PosDatabase SeedSale(bool link = true)
    {
        var database = new PosDatabase(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        TestShift.Sell(
            database,
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8_500)],
            10_000);
        if (link)
            database.LinkErpNumericId("demo-rice", 55, "p-rice");
        return database;
    }

    private static async Task Tick(PosDatabase database, Uri baseAddress)
    {
        var worker = new SyncWorker(database, new SyncStatus(), new SyncOptions
        {
            BaseAddress = baseAddress,
            DeviceToken = "test-device-token",
            PaymentMethod = "cash",
            MoneyScale = ErpMoneyScale.MajorUnits,
            Interval = TimeSpan.FromHours(1),
        });
        await worker.TickAsync(CancellationToken.None);
        await worker.DisposeAsync();
    }

    private static void AssertSaleIntact(PosDatabase database, bool synced)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT total_minor, receipt_no, sync_status FROM sale";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(8_500, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(synced ? "synced" : "pending", reader.GetString(2));
        Assert.False(reader.Read());
    }

    private static void RewritePayload(PosDatabase database, string outboxId, string payload)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbox SET payload_json = $p WHERE id = $id";
        command.Parameters.AddWithValue("$p", payload);
        command.Parameters.AddWithValue("$id", outboxId);
        Assert.Equal(1, command.ExecuteNonQuery());
        // Ensure due now after rewrite.
        database.ForceOutboxDueNow(outboxId);
    }

    private static string? ReadError(PosDatabase database, string outboxId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_error FROM outbox WHERE id = $id";
        command.Parameters.AddWithValue("$id", outboxId);
        return command.ExecuteScalar() as string;
    }

    private sealed class StubServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private StubServer(HttpListener listener, Func<HttpListenerContext, Task> handler)
        {
            _listener = listener;
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    await handler(context);
                }
            });
        }

        public Uri BaseAddress => new(_listener.Prefixes.First());

        public static async Task<StubServer> Start(Func<HttpListenerContext, Task> handler)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{GetFreePort()}/");
            listener.Start();
            var server = new StubServer(listener, handler);
            await Task.Yield();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            _listener.Close();
            _stop.Dispose();
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}

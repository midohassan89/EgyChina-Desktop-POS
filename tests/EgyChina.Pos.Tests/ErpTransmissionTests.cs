using System.Net;
using System.Text;
using System.Text.Json;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;
using Xunit;

namespace EgyChina.Pos.Tests;

/// <summary>
/// These tests drive the desktop client against a local stand-in.
/// The stand-in is not the ERP. It checks that we speak the request the live bundle sends,
/// and that we acknowledge a sale only after a finished 2xx. Server-side dedupe of localId
/// is not something these tests can prove.
/// </summary>
public sealed class ErpTransmissionTests : IDisposable
{
    private readonly string _directory;

    public ErpTransmissionTests()
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
    public void Major_scale_keeps_piaster_math_exact()
    {
        Assert.Equal(130.00m, ErpCheckoutMapper.Scale(13_000, ErpMoneyScale.MajorUnits));
        Assert.Equal(85.00m, ErpCheckoutMapper.Scale(8_500, ErpMoneyScale.MajorUnits));
        Assert.Equal(8_500m, ErpCheckoutMapper.Scale(8_500, ErpMoneyScale.MinorUnits));
    }

    [Fact]
    public async Task Unauthorized_response_keeps_the_sale_pending()
    {
        var database = SeedSale();
        await using var server = await StubServer.Start(async context =>
        {
            await Write(context, 401, """{"error":"Unauthorized"}""");
        });

        await Tick(database, server.BaseAddress);

        Assert.Equal("unauthorized", database.ReadCursor(SyncWorker.CursorStream)?.Cursor);
        Assert.Equal(1, database.CountPendingOutbox());
        AssertSaleIntact(database, total: 8_500, receipt: 1, synced: false);
    }

    [Fact]
    public async Task Accepted_response_acks_once_and_does_not_replay()
    {
        var database = SeedSale();
        var bodies = new List<string>();
        await using var server = await StubServer.Start(async context =>
        {
            bodies.Add(await Read(context));
            Assert.Equal("POST", context.Request.HttpMethod);
            Assert.Equal("/api/checkout", context.Request.Url?.AbsolutePath);
            Assert.False(string.IsNullOrEmpty(context.Request.Headers["Cookie"]));
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        await Tick(database, server.BaseAddress);
        await Tick(database, server.BaseAddress);

        Assert.Single(bodies);
        using var json = JsonDocument.Parse(bodies[0]);
        var root = json.RootElement;
        Assert.Equal("fixture-method", root.GetProperty("paymentMethod").GetString());
        Assert.Equal(85.00m, root.GetProperty("total").GetDecimal());
        Assert.Equal(85.00m, root.GetProperty("amount").GetDecimal());
        Assert.False(root.GetProperty("isReturn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("wooOrderId").ValueKind);
        Assert.Equal(55, root.GetProperty("lines")[0].GetProperty("productId").GetInt64());
        Assert.Equal(85.00m, root.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(85.00m, root.GetProperty("lines")[0].GetProperty("lineTotal").GetDecimal());
        Assert.Equal(1, root.GetProperty("lines")[0].GetProperty("stockMultiplier").GetInt32());
        Assert.Equal(0, database.CountPendingOutbox());
        AssertSaleIntact(database, total: 8_500, receipt: 1, synced: true);
    }

    [Fact]
    public async Task Lost_response_retries_the_same_local_id_and_acks_only_after_a_full_success()
    {
        var database = SeedSale();
        var localIds = new List<string>();
        var hits = 0;
        await using var server = await StubServer.Start(async context =>
        {
            var body = await Read(context);
            using var json = JsonDocument.Parse(body);
            localIds.Add(json.RootElement.GetProperty("localId").GetString()!);
            hits++;
            if (hits == 1)
            {
                // The stand-in recorded the sale, then the body died before a full response.
                // The live ERP has not been shown to dedupe localId; this only locks the client.
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = 40;
                await context.Response.OutputStream.WriteAsync(new byte[] { (byte)'{' });
                context.Response.Abort();
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        await Tick(database, server.BaseAddress);
        Assert.Equal(1, database.CountPendingOutbox());
        AssertSaleIntact(database, total: 8_500, receipt: 1, synced: false);

        await Tick(database, server.BaseAddress);
        Assert.Equal(localIds[0], localIds[1]);
        Assert.Equal(0, database.CountPendingOutbox());
        AssertSaleIntact(database, total: 8_500, receipt: 1, synced: true);
    }

    [Fact]
    public async Task Restart_requeues_an_inflight_row_before_sending()
    {
        var database = SeedSale();
        var pending = database.ListPendingSales(5);
        Assert.True(database.MarkOutboxInflight(pending[0].Id));
        database.RecoverInflightOutbox();
        Assert.Single(database.ListPendingSales(5));

        await using var server = await StubServer.Start(context =>
        {
            context.Response.StatusCode = 200;
            context.Response.Close();
            return Task.CompletedTask;
        });

        await Tick(database, server.BaseAddress);
        Assert.Equal(0, database.CountPendingOutbox());
    }

    [Fact]
    public async Task String_product_id_is_not_posted()
    {
        var database = SeedSale(link: false);
        var called = 0;
        await using var server = await StubServer.Start(context =>
        {
            called++;
            context.Response.StatusCode = 200;
            context.Response.Close();
            return Task.CompletedTask;
        });

        await Tick(database, server.BaseAddress);

        Assert.Equal(0, called);
        Assert.Equal(1, database.CountPendingOutbox());
        var row = Assert.Single(database.ListPendingSales(5));
        Assert.Equal("product-id-not-numeric", ReadError(database, row.Id));
    }

    [Fact]
    public async Task Catalog_pull_links_numeric_id_without_changing_price()
    {
        var database = SeedSale(link: false);
        await using var server = await StubServer.Start(async context =>
        {
            if (context.Request.HttpMethod == "GET")
            {
                Assert.Equal("/api/products", context.Request.Url?.AbsolutePath);
                await Write(context, 200, """
                    {"serverTime":"2026-11-06T00:00:00Z","products":[{"id":55,"sku":"DEMO-RICE","prismaId":"p-rice","name":"أرز"}],"removedPrismaIds":[]}
                    """);
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        await Tick(database, server.BaseAddress, catalogPull: true);

        Assert.Equal(55, database.TryGetErpNumericId("demo-rice"));
        AssertSaleIntact(database, total: 8_500, receipt: 1, synced: true);
    }

    private PosDatabase SeedSale(bool link = true)
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        database.CompleteCashSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8_500)],
            10_000);
        if (link)
            database.LinkErpNumericId("demo-rice", 55, "p-rice");
        return database;
    }

    private static async Task Tick(PosDatabase database, Uri baseAddress, bool catalogPull = false)
    {
        var worker = new SyncWorker(database, new SyncStatus(), new SyncOptions
        {
            BaseAddress = baseAddress,
            SessionCookie = "test-session=fixture",
            PaymentMethod = "fixture-method",
            MoneyScale = ErpMoneyScale.MajorUnits,
            CatalogPull = catalogPull,
            Interval = TimeSpan.FromHours(1),
        });
        await worker.TickAsync(CancellationToken.None);
        await worker.DisposeAsync();
    }

    private static void AssertSaleIntact(PosDatabase database, long total, int receipt, bool synced)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT total_minor, receipt_no, sync_status FROM sale";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(total, reader.GetInt64(0));
        Assert.Equal(receipt, reader.GetInt64(1));
        Assert.Equal(synced ? "synced" : "pending", reader.GetString(2));
    }

    private static string? ReadError(PosDatabase database, string outboxId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_error FROM outbox WHERE id = $id";
        command.Parameters.AddWithValue("$id", outboxId);
        return command.ExecuteScalar() as string;
    }

    private static async Task Write(HttpListenerContext context, int statusCode, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task<string> Read(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    private sealed class StubServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private StubServer(HttpListener listener, Func<HttpListenerContext, Task> handle)
        {
            _listener = listener;
            BaseAddress = new Uri(listener.Prefixes.First());
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync();
                    }
                    catch (Exception) when (_stop.IsCancellationRequested || !listener.IsListening)
                    {
                        break;
                    }

                    try
                    {
                        await handle(context);
                    }
                    catch (Exception)
                    {
                        try
                        {
                            context.Response.Abort();
                        }
                        catch (Exception)
                        {
                            // The lost-response test aborts on purpose.
                        }
                    }
                }
            });
        }

        public Uri BaseAddress { get; }

        public static Task<StubServer> Start(Func<HttpListenerContext, Task> handle)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = Random.Shared.Next(20000, 45000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return Task.FromResult(new StubServer(listener, handle));
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
            }

            throw new InvalidOperationException("Could not bind a local port.");
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
        }
    }
}

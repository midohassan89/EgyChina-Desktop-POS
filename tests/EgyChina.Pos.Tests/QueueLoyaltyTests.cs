using System.Net;
using System.Text;
using System.Text.Json;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Sync;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class QueueLoyaltyTests : IDisposable
{
    private readonly string _directory;

    public QueueLoyaltyTests()
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
    public void QueueTicketCodes_and_LoyaltyScan_match_erp_rules()
    {
        Assert.True(QueueTicketCodes.IsQueueCode("Q-1001"));
        Assert.True(QueueTicketCodes.IsQueueCode("q-42"));
        Assert.False(QueueTicketCodes.IsQueueCode("POS-1"));
        Assert.False(QueueTicketCodes.IsQueueCode("Q-"));
        Assert.Equal("Q-1001", QueueTicketCodes.Normalize(" q-1001 "));

        Assert.True(LoyaltyScan.IsLoyaltyPhone("01028009271"));
        Assert.True(LoyaltyScan.IsLoyaltyPhone("201028009271"));
        Assert.Equal("01028009271", LoyaltyScan.NormalizePhone("201028009271"));
        Assert.False(LoyaltyScan.IsLoyaltyPhone("0221234567"));

        Assert.Equal(500, LoyaltyScan.DiscountMinorFromPoints(5500, 12_000));
        Assert.Equal(5000, LoyaltyScan.PointsRedeemedForDiscount(500));
        Assert.Equal(850, LoyaltyScan.PointsEarnedForPayable(8_500));
    }

    [Fact]
    public void FindProductForQueueItem_matches_prisma_erp_id_and_barcode()
    {
        var database = NewDatabase();
        LinkDemoRice(database, erpNumericId: 55, prismaId: "clrice001");

        Assert.Equal("demo-rice", database.FindProductForQueueItem("clrice001", null)?.Id);
        Assert.Equal("demo-rice", database.FindProductForQueueItem("55", null)?.Id);
        Assert.Equal("demo-rice", database.FindProductForQueueItem("missing", PosDatabase.DemoRiceBarcode)?.Id);
        Assert.Null(database.FindProductForQueueItem("nope", "000"));
    }

    [Fact]
    public void Sale_with_loyalty_stores_discount_and_payload_fields()
    {
        var database = NewDatabase();
        var shift = TestShift.Open(database);
        var sale = database.CompleteSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8500)],
            7500,
            shift.CashierId,
            shift.Id,
            PosPaymentMethod.Cash,
            discountMinor: 1000,
            loyaltyCustomerId: "cust-1",
            pointsEarned: 750,
            pointsRedeemed: 1000);

        Assert.Equal(7500, sale.TotalMinor);
        Assert.Equal(0, sale.ChangeMinor);

        using var connection = Open(database.DatabasePath);
        using var saleRow = connection.CreateCommand();
        saleRow.CommandText = "SELECT subtotal_minor, discount_minor, total_minor FROM sale WHERE id = $id";
        saleRow.Parameters.AddWithValue("$id", sale.SaleId);
        using var reader = saleRow.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(8500, reader.GetInt64(0));
        Assert.Equal(1000, reader.GetInt64(1));
        Assert.Equal(7500, reader.GetInt64(2));
        reader.Close();

        using var outbox = connection.CreateCommand();
        outbox.CommandText = "SELECT payload_json FROM outbox WHERE aggregate_id = $id";
        outbox.Parameters.AddWithValue("$id", sale.SaleId);
        using var payload = JsonDocument.Parse((string)outbox.ExecuteScalar()!);
        var root = payload.RootElement;
        Assert.Equal("cust-1", root.GetProperty("customerId").GetString());
        Assert.Equal(750, root.GetProperty("pointsEarned").GetInt32());
        Assert.Equal(1000, root.GetProperty("pointsRedeemed").GetInt32());
        Assert.Equal(1000, root.GetProperty("discountMinor").GetInt64());
        Assert.Equal(7500, root.GetProperty("totalMinor").GetInt64());
    }

    [Fact]
    public void ErpCheckoutMapper_includes_loyalty_fields()
    {
        const string payload = """
            {
              "saleId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
              "totalMinor": 7500,
              "isReturn": false,
              "customerId": "cust-9",
              "pointsEarned": 750,
              "pointsRedeemed": 1000,
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
        Assert.Equal("cust-9", root.GetProperty("customerId").GetString());
        Assert.Equal(750, root.GetProperty("pointsEarned").GetInt32());
        Assert.Equal(1000, root.GetProperty("pointsRedeemed").GetInt32());
        Assert.Equal(75.00m, root.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task PosErpGateway_claims_queue_ticket_from_stub()
    {
        await using var server = await StubHttp.Start(async context =>
        {
            Assert.Equal("GET", context.Request.HttpMethod);
            Assert.StartsWith("/api/pos/queue-tickets/Q-1007", context.Request.RawUrl, StringComparison.Ordinal);
            Assert.StartsWith("Bearer ", context.Request.Headers["Authorization"] ?? "", StringComparison.Ordinal);
            var body = """
                {
                  "ok": true,
                  "ticket": {
                    "code": "Q-1007",
                    "status": "CLAIMED",
                    "total": 85,
                    "customerId": null,
                    "customerPhone": null,
                    "customerName": null,
                    "items": [
                      {
                        "productId": "clrice001",
                        "name": "أرز",
                        "barcode": "2000000000018",
                        "quantity": 1,
                        "unitPrice": 85,
                        "isWeighted": false
                      }
                    ]
                  }
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
        var result = await gateway.ClaimQueueTicketAsync("q-1007", CancellationToken.None);
        Assert.True(result.Ok);
        Assert.NotNull(result.Ticket);
        Assert.Equal("Q-1007", result.Ticket!.Code);
        Assert.Single(result.Ticket.Items);
        Assert.Equal("clrice001", result.Ticket.Items[0].ProductId);
    }

    [Fact]
    public async Task PosErpGateway_looks_up_loyalty_phone_from_stub()
    {
        await using var server = await StubHttp.Start(async context =>
        {
            Assert.Equal("GET", context.Request.HttpMethod);
            Assert.Contains("phone=01028009271", context.Request.RawUrl, StringComparison.Ordinal);
            var body = """{"customer":{"id":"c1","name":"عميل","phone":"01028009271","pointsBalance":2500}}""";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var gateway = new PosErpGateway(server.BaseAddress, "test-device-token");
        var (customer, error) = await gateway.LookupLoyaltyPhoneAsync("01028009271", CancellationToken.None);
        Assert.Null(error);
        Assert.NotNull(customer);
        Assert.Equal("c1", customer!.Id);
        Assert.Equal(2500, customer.PointsBalance);
    }

    private PosDatabase NewDatabase()
    {
        var database = new PosDatabase(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }

    private static void LinkDemoRice(PosDatabase database, long erpNumericId, string prismaId)
    {
        database.LinkErpNumericId("demo-rice", erpNumericId, prismaId);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection Open(string path)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ConnectionString);
        connection.Open();
        return connection;
    }
}

internal static class StubHttp
{
    public static async Task<HttpListenerHost> Start(Func<HttpListenerContext, Task> handler)
    {
        var host = new HttpListenerHost(handler);
        await host.StartAsync();
        return host;
    }
}

internal sealed class HttpListenerHost : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public HttpListenerHost(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
    }

    public Uri BaseAddress { get; private set; } = null!;

    public Task StartAsync()
    {
        for (var port = 18_700; port < 18_900; port++)
        {
            try
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/");
                _listener.Prefixes.Add(BaseAddress.ToString());
                _listener.Start();
                _loop = Task.Run(ListenLoop);
                return Task.CompletedTask;
            }
            catch (HttpListenerException)
            {
                _listener.Prefixes.Clear();
            }
        }

        throw new InvalidOperationException("Could not bind stub HTTP listener.");
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _handler(context);
                }
                catch
                {
                    try { context.Response.Abort(); } catch { /* ignore */ }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        _listener.Close();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* ignore */ }
        }

        _cts.Dispose();
    }
}

using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class PaymentMethodTests : IDisposable
{
    private readonly string _directory;

    public PaymentMethodTests()
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
    public void WeChatFx_applies_four_percent_safety_margin()
    {
        var safe = WeChatFx.ApplySafetyMargin(10m);
        Assert.Equal(9.60m, safe);
        Assert.Equal("¥ 12.50", WeChatFx.FormatCny(WeChatFx.EgpMinorToCnyMajor(8750, 7m)));
    }

    [Fact]
    public void PosPaymentMethods_map_erp_and_local_ids()
    {
        Assert.Equal("wechat", PosPaymentMethods.ToErpId(PosPaymentMethod.WeChat));
        Assert.Equal("wallet", PosPaymentMethods.ToLocalDbMethod(PosPaymentMethod.Wallet));
        Assert.True(PosPaymentMethods.ShowsCustomerQr(PosPaymentMethod.InstaPay));
        Assert.False(PosPaymentMethods.IsCash(PosPaymentMethod.Visa));
    }

    [Fact]
    public void CompleteSale_stores_wechat_payment_and_exact_tender()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        Assert.Equal(6, database.GetSchemaVersion());

        var auth = database.TryAuthenticatePin(PinHasher.BootstrapCashierPin);
        Assert.Equal(PinAuthStatus.Ok, auth.Status);
        var shift = database.OpenShift(auth.Cashier!.Id, 0);
        var rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode)!;
        var lines = new[]
        {
            new CashSaleLine(rice.Id, rice.Barcode, rice.NameAr, 1, rice.PriceEgpMinor),
        };

        var sale = database.CompleteSale(
            lines,
            rice.PriceEgpMinor,
            auth.Cashier.Id,
            shift.Id,
            PosPaymentMethod.WeChat,
            wechatEgpPerCny: 7m);

        Assert.Equal(0, sale.ChangeMinor);
        SqliteConnection.ClearAllPools();

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT method FROM payment WHERE sale_id = $id";
        command.Parameters.AddWithValue("$id", sale.SaleId);
        Assert.Equal("wechat", (string)command.ExecuteScalar()!);

        using var outbox = db.CreateCommand();
        outbox.CommandText = "SELECT payload_json FROM outbox WHERE aggregate_id = $id";
        outbox.Parameters.AddWithValue("$id", sale.SaleId);
        var json = (string)outbox.ExecuteScalar()!;
        Assert.Contains("\"paymentMethod\":\"wechat\"", json, StringComparison.Ordinal);
        Assert.Contains("egpPerCny", json, StringComparison.Ordinal);
    }
}

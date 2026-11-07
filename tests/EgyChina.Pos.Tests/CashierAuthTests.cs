using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class CashierAuthTests : IDisposable
{
    private readonly string _directory;

    public CashierAuthTests()
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
    public void Bootstrap_pins_authenticate_cashier_and_supervisor()
    {
        var database = OpenDb();
        var cashier = database.TryAuthenticatePin(PinHasher.BootstrapCashierPin);
        Assert.Equal(PinAuthStatus.Ok, cashier.Status);
        Assert.Equal(PosDatabase.LocalCashierId, cashier.Cashier?.Id);
        Assert.Equal(CashierRole.Cashier, cashier.Cashier?.Role);

        var supervisor = database.TryAuthenticatePin(PinHasher.BootstrapSupervisorPin);
        Assert.Equal(PinAuthStatus.Ok, supervisor.Status);
        Assert.Equal(PosDatabase.LocalSupervisorId, supervisor.Cashier?.Id);
        Assert.Equal(CashierRole.Supervisor, supervisor.Cashier?.Role);
    }

    [Fact]
    public void Wrong_pin_locks_after_five_failures()
    {
        var database = OpenDb();
        for (var i = 0; i < PosDatabase.PinMaxFailures - 1; i++)
        {
            var miss = database.TryAuthenticatePin("000000");
            Assert.Equal(PinAuthStatus.WrongPin, miss.Status);
        }

        var locked = database.TryAuthenticatePin("000000");
        Assert.Equal(PinAuthStatus.Locked, locked.Status);
        Assert.NotNull(locked.LockedUntil);

        var stillLocked = database.TryAuthenticatePin(PinHasher.BootstrapCashierPin);
        Assert.Equal(PinAuthStatus.Locked, stillLocked.Status);

        database.ClearPinLockout();
        var ok = database.TryAuthenticatePin(PinHasher.BootstrapCashierPin);
        Assert.Equal(PinAuthStatus.Ok, ok.Status);
    }

    [Fact]
    public void Sale_requires_open_shift_and_stamps_cashier()
    {
        var database = OpenDb();
        Assert.Throws<InvalidOperationException>(() => database.CompleteCashSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز", 1, 8_500)],
            10_000,
            PosDatabase.LocalCashierId,
            Guid.NewGuid().ToString("D")));

        var shift = TestShift.Open(database, openingFloatMinor: 50_000);
        Assert.Equal(50_000, shift.OpeningFloatMinor);

        var sale = database.CompleteCashSale(
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز", 1, 8_500)],
            10_000,
            PosDatabase.LocalCashierId,
            shift.Id);

        Assert.Equal(PosDatabase.LocalCashierId, sale.CashierId);
        Assert.Equal(shift.Id, sale.ShiftId);

        var closed = database.CloseShift(shift.Id, PosDatabase.LocalCashierId, 55_000);
        Assert.Equal("closed", closed.Status);
        Assert.Equal(55_000, closed.ClosingCashMinor);
        Assert.Null(database.GetOpenShift());
    }

    [Fact]
    public void Supervisor_can_close_another_cashiers_shift()
    {
        var database = OpenDb();
        var shift = TestShift.Open(database);
        var closed = database.CloseShift(shift.Id, PosDatabase.LocalSupervisorId, 0);
        Assert.Equal("closed", closed.Status);
        Assert.Equal(PosDatabase.LocalCashierId, closed.CashierId);
    }

    private PosDatabase OpenDb()
    {
        var database = new PosDatabase(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();
        return database;
    }
}

using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

internal static class TestShift
{
    public static ShiftRecord Open(
        PosDatabase database,
        string? cashierId = null,
        long openingFloatMinor = 0)
    {
        var pin = string.Equals(cashierId, PosDatabase.LocalSupervisorId, StringComparison.Ordinal)
            ? PinHasher.BootstrapSupervisorPin
            : PinHasher.BootstrapCashierPin;
        var auth = database.TryAuthenticatePin(pin);
        Assert.Equal(PinAuthStatus.Ok, auth.Status);
        Assert.NotNull(auth.Cashier);
        var id = cashierId ?? auth.Cashier.Id;
        return database.OpenShift(id, openingFloatMinor);
    }

    public static CompletedCashSale Sell(
        PosDatabase database,
        IReadOnlyList<CashSaleLine> lines,
        long tenderMinor,
        string? cashierId = null)
    {
        var shift = database.GetOpenShift() ?? Open(database, cashierId);
        var cashier = cashierId ?? shift.CashierId;
        return database.CompleteCashSale(lines, tenderMinor, cashier, shift.Id);
    }
}

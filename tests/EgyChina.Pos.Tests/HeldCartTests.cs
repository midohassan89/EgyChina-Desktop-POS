using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class HeldCartTests : IDisposable
{
    private readonly string _directory;

    public HeldCartTests()
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
    public void Hold_list_resume_and_discard_roundtrip()
    {
        var database = new PosDatabase(Path.Combine(_directory, "pos.db"));
        database.Initialize();
        Assert.Equal(5, database.GetSchemaVersion());

        var rice = database.FindByBarcode(PosDatabase.DemoRiceBarcode)!;
        var oil = database.FindByBarcode(PosDatabase.DemoOilBarcode)!;
        var lines = new[]
        {
            new HeldCartLinePayload(rice.Id, rice.Barcode, rice.NameAr, rice.NameZh, 2, rice.PriceEgpMinor),
            new HeldCartLinePayload(oil.Id, oil.Barcode, oil.NameAr, oil.NameZh, 1, oil.PriceEgpMinor),
        };

        var held = database.HoldCart(lines, PosDatabase.LocalCashierId);
        Assert.Equal(1, database.CountHeldCarts());
        Assert.Equal(2 + 1, held.ItemCount);
        Assert.Equal(rice.PriceEgpMinor * 2 + oil.PriceEgpMinor, held.TotalMinor);

        var listed = database.ListHeldCarts();
        Assert.Single(listed);
        Assert.Equal(held.Id, listed[0].Id);

        var restored = database.TakeHeldCart(held.Id);
        Assert.NotNull(restored);
        Assert.Equal(2, restored.Count);
        Assert.Equal(0, database.CountHeldCarts());
        Assert.Equal(2, restored[0].Qty);
        Assert.Equal(rice.Id, restored[0].ProductId);

        var again = database.HoldCart(lines);
        Assert.True(database.DiscardHeldCart(again.Id));
        Assert.Equal(0, database.CountHeldCarts());
    }

    [Fact]
    public void Hold_rejects_empty_cart()
    {
        var database = new PosDatabase(Path.Combine(_directory, "empty.db"));
        database.Initialize();
        Assert.Throws<InvalidOperationException>(() => database.HoldCart(Array.Empty<HeldCartLinePayload>()));
    }
}

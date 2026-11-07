using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Printing;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class PrintingTests : IDisposable
{
    private readonly string _directory;

    public PrintingTests()
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
    public void Raster_encoder_emits_initialize_gs_v0_and_cut()
    {
        using var bitmap = ReceiptBitmapRenderer.Render(SampleDocument());
        Assert.Equal(EscPosRasterEncoder.PageWidthDots, bitmap.Width);
        Assert.True(bitmap.Height > 80);

        var bytes = EscPosRasterEncoder.Encode(bitmap, kickDrawer: true);
        Assert.True(bytes.Length > 100);
        Assert.Equal(0x1B, bytes[0]);
        Assert.Equal(0x40, bytes[1]);
        Assert.Contains((byte)0x1D, bytes); // GS
        Assert.Equal(0x1B, bytes[^5]);
        Assert.Equal(0x70, bytes[^4]); // drawer kick
    }

    [Fact]
    public async Task File_transport_prints_after_sale_and_supports_reprint()
    {
        var database = new PosDatabase(Path.Combine(_directory, "print.db"));
        database.Initialize();
        var sale = TestShift.Sell(
            database,
            [new CashSaleLine("demo-rice", PosDatabase.DemoRiceBarcode, "أرز مصري ٥ كجم", 1, 8_500)],
            10_000);

        var path = Path.Combine(_directory, "receipt.bin");
        var printer = EscPosReceiptPrinter.Create(new PrinterOptions
        {
            Transport = PrinterTransportKind.File,
            FilePath = path,
        });
        Assert.True(printer.IsConfigured);

        var first = database.TryBuildReceiptDocument(sale.SaleId, isReprint: false, kickDrawer: true);
        Assert.NotNull(first);
        await printer.PrintAsync(first, CancellationToken.None);
        Assert.True(File.Exists(path));
        var firstBytes = await File.ReadAllBytesAsync(path);
        Assert.True(firstBytes.Length > 100);

        var reprint = database.TryBuildReceiptDocument(sale.SaleId, isReprint: true);
        Assert.NotNull(reprint);
        Assert.True(reprint.IsReprint);
        Assert.Equal(sale.ReceiptNo, reprint.ReceiptNo);
        Assert.Equal(8_500, reprint.TotalMinor);
        Assert.Equal(10_000, reprint.TenderMinor);
        Assert.Equal(1_500, reprint.ChangeMinor);

        await printer.PrintAsync(reprint, CancellationToken.None);
        var secondBytes = await File.ReadAllBytesAsync(path);
        Assert.True(secondBytes.Length > 100);
        Assert.Equal(sale.SaleId, database.GetLastCompletedSaleId());
    }

    [Fact]
    public void Null_printer_is_not_configured()
    {
        var printer = EscPosReceiptPrinter.Create(new PrinterOptions());
        Assert.False(printer.IsConfigured);
        Assert.IsType<NullReceiptPrinter>(printer);
    }

    private static ReceiptDocument SampleDocument() =>
        new(
            StoreNameAr: "ايجي شاينا ماركت",
            StoreNameZh: "埃及中国商场",
            ReceiptNo: 12,
            CompletedAt: DateTimeOffset.Parse("2026-11-07T10:00:00Z"),
            CashierName: "كاشير محلي",
            Lines:
            [
                new ReceiptLine("أرز مصري ٥ كجم", "大米", 1000, 8_500, 8_500),
            ],
            TotalMinor: 8_500,
            TenderMinor: 10_000,
            ChangeMinor: 1_500,
            FooterAr: "سوق العبور");
}

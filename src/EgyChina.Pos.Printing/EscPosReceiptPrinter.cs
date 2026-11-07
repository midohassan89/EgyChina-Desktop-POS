using EgyChina.Pos.Core;
using SkiaSharp;

namespace EgyChina.Pos.Printing;

/// <summary>
/// Renders a receipt bitmap then sends ESC/POS raster bytes through the configured transport.
/// Never throws into the sale commit path when used with fire-and-forget from the UI.
/// </summary>
public sealed class EscPosReceiptPrinter : IReceiptPrinter
{
    private readonly IPrinterTransport _transport;

    public EscPosReceiptPrinter(IPrinterTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public bool IsConfigured => true;

    public async Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var bitmap = ReceiptBitmapRenderer.Render(document);
        var bytes = EscPosRasterEncoder.Encode(bitmap, document.KickDrawer);
        await _transport.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public Task KickDrawerAsync(CancellationToken cancellationToken) =>
        _transport.WriteAsync(EscPosRasterEncoder.DrawerKickOnly(), cancellationToken);

    public static IReceiptPrinter Create(PrinterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
            return new NullReceiptPrinter();

        IPrinterTransport transport = options.Transport switch
        {
            PrinterTransportKind.RawSpooler when OperatingSystem.IsWindows() =>
                new RawSpoolerTransport(options.QueueName!),
            PrinterTransportKind.RawSpooler =>
                throw new PlatformNotSupportedException("RAW spooler printing requires Windows."),
            PrinterTransportKind.Serial =>
                new SerialPrinterTransport(options.PortName!),
            PrinterTransportKind.File =>
                new FilePrinterTransport(options.FilePath!),
            _ => throw new InvalidOperationException("Printer transport is not configured."),
        };

        return new EscPosReceiptPrinter(transport);
    }
}

using System.IO.Ports;

namespace EgyChina.Pos.Printing;

/// <summary>Secondary transport for Xprinter virtual COM ports.</summary>
public sealed class SerialPrinterTransport : IPrinterTransport
{
    private readonly string _portName;
    private readonly int _baudRate;

    public SerialPrinterTransport(string portName, int baudRate = 9600)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("COM port is required.", nameof(portName));
        _portName = portName.Trim();
        _baudRate = baudRate;
    }

    public Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var port = new SerialPort(_portName, _baudRate)
            {
                WriteTimeout = 5_000,
            };
            port.Open();
            port.Write(payload, 0, payload.Length);
            port.BaseStream.Flush();
        }, cancellationToken);
    }
}

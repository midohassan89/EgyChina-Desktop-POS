namespace EgyChina.Pos.Printing;

public enum PrinterTransportKind
{
    None,
    RawSpooler,
    Serial,
    File,
}

/// <summary>
/// Printer wiring. Env wins over app settings for a till probe.
/// </summary>
public sealed class PrinterOptions
{
    public const string TransportVariable = "EGYCHINA_POS_PRINTER_TRANSPORT";
    public const string QueueVariable = "EGYCHINA_POS_PRINTER_QUEUE";
    public const string PortVariable = "EGYCHINA_POS_PRINTER_PORT";
    public const string FileVariable = "EGYCHINA_POS_PRINTER_FILE";

    public PrinterTransportKind Transport { get; init; } = PrinterTransportKind.None;

    public string? QueueName { get; init; }

    public string? PortName { get; init; }

    public string? FilePath { get; init; }

    public bool IsConfigured => Transport switch
    {
        PrinterTransportKind.RawSpooler => !string.IsNullOrWhiteSpace(QueueName),
        PrinterTransportKind.Serial => !string.IsNullOrWhiteSpace(PortName),
        PrinterTransportKind.File => !string.IsNullOrWhiteSpace(FilePath),
        _ => false,
    };

    public static PrinterOptions FromEnvironment()
    {
        var transportRaw = Environment.GetEnvironmentVariable(TransportVariable)?.Trim().ToLowerInvariant();
        var transport = transportRaw switch
        {
            "raw-spooler" or "spooler" or "raw" => PrinterTransportKind.RawSpooler,
            "serial" or "com" => PrinterTransportKind.Serial,
            "file" => PrinterTransportKind.File,
            _ => PrinterTransportKind.None,
        };

        return new PrinterOptions
        {
            Transport = transport,
            QueueName = Environment.GetEnvironmentVariable(QueueVariable)?.Trim(),
            PortName = Environment.GetEnvironmentVariable(PortVariable)?.Trim(),
            FilePath = Environment.GetEnvironmentVariable(FileVariable)?.Trim(),
        };
    }
}

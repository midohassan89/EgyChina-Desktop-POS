namespace EgyChina.Pos.Printing;

/// <summary>Test/dev sink: writes raw ESC/POS bytes to a file.</summary>
public sealed class FilePrinterTransport : IPrinterTransport
{
    private readonly string _path;

    public FilePrinterTransport(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Printer file path is required.", nameof(path));
        _path = path;
    }

    public async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(_path, payload, cancellationToken).ConfigureAwait(false);
    }
}

namespace EgyChina.Pos.Printing;

public interface IPrinterTransport
{
    Task WriteAsync(byte[] payload, CancellationToken cancellationToken);
}

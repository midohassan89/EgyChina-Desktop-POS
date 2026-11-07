using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EgyChina.Pos.Printing;

/// <summary>
/// Windows RAW spooler path for USB ESC/POS queues (OpenPrinter / WritePrinter).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawSpoolerTransport : IPrinterTransport
{
    private readonly string _queueName;

    public RawSpoolerTransport(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            throw new ArgumentException("Printer queue name is required.", nameof(queueName));
        _queueName = queueName.Trim();
    }

    public Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Task.Run(() => WriteRaw(_queueName, payload, cancellationToken), cancellationToken);
    }

    private static void WriteRaw(string queueName, byte[] payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Native.OpenPrinter(queueName, out var handle, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenPrinter failed for '{queueName}'.");

        try
        {
            var doc = new Native.DOC_INFO_1
            {
                pDocName = "EgyChina POS Receipt",
                pOutputFile = null,
                pDatatype = "RAW",
            };

            if (Native.StartDocPrinter(handle, 1, ref doc) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed.");

            try
            {
                if (!Native.StartPagePrinter(handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed.");

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
                    try
                    {
                        if (!Native.WritePrinter(handle, pinned.AddrOfPinnedObject(), payload.Length, out var written)
                            || written != payload.Length)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter failed.");
                        }
                    }
                    finally
                    {
                        pinned.Free();
                    }
                }
                finally
                {
                    Native.EndPagePrinter(handle);
                }
            }
            finally
            {
                Native.EndDocPrinter(handle);
            }
        }
        finally
        {
            Native.ClosePrinter(handle);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DOC_INFO_1
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pDocName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pOutputFile;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string pDatatype;
        }

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
    }
}

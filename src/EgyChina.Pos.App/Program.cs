using Avalonia;
using System;
using System.Globalization;
using System.Threading;

namespace EgyChina.Pos.App;

sealed class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Arabic copy, Western digits. ar-EG would rewrite money formatting.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        if (!AcquireSingleInstance())
        {
            Console.Error.WriteLine("EgyChina POS is already running.");
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // HD Graphics 520 on the cashier PCs is a poor Direct3D host, and Avalonia 12's
            // default WinUI composition has timed out in real apps. A static POS form is
            // cheaper and more predictable on the CPU. Ignored on Linux dev machines.
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
                DpiAwareness = Win32DpiAwareness.PerMonitorDpiAware,
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();

    private static bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(false, "EgyChina.Pos.SingleInstance");
        try
        {
            return _instanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}

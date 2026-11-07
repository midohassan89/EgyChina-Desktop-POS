using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace EgyChina.Pos.App.Services;

/// <summary>Short success beep after a product is added to the cart.</summary>
public static class ScanSound
{
    public static void PlaySuccess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        _ = Task.Run(PlayWindowsBeep);
    }

    [SupportedOSPlatform("windows")]
    private static void PlayWindowsBeep()
    {
        try
        {
            Console.Beep(1320, 55);
        }
        catch (Exception)
        {
            // Ignore failures on locked audio devices / headless sessions.
        }
    }
}

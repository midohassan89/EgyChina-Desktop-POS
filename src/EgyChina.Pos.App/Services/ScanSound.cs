using System;
using System.Threading.Tasks;

namespace EgyChina.Pos.App.Services;

/// <summary>Short success beep after a product is added to the cart.</summary>
public static class ScanSound
{
    public static void PlaySuccess()
    {
        _ = Task.Run(static () =>
        {
            try
            {
                Console.Beep(1320, 55);
            }
            catch (Exception)
            {
                // Headless / non-Windows hosts — ignore.
            }
        });
    }
}

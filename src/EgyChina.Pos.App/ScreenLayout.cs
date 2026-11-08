using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.App;

internal readonly record struct ScreenPlacement(int ScreenCount, bool CustomerOnSecondary);

/// <summary>
/// Cashier till layout: kiosk FullScreen by default (covers the Windows taskbar).
/// Touch cashiers exit via double-tap on the header or «المزيد → نافذة / ملء الشاشة».
/// Set <c>EGYCHINA_POS_WINDOWED=1</c> to start windowed (laptop / developer).
/// </summary>
internal static class ScreenLayout
{
    public const string WindowedEnvironmentVariable = "EGYCHINA_POS_WINDOWED";

    /// <summary>Default true on tills. False = Maximized inside the working area (taskbar visible).</summary>
    public static bool CashierKiosk { get; set; } = PreferKioskAtStartup();

    public static ScreenPlacement Place(Window cashier, Window customer, bool moveCashier)
    {
        var screens = cashier.Screens;
        if (screens is null || screens.ScreenCount == 0)
            return new ScreenPlacement(0, false);

        var primary = screens.Primary ?? screens.All[0];
        Screen? secondary = null;
        foreach (var screen in screens.All)
        {
            if (!SameBounds(screen.Bounds, primary.Bounds))
            {
                secondary = screen;
                break;
            }
        }

        if (moveCashier)
            PlaceCashier(cashier, primary);

        if (secondary is not null)
            PlaceCustomerFull(customer, secondary);
        else
            PlaceCustomerBeside(customer, primary);

        PosLog.Info(
            $"screens={screens.ScreenCount} dedicatedCustomer={secondary is not null} kiosk={CashierKiosk} primary={primary.Bounds}");
        return new ScreenPlacement(screens.ScreenCount, secondary is not null);
    }

    public static void ApplyCashierDisplayMode(Window cashier)
    {
        var screens = cashier.Screens;
        if (screens is null || screens.ScreenCount == 0)
            return;
        var primary = screens.Primary ?? screens.All[0];
        PlaceCashier(cashier, primary);
    }

    public static bool ToggleCashierKiosk(Window cashier)
    {
        CashierKiosk = !CashierKiosk;
        ApplyCashierDisplayMode(cashier);
        return CashierKiosk;
    }

    private static bool PreferKioskAtStartup()
    {
        var raw = Environment.GetEnvironmentVariable(WindowedEnvironmentVariable)?.Trim();
        if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "windowed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static void PlaceCashier(Window cashier, Screen primary)
    {
        cashier.WindowStartupLocation = WindowStartupLocation.Manual;

        if (CashierKiosk)
        {
            // Full screen over the taskbar — standard retail touch / kiosk till.
            cashier.WindowDecorations = WindowDecorations.None;
            cashier.WindowState = WindowState.Normal;
            cashier.Position = primary.Bounds.Position;
            cashier.WindowState = WindowState.FullScreen;
            return;
        }

        // Windowed / maximized to the working area so the taskbar stays reachable.
        cashier.WindowDecorations = WindowDecorations.Full;
        cashier.WindowState = WindowState.Normal;
        cashier.Position = primary.WorkingArea.Position;
        cashier.WindowState = WindowState.Maximized;
    }

    private static void PlaceCustomerBeside(Window customer, Screen primary)
    {
        var area = primary.WorkingArea;
        var scale = primary.Scaling <= 0 ? 1 : primary.Scaling;
        var width = Math.Max(420, area.Width / scale * 0.36);
        var height = Math.Max(560, area.Height / scale - 32);
        customer.WindowDecorations = WindowDecorations.Full;
        customer.Topmost = false;
        customer.WindowState = WindowState.Normal;
        customer.Width = width;
        customer.Height = height;
        var x = area.X + area.Width - (int)Math.Round(width * scale) - 12;
        customer.Position = new PixelPoint(x, area.Y + 12);
    }

    private static void PlaceCustomerFull(Window customer, Screen secondary)
    {
        customer.WindowDecorations = WindowDecorations.None;
        customer.Topmost = true;
        customer.WindowState = WindowState.Normal;
        customer.Position = secondary.Bounds.Position;
        customer.WindowState = WindowState.FullScreen;
    }

    private static bool SameBounds(PixelRect left, PixelRect right) =>
        left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;
}

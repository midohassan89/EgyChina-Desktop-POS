using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.App;

internal readonly record struct ScreenPlacement(int ScreenCount, bool CustomerOnSecondary);

/// <summary>
/// Puts the cashier on the primary screen and the customer window on the other one.
/// Bounds from the platform are physical pixels. Window.Width is DIPs, so fullscreen
/// placement sets position and then the window state instead of copying pixel width.
/// </summary>
internal static class ScreenLayout
{
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
            PlaceCashier(cashier, primary, secondary is not null);

        if (secondary is not null)
            PlaceCustomerFull(customer, secondary);
        else
            PlaceCustomerBeside(customer, primary);

        PosLog.Info($"screens={screens.ScreenCount} dedicatedCustomer={secondary is not null} primary={primary.Bounds}");
        return new ScreenPlacement(screens.ScreenCount, secondary is not null);
    }

    private static void PlaceCashier(Window cashier, Screen primary, bool dedicated)
    {
        var area = primary.WorkingArea;
        cashier.WindowStartupLocation = WindowStartupLocation.Manual;
        cashier.WindowState = WindowState.Normal;
        cashier.Position = area.Position;

        if (dedicated)
        {
            cashier.WindowState = WindowState.Maximized;
            return;
        }

        var scale = primary.Scaling <= 0 ? 1 : primary.Scaling;
        cashier.Width = Math.Max(720, area.Width / scale * 0.60);
        cashier.Height = Math.Max(560, area.Height / scale - 32);
        cashier.Position = new PixelPoint(area.X + 12, area.Y + 12);
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

using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgyChina.Pos.App.ViewModels;

namespace EgyChina.Pos.App.Helpers;

/// <summary>Scrolls a cart / CFD list so the touched line stays in view.</summary>
internal static class CartLineScroll
{
    public static void Reveal(ScrollViewer? scroll, CartLine? line, string lineClass, int attempt = 0)
    {
        if (scroll is null || line is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var border = scroll.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.DataContext, line)
                    && candidate.Classes.Contains(lineClass));

            if (border is not null)
            {
                border.BringIntoView();
                return;
            }

            if (attempt >= 8)
                return;

            // New rows need a layout pass before the container exists.
            Dispatcher.UIThread.Post(
                () => Reveal(scroll, line, lineClass, attempt + 1),
                DispatcherPriority.Background);
        }, attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.Background);
    }
}

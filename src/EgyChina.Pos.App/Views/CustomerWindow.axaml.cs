using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EgyChina.Pos.App.ViewModels;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.App.Views;

public partial class CustomerWindow : Window
{
    private PosSession? _session;

    public CustomerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachSession();
        Opened += (_, _) => ReloadQrImages();
    }

    private void AttachSession()
    {
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session.Lines.CollectionChanged -= OnLinesChanged;
        }

        _session = DataContext as PosSession;
        if (_session is null)
            return;

        _session.PropertyChanged += OnSessionPropertyChanged;
        _session.Lines.CollectionChanged += OnLinesChanged;
        ReloadQrImages();
        ScrollCartToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(ScrollCartToEnd, DispatcherPriority.Background);

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PosSession.ShowWeChatQr)
            or nameof(PosSession.ShowInstaPayQr)
            or nameof(PosSession.IsCfdPayment))
        {
            Dispatcher.UIThread.Post(ReloadQrImages);
        }

        if (e.PropertyName is nameof(PosSession.IsCfdShopping)
            or nameof(PosSession.LineCountText)
            or nameof(PosSession.TotalText))
        {
            Dispatcher.UIThread.Post(ScrollCartToEnd, DispatcherPriority.Background);
        }
    }

    private void ScrollCartToEnd()
    {
        try
        {
            if (CfdCartScroll is null || _session is null || !_session.IsCfdShopping)
                return;
            CfdCartScroll.ScrollToEnd();
        }
        catch (Exception)
        {
            // Layout may not be ready yet.
        }
    }

    private void ReloadQrImages()
    {
        PosPaths.EnsurePaymentQrAssets();
        ApplyQr(WeChatQrImage, WeChatQrFallback, PosPaths.WeChatQrFile());
        ApplyQr(InstaPayQrImage, InstaPayQrFallback, PosPaths.InstaPayQrFile());
    }

    private static void ApplyQr(Image image, TextBlock fallback, string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                using var stream = File.OpenRead(path);
                image.Source = new Bitmap(stream);
                image.IsVisible = true;
                fallback.IsVisible = false;
                return;
            }
        }
        catch (Exception)
        {
        }

        image.Source = null;
        image.IsVisible = false;
        fallback.IsVisible = true;
    }
}

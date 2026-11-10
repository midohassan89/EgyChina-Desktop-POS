using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EgyChina.Pos.App.Helpers;
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
            _session.CartLineRevealRequested -= OnCartLineRevealRequested;
        }

        _session = DataContext as PosSession;
        if (_session is null)
            return;

        _session.PropertyChanged += OnSessionPropertyChanged;
        _session.Lines.CollectionChanged += OnLinesChanged;
        _session.CartLineRevealRequested += OnCartLineRevealRequested;
        ReloadQrImages();
        RevealHighlightedLine();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RevealHighlightedLine();

    private void OnCartLineRevealRequested(CartLine line) =>
        CartLineScroll.Reveal(CfdCartScroll, line, "cfd-line");

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PosSession.ShowWeChatQr)
            or nameof(PosSession.ShowInstaPayQr)
            or nameof(PosSession.IsCfdPayment))
        {
            Dispatcher.UIThread.Post(ReloadQrImages);
        }

        if (e.PropertyName is nameof(PosSession.IsCfdShopping))
            RevealHighlightedLine();
    }

    private void RevealHighlightedLine()
    {
        if (_session is null || !_session.IsCfdShopping)
            return;

        var line = _session.Lines.FirstOrDefault(item => item.IsHighlighted)
                   ?? _session.Lines.LastOrDefault();
        if (line is null)
            return;

        CartLineScroll.Reveal(CfdCartScroll, line, "cfd-line");
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

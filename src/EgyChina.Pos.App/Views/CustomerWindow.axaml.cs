using System;
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
            _session.PropertyChanged -= OnSessionPropertyChanged;

        _session = DataContext as PosSession;
        if (_session is null)
            return;

        _session.PropertyChanged += OnSessionPropertyChanged;
        ReloadQrImages();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PosSession.ShowWeChatQr)
            or nameof(PosSession.ShowInstaPayQr)
            or nameof(PosSession.IsCfdPayment))
        {
            Dispatcher.UIThread.Post(ReloadQrImages);
        }
    }

    private void ReloadQrImages()
    {
        Directory.CreateDirectory(PosPaths.AssetsDirectory());
        ApplyQr(WeChatQrImage, WeChatQrFallback, PosPaths.WeChatQrFile());
        ApplyQr(InstaPayQrImage, InstaPayQrFallback, PosPaths.InstaPayQrFile());
    }

    private static void ApplyQr(Image image, TextBlock fallback, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                image.Source = new Bitmap(path);
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

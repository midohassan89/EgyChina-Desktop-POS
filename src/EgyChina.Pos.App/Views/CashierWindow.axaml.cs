using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EgyChina.Pos.App.ViewModels;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.App.Views;

public partial class CashierWindow : Window
{
    private readonly DispatcherTimer _clock;
    private PosSession? _session;

    public CashierWindow()
    {
        InitializeComponent();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            if (DataContext is PosSession session)
                session.SetClock(DateTime.Now);
        };
        Opened += (_, _) =>
        {
            _clock.Start();
            FocusBarcode();
        };
        Closed += (_, _) => _clock.Stop();
        DataContextChanged += (_, _) => AttachSession();
    }

    private void AttachSession()
    {
        if (_session is not null)
            _session.TenderOpened -= FocusTender;
        _session = DataContext as PosSession;
        if (_session is not null)
            _session.TenderOpened += FocusTender;
    }

    private void FocusTender()
    {
        Dispatcher.UIThread.Post(() =>
        {
            TenderBox.Focus();
            TenderBox.SelectAll();
        });
    }

    private void OnTenderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PosSession session)
            return;
        e.Handled = true;
        if (session.CanConfirmCash)
            session.ConfirmCashCommand.Execute(null);
    }

    private void OnBarcodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        SubmitBarcode();
    }

    private void OnBarcodeSubmit(object? sender, RoutedEventArgs e) => SubmitBarcode();

    private void OnProductTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border { DataContext: CatalogProduct product } || DataContext is not PosSession session)
            return;

        session.AddProduct(product);
        FocusBarcode();
    }

    private void SubmitBarcode()
    {
        if (DataContext is not PosSession session)
            return;

        session.SubmitBarcode();
        FocusBarcode();
    }

    private void FocusBarcode()
    {
        BarcodeBox.Focus();
        BarcodeBox.SelectAll();
    }
}

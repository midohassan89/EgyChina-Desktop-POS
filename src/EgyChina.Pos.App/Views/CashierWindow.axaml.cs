using System;
using System.ComponentModel;
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
            FocusForSession();
        };
        Closed += (_, _) => _clock.Stop();
        DataContextChanged += (_, _) => AttachSession();
        KeyDown += OnWindowKeyDown;
        AddHandler(InputElement.TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
    }

    private void AttachSession()
    {
        if (_session is not null)
        {
            _session.TenderOpened -= FocusTender;
            _session.FocusRequested -= OnFocusRequested;
            _session.PropertyChanged -= OnSessionPropertyChanged;
        }

        _session = DataContext as PosSession;
        if (_session is null)
            return;

        _session.TenderOpened += FocusTender;
        _session.FocusRequested += OnFocusRequested;
        _session.PropertyChanged += OnSessionPropertyChanged;
        FocusForSession();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PosSession.NeedsLogin)
            or nameof(PosSession.NeedsOpenShift)
            or nameof(PosSession.ShowCloseShift)
            or nameof(PosSession.IsTendering)
            or nameof(PosSession.ShowNotFound)
            or nameof(PosSession.ShowPriceCheck)
            or nameof(PosSession.ShowHeldCarts)
            or nameof(PosSession.ShowSupervisorPin)
            or nameof(PosSession.ShowOnlineOrders))
        {
            Dispatcher.UIThread.Post(FocusForSession);
        }
    }

    private void OnFocusRequested(UiFocusTarget target) =>
        Dispatcher.UIThread.Post(() => ApplyFocus(target));

    private void FocusTender() => ApplyFocus(UiFocusTarget.Tender);

    private void FocusForSession()
    {
        if (_session is null)
        {
            FocusBarcode();
            return;
        }

        if (_session.NeedsLogin)
            ApplyFocus(UiFocusTarget.Pin);
        else if (_session.NeedsOpenShift)
            ApplyFocus(UiFocusTarget.OpeningFloat);
        else if (_session.ShowCloseShift)
            ApplyFocus(UiFocusTarget.ClosingCash);
        else if (_session.ShowSupervisorPin)
            ApplyFocus(UiFocusTarget.SupervisorPin);
        else if (_session.ShowNotFound)
            ApplyFocus(UiFocusTarget.NotFoundAck);
        else if (_session.ShowPriceCheck)
            ApplyFocus(UiFocusTarget.PriceCheck);
        else if (_session.IsTendering)
            ApplyFocus(UiFocusTarget.Tender);
        else
            ApplyFocus(UiFocusTarget.Barcode);
    }

    private void ApplyFocus(UiFocusTarget target)
    {
        switch (target)
        {
            case UiFocusTarget.Pin:
                PinBox.Focus();
                PinBox.SelectAll();
                break;
            case UiFocusTarget.OpeningFloat:
                OpeningFloatBox.Focus();
                OpeningFloatBox.SelectAll();
                break;
            case UiFocusTarget.ClosingCash:
                ClosingCashBox.Focus();
                ClosingCashBox.SelectAll();
                break;
            case UiFocusTarget.Tender:
                TenderBox.Focus();
                TenderBox.SelectAll();
                break;
            case UiFocusTarget.PriceCheck:
                PriceCheckBox.Focus();
                PriceCheckBox.SelectAll();
                break;
            case UiFocusTarget.NotFoundAck:
                NotFoundOkButton.Focus();
                break;
            case UiFocusTarget.SupervisorPin:
                SupervisorPinBox.Focus();
                SupervisorPinBox.SelectAll();
                break;
            default:
                FocusBarcode();
                break;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PosSession session)
            return;

        if (e.Key == Key.Escape)
        {
            if (session.ShowSupervisorPin)
            {
                session.CancelSupervisorPinCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (session.ShowOnlineOrders)
            {
                session.CloseOnlineOrdersCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (session.ShowNotFound)
            {
                session.DismissNotFoundCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (session.ShowPriceCheck)
            {
                session.ClosePriceCheckCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (session.ShowHeldCarts)
            {
                session.CloseHeldCartsCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (session.ShowCloseShift)
            {
                session.CancelCloseShiftCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (session.IsTendering)
            {
                session.CancelTenderCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (session.ShowNotFound && (e.Key == Key.Enter || e.Key == Key.Return))
        {
            session.DismissNotFoundCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2 && session.CanLock)
        {
            session.LockSessionCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            session.ShowCustomerDisplayCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F4)
        {
            session.OpenPriceCheckCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && session.CanReprint)
        {
            session.ReprintLastCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            if (session.IsReturnMode || session.CanToggleReturnMode)
            {
                session.ToggleReturnModeCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.F7 && session.CanOpenDrawer)
        {
            session.OpenCashDrawerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F8 && session.CanOpenOnlineOrders)
        {
            session.OpenOnlineOrdersCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not PosSession session || e.Handled)
            return;

        var overlayOpen = session.NeedsLogin
            || session.NeedsOpenShift
            || session.ShowCloseShift
            || session.IsTendering
            || session.ShowSupervisorPin
            || session.ShowOnlineOrders
            || session.BlocksScanning;
        var searchFocused = SearchBox.IsFocused;
        var barcodeFocused = BarcodeBox.IsFocused;
        if (!ScannerInput.ShouldStealFromSearch(searchFocused, barcodeFocused, overlayOpen, e.Text))
            return;

        session.Barcode += e.Text;
        FocusBarcode();
        e.Handled = true;
    }

    private void OnTenderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PosSession session)
            return;
        e.Handled = true;
        if (session.CanConfirmCash)
            session.ConfirmCashCommand.Execute(null);
    }

    private void OnPinKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PosSession session)
            return;
        e.Handled = true;
        session.SubmitPinCommand.Execute(null);
    }

    private void OnOpeningFloatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PosSession session)
            return;
        e.Handled = true;
        session.OpenShiftCommand.Execute(null);
    }

    private void OnClosingCashKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PosSession session)
            return;
        e.Handled = true;
        session.ConfirmCloseShiftCommand.Execute(null);
    }

    private void OnPriceCheckKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PosSession session)
            return;
        e.Handled = true;
        session.SubmitPriceCheckCommand.Execute(null);
    }

    private void OnSupervisorPinKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PosSession session)
            return;
        e.Handled = true;
        session.SubmitSupervisorPinCommand.Execute(null);
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

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: CatalogProduct product } || DataContext is not PosSession session)
            return;
        session.ToggleFavoriteCommand.Execute(product);
    }

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: CatalogCategory category } || DataContext is not PosSession session)
            return;
        session.SelectCategoryCommand.Execute(category);
    }

    private void OnMoreActionsBackdrop(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is not PosSession session)
            return;
        // Close only when clicking the dimmed backdrop, not the menu card.
        if (e.Source is Border border && ReferenceEquals(border, sender))
        {
            session.CloseMoreActionsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResumeHeldClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: HeldCartSummary held } || DataContext is not PosSession session)
            return;
        session.ResumeHeldCartCommand.Execute(held);
    }

    private void OnDiscardHeldClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: HeldCartSummary held } || DataContext is not PosSession session)
            return;
        session.DiscardHeldCartCommand.Execute(held);
    }

    private void OnPaymentMethodClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string tag } || DataContext is not PosSession session)
            return;
        if (!Enum.TryParse<PosPaymentMethod>(tag, ignoreCase: true, out var method))
            return;
        session.SelectPaymentCommand.Execute(method);
    }

    private void SubmitBarcode()
    {
        if (DataContext is not PosSession session)
            return;

        session.SubmitBarcode();
        if (!session.ShowNotFound)
            FocusBarcode();
    }

    private void FocusBarcode()
    {
        BarcodeBox.Focus();
        BarcodeBox.SelectAll();
    }
}

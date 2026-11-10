using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgyChina.Pos.App.Controls;
using EgyChina.Pos.App.ViewModels;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.App.Views;

public partial class CashierWindow : Window
{
    private readonly DispatcherTimer _clock;
    private readonly StringBuilder _wedgeBuffer = new();
    private PosSession? _session;
    private TextBox? _oskTarget;
    private double _oskLeft = double.NaN;
    private double _oskWidth = double.NaN;
    private double _oskHeightCap = double.NaN;
    /// <summary>When false, the next GotFocus must not SelectAll (OSK refocus mid-typing).</summary>
    private bool _selectAllOnFocus = true;

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
            WireOnScreenKeyboard();
            ApplyOskLayout();
            FocusForSession();
        };
        Closed += (_, _) => _clock.Stop();
        DataContextChanged += (_, _) => AttachSession();
        SizeChanged += (_, _) => ApplyOskLayout();
        CatalogStage.LayoutUpdated += (_, _) => ApplyOskLayout();
        KeyDown += OnWindowKeyDown;
        // Tunnel first: USB wedge types into whichever control has focus — steal codes globally.
        AddHandler(InputElement.TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // Dismiss OSK when tapping empty / product / cart chrome (not the keyboard or a TextBox).
        AddHandler(InputElement.PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Tunnel);
    }

    private IEnumerable<TextBox> EnumerateTouchBoxes()
    {
        yield return BarcodeBox;
        yield return TenderBox;
        yield return ExchangeRateBox;
        yield return PinBox;
        yield return OpeningFloatBox;
        yield return ClosingCashBox;
        yield return PriceCheckBox;
        yield return SupervisorPinBox;
        yield return EditQtyBox;
        yield return EditUnitPriceBox;
        yield return EditDiscountBox;
    }

    private void WireOnScreenKeyboard()
    {
        OnScreenKeyboard.TextTyped -= OnOskTextTyped;
        OnScreenKeyboard.BackspacePressed -= OnOskBackspace;
        OnScreenKeyboard.EnterPressed -= OnOskEnter;
        OnScreenKeyboard.HideRequested -= OnOskHide;
        OnScreenKeyboard.TextTyped += OnOskTextTyped;
        OnScreenKeyboard.BackspacePressed += OnOskBackspace;
        OnScreenKeyboard.EnterPressed += OnOskEnter;
        OnScreenKeyboard.HideRequested += OnOskHide;

        EditLineNumpad.TextTyped -= OnOskTextTyped;
        EditLineNumpad.BackspacePressed -= OnOskBackspace;
        EditLineNumpad.EnterPressed -= OnEditLineNumpadEnter;
        EditLineNumpad.TextTyped += OnOskTextTyped;
        EditLineNumpad.BackspacePressed += OnOskBackspace;
        EditLineNumpad.EnterPressed += OnEditLineNumpadEnter;
        EditLineNumpad.SetMode(TouchKeyboardMode.Numeric);
        EditLineNumpad.SetEmbedded(true);

        foreach (var box in EnumerateTouchBoxes())
        {
            box.RemoveHandler(InputElement.PointerPressedEvent, OnTouchInputPointerPressed);
            box.AddHandler(InputElement.PointerPressedEvent, OnTouchInputPointerPressed, RoutingStrategies.Tunnel);
        }
    }

    private const double CartColumnReserve = 422;
    private const double TenderPadWidth = 420;

    private void ApplyOskLayout()
    {
        // Pin the pad in physical pixels so window RTL cannot shove it over the cart.
        var heightCap = Math.Clamp(Bounds.Height * 0.36, 160, 300);
        double left = 10;
        double width = Math.Max(360, Bounds.Width - CartColumnReserve - 20);
        var tenderPad = ReferenceEquals(_oskTarget, TenderBox);

        if (CatalogStage.Bounds.Width > 1
            && CatalogStage.TranslatePoint(new Avalonia.Point(0, 0), OskLayer) is { } origin)
        {
            if (tenderPad)
            {
                // Narrow pad beside the cart so amount entry stays next to «المبلغ المستلم».
                width = Math.Min(TenderPadWidth, CatalogStage.Bounds.Width);
                left = Math.Max(0, origin.X + CatalogStage.Bounds.Width - width);
                heightCap = Math.Clamp(Bounds.Height * 0.42, 200, 340);
            }
            else
            {
                left = Math.Max(0, origin.X);
                width = Math.Max(280, CatalogStage.Bounds.Width);
            }
        }

        if (left == _oskLeft && width == _oskWidth && heightCap == _oskHeightCap)
            return;

        _oskLeft = left;
        _oskWidth = width;
        _oskHeightCap = heightCap;

        Canvas.SetLeft(OnScreenKeyboardHost, left);
        Canvas.SetBottom(OnScreenKeyboardHost, 8);
        OnScreenKeyboardHost.Width = width;
        OnScreenKeyboardHost.MaxWidth = width;
        OnScreenKeyboardHost.MaxHeight = heightCap;
        OnScreenKeyboard.Width = double.NaN;
        OnScreenKeyboard.MaxWidth = double.PositiveInfinity;
        OnScreenKeyboard.Height = double.NaN;
        OnScreenKeyboard.MaxHeight = heightCap;
        OnScreenKeyboard.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        OnScreenKeyboard.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
    }

    private bool IsEditLineTouchField(TextBox box) =>
        ReferenceEquals(box, EditQtyBox)
        || ReferenceEquals(box, EditUnitPriceBox)
        || ReferenceEquals(box, EditDiscountBox);

    private bool IsNumericTouchField(TextBox box) =>
        ReferenceEquals(box, PinBox)
        || ReferenceEquals(box, SupervisorPinBox)
        || ReferenceEquals(box, TenderBox)
        || ReferenceEquals(box, OpeningFloatBox)
        || ReferenceEquals(box, ClosingCashBox)
        || ReferenceEquals(box, ExchangeRateBox)
        || IsEditLineTouchField(box);

    /// <summary>Dialog PIN/float fields — show pad on focus. Search + tender amount need an explicit tap.</summary>
    private bool ShowsOskOnFocus(TextBox box) =>
        !ReferenceEquals(box, BarcodeBox)
        && !ReferenceEquals(box, TenderBox)
        && !IsEditLineTouchField(box);

    private void PrepareOskFor(TextBox box)
    {
        _oskTarget = box;
        if (IsEditLineTouchField(box))
        {
            // Edit dialog owns a dedicated numeric pad beside the form.
            HideOnScreenKeyboard();
            return;
        }

        var numeric = IsNumericTouchField(box);
        OnScreenKeyboard.SetMode(numeric ? TouchKeyboardMode.Numeric : TouchKeyboardMode.Text);
        if (!numeric)
            OnScreenKeyboard.PreferLanguage(TouchKeyboardLanguage.Arabic);
        // Force remeasure when switching tender ↔ search pad geometry.
        _oskLeft = double.NaN;
        ApplyOskLayout();
    }

    private void ShowOnScreenKeyboard()
    {
        if (_session?.ShowEditLine == true)
            return;
        ApplyOskLayout();
        OnScreenKeyboardHost.IsVisible = true;
        OnScreenKeyboard.IsVisible = true;
    }

    private void HideOnScreenKeyboard()
    {
        OnScreenKeyboard.IsVisible = false;
        OnScreenKeyboardHost.IsVisible = false;
    }

    private void OnTouchInputGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        PrepareOskFor(box);
        // Search / tender amount: pad only after the cashier taps the field (not programmatic focus).
        if (ShowsOskOnFocus(box))
            ShowOnScreenKeyboard();

        // Highlight existing value so the next key replaces it; cashier can cancel selection to edit.
        if (_selectAllOnFocus)
            QueueSelectAll(box);
        _selectAllOnFocus = true;
    }

    private void OnTouchInputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var box = sender as TextBox
                  ?? (e.Source as Control)?.FindAncestorOfType<TextBox>()
                  ?? e.Source as TextBox;
        if (box is null || !EnumerateTouchBoxes().Contains(box))
            return;

        // Already focused: leave caret placement alone so the cashier can edit part of the value.
        PrepareOskFor(box);
        if (IsEditLineTouchField(box))
            return;
        ShowOnScreenKeyboard();
    }

    private void QueueSelectAll(TextBox box)
    {
        // After mouse-down caret placement, so SelectAll wins on first focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsFocused || !_selectAllOnFocus)
                return;
            if (string.IsNullOrEmpty(box.Text))
                return;
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void FocusWithoutSelectAll(TextBox box)
    {
        _selectAllOnFocus = false;
        try
        {
            box.Focus();
        }
        finally
        {
            _selectAllOnFocus = true;
        }
    }

    private static bool IsDialogTypingOpen(PosSession session) =>
        session.NeedsLogin
        || session.NeedsOpenShift
        || session.ShowCloseShift
        || session.IsTendering
        || session.ShowSupervisorPin
        || session.ShowPriceCheck;

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OnScreenKeyboardHost.IsVisible)
            return;

        if (e.Source is not Visual visual)
            return;

        // Keep keyboard while using it or retargeting another input.
        if (OnScreenKeyboardHost.IsVisualAncestorOf(visual)
            || ReferenceEquals(visual, OnScreenKeyboardHost)
            || OnScreenKeyboard.IsVisualAncestorOf(visual)
            || ReferenceEquals(visual, OnScreenKeyboard))
            return;
        if (visual is TextBox || visual.FindAncestorOfType<TextBox>() is not null)
            return;

        // During PIN / tender dialogs the dim overlay would otherwise dismiss the pad.
        if (DataContext is PosSession session && IsDialogTypingOpen(session))
            return;

        HideOnScreenKeyboard();
    }

    private void OnOskTextTyped(string text)
    {
        if (_oskTarget is null)
            return;

        // Ensure the bound TextBox stays the active target (dialogs can steal focus).
        if (!_oskTarget.IsFocused)
            FocusWithoutSelectAll(_oskTarget);

        var start = Math.Clamp(_oskTarget.SelectionStart, 0, _oskTarget.Text?.Length ?? 0);
        var length = Math.Max(0, _oskTarget.SelectionEnd - _oskTarget.SelectionStart);
        var current = _oskTarget.Text ?? string.Empty;
        if (length > 0)
            current = current.Remove(start, length);
        current = current.Insert(start, text);
        _oskTarget.Text = current;
        var caret = start + text.Length;
        _oskTarget.CaretIndex = caret;
        _oskTarget.SelectionStart = caret;
        _oskTarget.SelectionEnd = caret;
    }

    private void OnOskBackspace()
    {
        if (_oskTarget is null)
            return;
        var current = _oskTarget.Text ?? string.Empty;
        var start = Math.Clamp(_oskTarget.SelectionStart, 0, current.Length);
        var end = Math.Clamp(_oskTarget.SelectionEnd, 0, current.Length);
        if (end > start)
        {
            current = current.Remove(start, end - start);
            _oskTarget.Text = current;
            _oskTarget.CaretIndex = start;
        }
        else if (start > 0)
        {
            current = current.Remove(start - 1, 1);
            _oskTarget.Text = current;
            _oskTarget.CaretIndex = start - 1;
        }

        _oskTarget.SelectionStart = _oskTarget.CaretIndex;
        _oskTarget.SelectionEnd = _oskTarget.CaretIndex;
        if (!_oskTarget.IsFocused)
            FocusWithoutSelectAll(_oskTarget);
    }

    private void OnOskEnter()
    {
        if (_oskTarget is null)
            return;
        if (ReferenceEquals(_oskTarget, BarcodeBox))
        {
            SubmitBarcode();
            HideOnScreenKeyboard();
        }
        else if (ReferenceEquals(_oskTarget, PinBox) && _session is not null)
            _session.SubmitPinCommand.Execute(null);
        else if (ReferenceEquals(_oskTarget, SupervisorPinBox) && _session is not null)
            _session.SubmitSupervisorPinCommand.Execute(null);
        else if (ReferenceEquals(_oskTarget, TenderBox) && _session is { CanConfirmCash: true })
            _session.ConfirmCashCommand.Execute(null);
        else if (ReferenceEquals(_oskTarget, OpeningFloatBox) && _session is not null)
            _session.OpenShiftCommand.Execute(null);
        else if (ReferenceEquals(_oskTarget, ClosingCashBox) && _session is not null)
            _session.ConfirmCloseShiftCommand.Execute(null);
        else if (ReferenceEquals(_oskTarget, PriceCheckBox) && _session is not null)
            _session.SubmitPriceCheckCommand.Execute(null);
        else if (IsEditLineTouchField(_oskTarget))
            FocusNextEditLineFieldOrApply();
    }

    private void OnEditLineNumpadEnter() => FocusNextEditLineFieldOrApply();

    private void FocusNextEditLineFieldOrApply()
    {
        if (_session is null || !_session.ShowEditLine)
            return;

        if (_oskTarget is null || ReferenceEquals(_oskTarget, EditQtyBox))
        {
            EditUnitPriceBox.Focus();
            EditUnitPriceBox.SelectAll();
            _oskTarget = EditUnitPriceBox;
            return;
        }

        if (ReferenceEquals(_oskTarget, EditUnitPriceBox))
        {
            if (_session.ShowEditDiscountValue)
            {
                EditDiscountBox.Focus();
                EditDiscountBox.SelectAll();
                _oskTarget = EditDiscountBox;
                return;
            }

            _session.ApplyEditLineCommand.Execute(null);
            return;
        }

        if (ReferenceEquals(_oskTarget, EditDiscountBox))
            _session.ApplyEditLineCommand.Execute(null);
    }

    private void OnOskHide()
    {
        HideOnScreenKeyboard();
        _oskTarget = null;
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
            or nameof(PosSession.ShowOnlineOrders)
            or nameof(PosSession.ShowEditLine))
        {
            if (e.PropertyName == nameof(PosSession.ShowEditLine))
            {
                HideOnScreenKeyboard();
                if (_session?.ShowEditLine == true)
                {
                    EditLineNumpad.SetMode(TouchKeyboardMode.Numeric);
                    EditLineNumpad.SetEmbedded(true);
                }
            }

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
        else if (_session.ShowEditLine)
            ApplyFocus(UiFocusTarget.EditLineQty);
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
                // Quick-cash covers most tills — number pad only after an explicit tap on المبلغ المستلم.
                HideOnScreenKeyboard();
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
            case UiFocusTarget.EditLineQty:
                if (_session?.EditQtyLocked == true)
                {
                    EditUnitPriceBox.Focus();
                    EditUnitPriceBox.SelectAll();
                }
                else
                {
                    EditQtyBox.Focus();
                    EditQtyBox.SelectAll();
                }
                break;
            default:
                FocusBarcode();
                break;
        }
    }

    private void OnCartLineEditTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PosSession session)
            return;

        CartLine? line = null;
        if (sender is Control { Tag: CartLine tagged })
            line = tagged;
        else if (sender is Control { DataContext: CartLine fromContext })
            line = fromContext;

        if (line is null)
            return;

        session.OpenEditLine(line);
        e.Handled = true;
    }

    private bool IsScanLaneBlocked(PosSession session) =>
        session.NeedsLogin
        || session.NeedsOpenShift
        || session.ShowCloseShift
        || session.IsTendering
        || session.ShowSupervisorPin
        || session.ShowOnlineOrders
        || session.ShowMoreActions
        || session.ShowStatusInfo
        || session.BlocksScanning;

    private void ClearWedgeBuffer() => _wedgeBuffer.Clear();

    private bool TryCommitWedgeScan(PosSession session)
    {
        if (_wedgeBuffer.Length == 0)
            return false;

        var code = _wedgeBuffer.ToString().Trim();
        ClearWedgeBuffer();
        if (code.Length == 0 || IsScanLaneBlocked(session))
            return false;

        session.Barcode = code;
        SubmitBarcode();
        return true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PosSession session || e.Handled)
            return;

        if (e.Key is not (Key.Enter or Key.Return))
            return;

        if (TryCommitWedgeScan(session))
        {
            e.Handled = true;
            return;
        }

        // Idle Enter with focus away from the search box: submit whatever is already in Barcode.
        if (!IsScanLaneBlocked(session)
            && !BarcodeBox.IsFocused
            && !string.IsNullOrWhiteSpace(session.Barcode))
        {
            SubmitBarcode();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PosSession session)
            return;

        if (e.Key == Key.Escape)
        {
            ClearWedgeBuffer();
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

            if (session.ShowEditLine)
            {
                session.CloseEditLineCommand.Execute(null);
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

    private bool IsTypingFieldFocused() =>
        EnumerateTouchBoxes().Any(box => box.IsFocused);

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not PosSession session || e.Handled)
            return;

        // Global wedge capture only when no TextBox is focused — otherwise physical keyboard
        // digits/letters must type into search / PIN / tender normally.
        if (!ScannerInput.ShouldCaptureGlobally(
                IsScanLaneBlocked(session),
                IsTypingFieldFocused(),
                e.Text))
            return;

        _wedgeBuffer.Append(e.Text);
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
        if (DataContext is not PosSession session)
            return;

        CatalogProduct? product = null;
        if (sender is Button button)
        {
            product = button.CommandParameter as CatalogProduct
                      ?? button.DataContext as CatalogProduct;
        }

        if (product is null)
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

    private void OnHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PosSession session)
            return;
        session.ToggleCashierKioskCommand.Execute(null);
        e.Handled = true;
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

    private void OnStatusInfoBackdrop(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is not PosSession session)
            return;
        if (e.Source is Border border && ReferenceEquals(border, sender))
        {
            session.CloseStatusInfoCommand.Execute(null);
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

    private void OnQuickCashClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not PosSession session)
            return;
        long amountMinor = 0;
        if (sender is Button { Tag: long longTag })
            amountMinor = longTag;
        else if (sender is Button { Tag: int intTag })
            amountMinor = intTag;
        else if (sender is Button { Tag: string textTag })
            long.TryParse(textTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out amountMinor);
        if (amountMinor <= 0)
            return;
        session.SelectQuickCashCommand.Execute(amountMinor);
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
        HideOnScreenKeyboard();
        BarcodeBox.Focus();
        BarcodeBox.SelectAll();
    }
}

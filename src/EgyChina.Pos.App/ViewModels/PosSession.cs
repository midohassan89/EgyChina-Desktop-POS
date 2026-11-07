using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Printing;

namespace EgyChina.Pos.App.ViewModels;

public partial class PosSession : ObservableObject
{
    private readonly PosDatabase _database;
    private IReadOnlyList<CatalogProduct> _products;
    private readonly SyncStatus _sync;
    private readonly IReceiptPrinter _printer;
    private readonly IPosErpGateway _erp;
    private string? _lastSaleId;
    private DateTimeOffset? _settlementAt;
    private string? _highlightedLineId;
    private SupervisorActionKind? _pendingSupervisorAction;
    private CartLine? _pendingDeleteLine;
    private bool _lastSaleWasReturn;
    private string? _activeQueueTicketCode;
    private LoyaltyCustomer? _loyaltyCustomer;

    public PosSession(
        PosDatabase database,
        IReadOnlyList<CatalogProduct> products,
        SyncStatus sync,
        bool demoCatalog,
        IReceiptPrinter? printer = null,
        IPosErpGateway? erpGateway = null)
    {
        _database = database;
        _products = products;
        _sync = sync;
        _printer = printer ?? new NullReceiptPrinter();
        _erp = erpGateway ?? NullPosErpGateway.Instance;
        DatabasePath = database.DatabasePath;
        CatalogCaption = FormatCatalogCaption(products.Count, demoCatalog);
        ClockText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        NeedsLogin = true;
        SessionCaption = "سجّل الدخول برقم سري من ٦ أرقام";
        PrinterCaption = _printer.IsConfigured
            ? "الطابعة جاهزة"
            : "الطابعة غير مضبوطة — البيع يُحفظ ويمكن إعادة الطباعة لاحقاً";

        Lines.CollectionChanged += (_, _) => RefreshCheckout();
        _sync.Changed += HandleSyncChanged;
        var snap = _sync.Read();
        IsOnline = snap.IsOnline;
        SyncMessage = snap.Message;
        PendingOutbox = snap.PendingOutbox;
        DeadOutbox = snap.DeadOutbox;
        LastSyncError = snap.LastError ?? string.Empty;
        _lastSaleId = _database.GetLastCompletedSaleId();
        RefreshShiftState();
        ReloadHeldCarts();
    }

    public ObservableCollection<CartLine> Lines { get; } = new();

    public string DatabasePath { get; }

    [ObservableProperty]
    private string _catalogCaption = string.Empty;

    public string ShellNotice { get; } = "نقد بالجنيه يُحفظ على هذا الجهاز. رفع البيع إلى المنظومة اختياري حسب إعداد المزامنة.";

    [ObservableProperty]
    private string _printerCaption = string.Empty;

    [ObservableProperty]
    private string _cashierAlert = string.Empty;

    [ObservableProperty]
    private bool _blockNewSales;

    public Action? RequestCustomerDisplay { get; set; }

    public event Action? TenderOpened;

    public event Action<UiFocusTarget>? FocusRequested;

    public string ShortcutHint { get; } =
        "F2 قفل  ·  F3 شاشة العميل  ·  F4 فحص سعر  ·  F5 إعادة طباعة  ·  F6 استرجاع  ·  F7 درج  ·  F8 أونلاين  ·  Esc إلغاء";

    public ObservableCollection<HeldCartSummary> HeldCarts { get; } = new();

    [ObservableProperty]
    private CustomerDisplayMode _customerDisplayMode = CustomerDisplayMode.Idle;

    [ObservableProperty]
    private string _idleGreetingText = CustomerDisplayProjector.IdleGreeting(DateTimeOffset.Now);

    public bool IsCfdIdle => CustomerDisplayMode == CustomerDisplayMode.Idle;

    public bool IsCfdShopping => CustomerDisplayMode == CustomerDisplayMode.Shopping;

    public bool IsCfdPayment => CustomerDisplayMode == CustomerDisplayMode.Payment;

    public bool IsCfdCompleted => CustomerDisplayMode == CustomerDisplayMode.Completed;

    public IReadOnlyList<PosPaymentMethod> PaymentOptions => PosPaymentMethods.CheckoutOptions;

    [ObservableProperty]
    private PosPaymentMethod _selectedPayment = PosPaymentMethod.Cash;

    [ObservableProperty]
    private string _exchangeRateText = WeChatFx.DefaultEgpPerCny.ToString("0.##", CultureInfo.InvariantCulture);

    public string PaymentMethodLabel => PosPaymentMethods.LabelAr(SelectedPayment);

    public string PaymentMethodLabelZh => PosPaymentMethods.LabelArZh(SelectedPayment);

    public bool IsCashPayment => PosPaymentMethods.IsCash(SelectedPayment);

    public bool IsWeChatPayment => PosPaymentMethods.IsWeChat(SelectedPayment);

    public bool IsInstaPayPayment => PosPaymentMethods.IsInstaPay(SelectedPayment);

    public bool ShowsCustomerQr => PosPaymentMethods.ShowsCustomerQr(SelectedPayment);

    public bool ShowWeChatQr => IsTendering && IsWeChatPayment;

    public bool ShowInstaPayQr => IsTendering && IsInstaPayPayment;

    public string WeChatCnyText
    {
        get
        {
            if (!IsWeChatPayment || !WeChatFx.TryParseRate(ExchangeRateText, out var rate))
                return string.Empty;
            return WeChatFx.FormatCny(WeChatFx.EgpMinorToCnyMajor(TotalMinor, rate));
        }
    }

    public string InstaPayPhoneHint => "01028009271";

    [ObservableProperty]
    private bool _needsLogin = true;

    [ObservableProperty]
    private bool _needsOpenShift;

    [ObservableProperty]
    private bool _showCloseShift;

    [ObservableProperty]
    private string _pinText = string.Empty;

    [ObservableProperty]
    private string _authError = string.Empty;

    [ObservableProperty]
    private string _sessionCaption = string.Empty;

    [ObservableProperty]
    private string _cashierName = string.Empty;

    [ObservableProperty]
    private string _openingFloatText = "0";

    [ObservableProperty]
    private string _closingCashText = "0";

    [ObservableProperty]
    private string _shiftError = string.Empty;

    private CashierRecord? _currentCashier;
    private ShiftRecord? _currentShift;

    [ObservableProperty]
    private string _barcode = string.Empty;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private CatalogTab _catalogTab = CatalogTab.All;

    [ObservableProperty]
    private bool _showNotFound;

    [ObservableProperty]
    private string _notFoundBarcode = string.Empty;

    [ObservableProperty]
    private bool _showPriceCheck;

    [ObservableProperty]
    private string _priceCheckQuery = string.Empty;

    [ObservableProperty]
    private PriceCheckResult? _priceCheckResult;

    [ObservableProperty]
    private string _priceCheckError = string.Empty;

    [ObservableProperty]
    private bool _showHeldCarts;

    [ObservableProperty]
    private string _heldCartError = string.Empty;

    [ObservableProperty]
    private string _scanHint = "امسح الباركود أو اختر صنفاً.";

    [ObservableProperty]
    private bool _isReturnMode;

    [ObservableProperty]
    private bool _showSupervisorPin;

    [ObservableProperty]
    private string _supervisorPinText = string.Empty;

    [ObservableProperty]
    private string _supervisorError = string.Empty;

    [ObservableProperty]
    private string _supervisorTitle = string.Empty;

    [ObservableProperty]
    private string _supervisorHint = string.Empty;

    [ObservableProperty]
    private bool _redeemLoyaltyPoints;

    [ObservableProperty]
    private bool _isLookingUpErp;

    [ObservableProperty]
    private ShiftZReport? _currentZReport;

    [ObservableProperty]
    private bool _showOnlineOrders;

    [ObservableProperty]
    private string _onlineOrdersError = string.Empty;

    public ObservableCollection<OnlineOrderSummary> OnlineOrders { get; } = new();

    public bool HasZReport => CurrentZReport is not null;

    public bool HasOnlineOrdersError => !string.IsNullOrWhiteSpace(OnlineOrdersError);

    public bool HasNoOnlineOrders => ShowOnlineOrders && OnlineOrders.Count == 0 && !HasOnlineOrdersError && !IsLookingUpErp;

    public string ZReportVarianceText
    {
        get
        {
            if (CurrentZReport is null || !Money.TryParseEgp(ClosingCashText, out var actual))
                return string.Empty;
            var variance = CurrentZReport.VarianceMinor(actual);
            var label = variance == 0 ? "مطابق" : variance > 0 ? "زيادة" : "عجز";
            return $"{label}: {Money.FormatEgp(Math.Abs(variance))}";
        }
    }

    public bool HasLoyaltyCustomer => _loyaltyCustomer is not null;

    public string LoyaltyCaption
    {
        get
        {
            if (_loyaltyCustomer is null)
                return string.Empty;
            var label = string.IsNullOrWhiteSpace(_loyaltyCustomer.Name)
                ? _loyaltyCustomer.Phone
                : _loyaltyCustomer.Name!;
            return $"{label} · {_loyaltyCustomer.PointsBalance.ToString(CultureInfo.InvariantCulture)} نقطة";
        }
    }

    public bool CanRedeemLoyalty =>
        HasLoyaltyCustomer
        && !IsReturnMode
        && _loyaltyCustomer!.PointsBalance >= 1000
        && SubtotalMinor > 0;

    public bool CanToggleRedeemLoyalty => CanRedeemLoyalty || RedeemLoyaltyPoints;

    public string RedeemLoyaltyButtonText =>
        RedeemLoyaltyPoints ? "إلغاء الاستبدال" : "استبدال نقاط";

    public long LoyaltyDiscountMinor =>
        RedeemLoyaltyPoints && CanRedeemLoyalty
            ? LoyaltyScan.DiscountMinorFromPoints(_loyaltyCustomer!.PointsBalance, SubtotalMinor)
            : 0;

    public string LoyaltyDiscountText =>
        LoyaltyDiscountMinor > 0
            ? "خصم النقاط " + Money.FormatEgp(LoyaltyDiscountMinor)
            : string.Empty;

    public bool ShowLoyaltyDiscount => LoyaltyDiscountMinor > 0;

    public string ActiveQueueCaption =>
        string.IsNullOrWhiteSpace(_activeQueueTicketCode)
            ? string.Empty
            : $"قسيمة {_activeQueueTicketCode}";

    public bool HasActiveQueueTicket => !string.IsNullOrWhiteSpace(_activeQueueTicketCode);

    public bool IsCatalogAll => CatalogTab == CatalogTab.All;

    public bool IsCatalogFavorites => CatalogTab == CatalogTab.Favorites;

    public double CatalogAllEmphasis => IsCatalogAll ? 1.0 : 0.45;

    public double CatalogFavoritesEmphasis => IsCatalogFavorites ? 1.0 : 0.45;

    public bool HasPriceCheckResult => PriceCheckResult is not null;

    public bool HasPriceCheckError => !string.IsNullOrWhiteSpace(PriceCheckError);

    public bool HasSupervisorError => !string.IsNullOrWhiteSpace(SupervisorError);

    public bool BlocksScanning =>
        ShowNotFound || ShowPriceCheck || ShowHeldCarts || ShowSupervisorPin || ShowOnlineOrders;

    public string ReturnModeButtonText => IsReturnMode ? "إنهاء الاسترجاع" : "وضع الاسترجاع";

    public string ReturnModeBanner =>
        IsReturnMode
            ? "وضع الاسترجاع نشط — امسح POS-رقم الإيصال أو أضف الأصناف ثم أكّد الرد"
            : string.Empty;

    public bool ShowReturnModeBanner => IsReturnMode;

    [ObservableProperty]
    private string _syncMessage = "بدء المزامنة…";

    [ObservableProperty]
    private int _pendingOutbox;

    [ObservableProperty]
    private int _deadOutbox;

    [ObservableProperty]
    private string _lastSyncError = string.Empty;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private string _clockText = string.Empty;

    [ObservableProperty]
    private string _screenModeText = "جارٍ اكتشاف الشاشات…";

    [ObservableProperty]
    private string _displayBanner = string.Empty;

    [ObservableProperty]
    private bool _isTendering;

    [ObservableProperty]
    private string _tenderText = string.Empty;

    [ObservableProperty]
    private bool _hasSettlement;

    [ObservableProperty]
    private int _lastReceiptNo;

    [ObservableProperty]
    private long _lastChangeMinor;

    [ObservableProperty]
    private string _saleError = string.Empty;

    public string OnlineText => IsOnline ? "متصل" : "غير متصل";

    public string OutboxCaption
    {
        get
        {
            var pending = PendingOutbox.ToString(CultureInfo.InvariantCulture);
            if (DeadOutbox > 0)
                return $"صندوق الصادر: {pending} · تالف: {DeadOutbox.ToString(CultureInfo.InvariantCulture)}";
            return $"صندوق الصادر: {pending}";
        }
    }

    public bool HasSyncError => !string.IsNullOrWhiteSpace(LastSyncError);

    public string SyncErrorCaption => HasSyncError ? $"آخر خطأ: {LastSyncError}" : string.Empty;

    public bool HasDisplayBanner => !string.IsNullOrWhiteSpace(DisplayBanner);

    public bool IsCartEmpty => Lines.Count == 0;

    public bool HasLines => Lines.Count > 0;

    public bool CanOperate => !NeedsLogin && !NeedsOpenShift && _currentCashier is not null && _currentShift is not null;

    public bool HasCashierAlert => !string.IsNullOrWhiteSpace(CashierAlert);

    public bool CanBeginPayment =>
        CanOperate && HasLines && !IsTendering && !BlockNewSales && !BlocksScanning;

    public bool CanHoldCart =>
        CanOperate && HasLines && !IsTendering && !BlocksScanning && !IsReturnMode;

    public bool CanOpenHeldCarts =>
        CanOperate && !IsTendering && !BlocksScanning && !IsReturnMode;

    public bool CanToggleReturnMode =>
        CanOperate && !IsTendering && !ShowSupervisorPin && !ShowNotFound && !ShowPriceCheck && !ShowHeldCarts && !ShowOnlineOrders;

    public bool CanOpenDrawer =>
        CanOperate && !IsTendering && !ShowSupervisorPin && !ShowNotFound && !ShowPriceCheck && !ShowHeldCarts && !ShowOnlineOrders;

    public bool CanOpenOnlineOrders =>
        CanOperate && !IsTendering && !ShowSupervisorPin && !ShowNotFound && !ShowPriceCheck && !ShowHeldCarts && !ShowOnlineOrders;

    public bool HasHeldCarts => HeldCarts.Count > 0;

    public bool HasHeldCartError => !string.IsNullOrWhiteSpace(HeldCartError);

    public string HeldCartsButtonText =>
        HasHeldCarts
            ? $"السلال المعلّقة ({HeldCarts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : "السلال المعلّقة";

    public bool CanLock => !NeedsLogin && _currentCashier is not null;

    public bool CanOpenCloseShiftPanel => CanOperate && !IsTendering;

    public bool CanReprint => !string.IsNullOrWhiteSpace(_lastSaleId) && !IsTendering && !NeedsLogin;

    public string LineCountText => Lines.Count == 0 ? "السلة فارغة" : $"{Lines.Count.ToString(CultureInfo.InvariantCulture)} سطور";

    public long SubtotalMinor => Lines.Sum(line => checked(line.UnitPriceMinor * line.Qty));

    public long TotalMinor => Math.Max(0, checked(SubtotalMinor - LoyaltyDiscountMinor));

    public string TotalText => Money.FormatEgp(TotalMinor);

    public bool ShowSettlement => HasSettlement && IsCartEmpty;

    public string AmountLabel
    {
        get
        {
            if (IsTendering)
                return IsReturnMode ? "المسترد" : "المستحق";
            if (ShowSettlement)
                return IsReturnMode ? "تم الرد" : "الباقي";
            return IsReturnMode ? "قيمة الاسترجاع" : "الإجمالي";
        }
    }

    public string AmountText => ShowSettlement
        ? Money.FormatEgp(Math.Abs(LastChangeMinor))
        : TotalText;

    public string SettlementText =>
        _lastSaleWasReturn
            ? $"تم الاسترجاع — إيصال {LastReceiptNo.ToString(CultureInfo.InvariantCulture)}"
            : $"تم البيع — إيصال {LastReceiptNo.ToString(CultureInfo.InvariantCulture)}";

    public string IdleCustomerText => IsCfdCompleted ? "شكراً لكم" : IdleGreetingText;

    public bool ShowCustomerChange =>
        IsTendering && IsCashPayment && CanConfirmCash && !IsReturnMode;

    public string CustomerChangeText => ShowCustomerChange
        ? "الباقي " + Money.FormatEgp(ParsedTender - TotalMinor)
        : string.Empty;

    public bool CanConfirmCash
    {
        get
        {
            if (!CanOperate || !IsTendering || !HasLines)
                return false;
            if (!Money.TryParseEgp(TenderText, out var tender))
                return false;
            if (IsWeChatPayment && !WeChatFx.TryParseRate(ExchangeRateText, out _))
                return false;
            if (IsReturnMode || !IsCashPayment)
                return tender == TotalMinor;
            return tender >= TotalMinor;
        }
    }

    public string TenderHint
    {
        get
        {
            if (!string.IsNullOrEmpty(SaleError))
                return SaleError;
            if (!IsTendering)
                return string.Empty;
            if (IsWeChatPayment && !WeChatFx.TryParseRate(ExchangeRateText, out _))
                return "أدخل سعر الصرف (ج.م لكل يوان).";
            if (!Money.TryParseEgp(TenderText, out var tender))
            {
                if (IsReturnMode)
                    return "الاسترجاع يرد المستحق بالكامل بدون باقي.";
                return IsCashPayment
                    ? "اكتب المبلغ المستلم، مثل 200 أو 200.50"
                    : "المبلغ المستحق يُثبَّت تلقائياً لهذه الطريقة.";
            }

            if (IsReturnMode)
            {
                if (tender != TotalMinor)
                    return "الاسترجاع يرد المستحق بالكامل بدون باقي.";
                return "أكد رد المبلغ للعميل.";
            }

            if (IsCashPayment)
            {
                if (tender < TotalMinor)
                    return "المبلغ أقل من المستحق";
                return "الباقي " + Money.FormatEgp(tender - TotalMinor);
            }

            if (tender != TotalMinor)
                return "هذه الطريقة بدون باقي — استخدم المستحق بالكامل.";
            if (IsWeChatPayment)
                return "المبلغ باليوان: " + WeChatCnyText;
            return "أكد الاستلام بنفس المستحق.";
        }
    }

    public IReadOnlyList<CatalogProduct> VisibleProducts
    {
        get
        {
            IEnumerable<CatalogProduct> source = CatalogTab == CatalogTab.Favorites
                ? _products.Where(product => product.IsFavorite)
                : _products;

            var term = Search.Trim();
            if (term.Length == 0)
                return source.ToArray();

            return source.Where(product => Matches(product, term)).ToArray();
        }
    }

    public bool HasNoSearchResults => VisibleProducts.Count == 0;

    public bool HasNoFavorites => CatalogTab == CatalogTab.Favorites && _products.All(p => !p.IsFavorite);

    public void AddProduct(CatalogProduct product)
    {
        if (BlocksScanning)
            return;

        if (BlockNewSales)
        {
            ScanHint = CashierAlert.Length > 0 ? CashierAlert : "القرص ممتلئ. لا يمكن إضافة أصناف الآن.";
            return;
        }

        if (!CanOperate)
        {
            ScanHint = NeedsLogin ? "سجّل الدخول أولاً" : "افتح وردية قبل البيع";
            return;
        }

        HasSettlement = false;
        _settlementAt = null;
        var existing = Lines.FirstOrDefault(line => line.ProductId == product.Id);
        if (existing is null)
        {
            var line = new CartLine(product, RefreshCheckout, RemoveLine, HighlightLine);
            Lines.Add(line);
            HighlightLine(line);
        }
        else
        {
            existing.Qty++;
        }

        ScanHint = product.NameAr;
        RequestFocus(UiFocusTarget.Barcode);
    }

    private void RemoveLine(CartLine line)
    {
        if (!CanOperate || IsTendering || ShowSupervisorPin)
            return;
        BeginSupervisorChallenge(SupervisorActionKind.DeleteItem, line);
    }

    private void RemoveLineNow(CartLine line)
    {
        Lines.Remove(line);
        if (_highlightedLineId == line.LineId)
            ClearHighlight();
        RefreshCheckout();
    }

    private void HighlightLine(CartLine line)
    {
        _highlightedLineId = line.LineId;
        foreach (var item in Lines)
            item.IsHighlighted = item.LineId == line.LineId;
    }

    private void ClearHighlight()
    {
        _highlightedLineId = null;
        foreach (var item in Lines)
            item.IsHighlighted = false;
    }

    public void SubmitBarcode()
    {
        if (BlocksScanning || IsLookingUpErp)
            return;

        if (!CanOperate)
        {
            ScanHint = NeedsLogin ? "سجّل الدخول أولاً" : "افتح وردية قبل البيع";
            return;
        }

        var code = Barcode.Trim();
        if (code.Length == 0)
            return;

        if (QueueTicketCodes.IsQueueCode(code))
        {
            Barcode = string.Empty;
            _ = ClaimQueueTicketAsync(code);
            return;
        }

        if (LoyaltyScan.IsLoyaltyPhone(code))
        {
            Barcode = string.Empty;
            _ = LookupLoyaltyAsync(code);
            return;
        }

        if (IsReturnMode && ReceiptCodes.LooksLikeReceiptCode(code))
        {
            Barcode = string.Empty;
            LoadReturnFromReceipt(code);
            return;
        }

        var product = _database.FindByBarcode(code);
        Barcode = string.Empty;
        if (product is null)
        {
            OpenNotFound(code);
            return;
        }

        AddProduct(product);
    }

    private async Task ClaimQueueTicketAsync(string code)
    {
        if (IsReturnMode)
        {
            IsReturnMode = false;
        }

        IsLookingUpErp = true;
        ScanHint = "جاري سحب القسيمة…";
        try
        {
            var result = await _erp.ClaimQueueTicketAsync(code, CancellationToken.None).ConfigureAwait(true);
            if (!result.Ok || result.Ticket is null)
            {
                ScanHint = result.ErrorMessage ?? "تعذر سحب القسيمة.";
                return;
            }

            var ticket = result.Ticket;
            var added = 0;
            var missing = new List<string>();
            Lines.Clear();
            ClearHighlight();
            HasSettlement = false;
            _settlementAt = null;

            foreach (var item in ticket.Items)
            {
                if (!TryAddQueueItem(item, out var miss))
                {
                    if (!string.IsNullOrWhiteSpace(miss))
                        missing.Add(miss!);
                    continue;
                }

                added++;
            }

            if (added == 0)
            {
                _activeQueueTicketCode = null;
                ScanHint = missing.Count > 0
                    ? $"تعذر تحميل الأصناف: {string.Join("، ", missing.Take(3))}"
                    : "القسيمة فارغة.";
                RefreshCheckout();
                return;
            }

            _activeQueueTicketCode = ticket.Code;
            if (!string.IsNullOrWhiteSpace(ticket.CustomerPhone) || !string.IsNullOrWhiteSpace(ticket.CustomerId))
            {
                if (!string.IsNullOrWhiteSpace(ticket.CustomerPhone) && LoyaltyScan.IsLoyaltyPhone(ticket.CustomerPhone))
                    await LookupLoyaltyAsync(ticket.CustomerPhone).ConfigureAwait(true);
                else if (!string.IsNullOrWhiteSpace(ticket.CustomerId))
                {
                    _loyaltyCustomer = new LoyaltyCustomer(
                        ticket.CustomerId!,
                        ticket.CustomerName,
                        ticket.CustomerPhone ?? "",
                        0);
                    RedeemLoyaltyPoints = false;
                    OnPropertyChanged(nameof(HasLoyaltyCustomer));
                    OnPropertyChanged(nameof(LoyaltyCaption));
                    OnPropertyChanged(nameof(CanRedeemLoyalty));
                }
            }

            ScanHint = missing.Count > 0
                ? $"تم تحميل {ticket.Code} · ناقص {missing.Count.ToString(CultureInfo.InvariantCulture)} صنف"
                : $"تم تحميل قسيمة {ticket.Code}";
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("claim queue ticket failed", ex);
            ScanHint = ArabicUi.FromException(ex, "تعذر سحب القسيمة.");
        }
        finally
        {
            IsLookingUpErp = false;
        }
    }

    private bool TryAddQueueItem(QueueTicketItem item, out string? missingLabel)
    {
        missingLabel = null;
        var product = _database.FindProductForQueueItem(item.ProductId, item.Barcode);
        if (product is null)
        {
            missingLabel = string.IsNullOrWhiteSpace(item.Name) ? item.ProductId : item.Name;
            return false;
        }

        var qtyAbs = Math.Abs(item.Quantity);
        var unitMajor = Math.Abs(item.UnitPriceMajor);
        int qty;
        long unitMinor;
        if (item.IsWeighted || qtyAbs != Math.Floor(qtyAbs))
        {
            qty = 1;
            unitMinor = ToMinor(qtyAbs * unitMajor);
        }
        else
        {
            qty = Math.Max(1, (int)Math.Round(qtyAbs, MidpointRounding.AwayFromZero));
            unitMinor = ToMinor(unitMajor);
        }

        if (unitMinor < 0)
            unitMinor = 0;

        var payload = new HeldCartLinePayload(
            product.Id,
            item.Barcode ?? product.Barcode,
            string.IsNullOrWhiteSpace(item.Name) ? product.NameAr : item.Name,
            item.NameZh ?? product.NameZh,
            qty,
            unitMinor);
        var line = new CartLine(payload, RefreshCheckout, RemoveLine, HighlightLine);
        Lines.Add(line);
        HighlightLine(line);
        return true;
    }

    private static long ToMinor(decimal majorPounds)
    {
        var scaled = Math.Round(majorPounds * 100m, MidpointRounding.AwayFromZero);
        if (scaled > long.MaxValue)
            return long.MaxValue;
        if (scaled < long.MinValue)
            return long.MinValue;
        return (long)scaled;
    }

    private async Task LookupLoyaltyAsync(string phone)
    {
        IsLookingUpErp = true;
        ScanHint = "جاري البحث عن الولاء…";
        try
        {
            var (customer, error) = await _erp.LookupLoyaltyPhoneAsync(phone, CancellationToken.None)
                .ConfigureAwait(true);
            if (customer is null)
            {
                ClearLoyalty();
                ScanHint = error ?? "عميل غير موجود";
                return;
            }

            _loyaltyCustomer = customer;
            RedeemLoyaltyPoints = false;
            OnPropertyChanged(nameof(HasLoyaltyCustomer));
            OnPropertyChanged(nameof(LoyaltyCaption));
            OnPropertyChanged(nameof(CanRedeemLoyalty));
            OnPropertyChanged(nameof(CanToggleRedeemLoyalty));
            OnPropertyChanged(nameof(RedeemLoyaltyButtonText));
            ScanHint = LoyaltyCaption;
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("loyalty lookup failed", ex);
            ScanHint = ArabicUi.FromException(ex, "تعذر البحث عن العميل.");
        }
        finally
        {
            IsLookingUpErp = false;
        }
    }

    [RelayCommand]
    private void ClearLoyalty()
    {
        _loyaltyCustomer = null;
        RedeemLoyaltyPoints = false;
        OnPropertyChanged(nameof(HasLoyaltyCustomer));
        OnPropertyChanged(nameof(LoyaltyCaption));
        OnPropertyChanged(nameof(CanRedeemLoyalty));
        OnPropertyChanged(nameof(CanToggleRedeemLoyalty));
        OnPropertyChanged(nameof(RedeemLoyaltyButtonText));
        OnPropertyChanged(nameof(LoyaltyDiscountMinor));
        OnPropertyChanged(nameof(LoyaltyDiscountText));
        OnPropertyChanged(nameof(ShowLoyaltyDiscount));
        RefreshCheckout();
    }

    [RelayCommand]
    private void ToggleRedeemLoyalty()
    {
        if (!CanRedeemLoyalty && !RedeemLoyaltyPoints)
            return;
        RedeemLoyaltyPoints = !RedeemLoyaltyPoints;
        if (IsTendering)
            TenderText = Money.FormatAmount(TotalMinor);
        RefreshCheckout();
    }

    partial void OnRedeemLoyaltyPointsChanged(bool value)
    {
        OnPropertyChanged(nameof(RedeemLoyaltyButtonText));
        OnPropertyChanged(nameof(CanToggleRedeemLoyalty));
        RefreshCheckout();
    }

    private void LoadReturnFromReceipt(string code)
    {
        try
        {
            var receiptLines = _database.FindSaleLinesByCode(code);
            if (receiptLines is null || receiptLines.Count == 0)
            {
                ScanHint = "لم يُعثر على إيصال محلي بهذا الرمز (أو أنه استرجاع سابق).";
                return;
            }

            Lines.Clear();
            ClearHighlight();
            HasSettlement = false;
            _settlementAt = null;
            foreach (var item in receiptLines)
            {
                var payload = new HeldCartLinePayload(
                    item.ProductId,
                    item.Barcode,
                    item.NameAr,
                    item.NameZh,
                    item.Qty,
                    item.UnitPriceMinor);
                var line = new CartLine(payload, RefreshCheckout, RemoveLine, HighlightLine);
                Lines.Add(line);
            }

            if (Lines.Count > 0)
                HighlightLine(Lines[^1]);
            ScanHint = $"تم تحميل {Lines.Count.ToString(CultureInfo.InvariantCulture)} أصناف من الإيصال للاسترجاع.";
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("load return receipt failed", ex);
            ScanHint = ArabicUi.FromException(ex, "تعذر تحميل الإيصال.");
        }
    }

    [RelayCommand]
    private void ShowAllCatalog()
    {
        CatalogTab = CatalogTab.All;
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
        OnPropertyChanged(nameof(IsCatalogAll));
        OnPropertyChanged(nameof(IsCatalogFavorites));
    }

    [RelayCommand]
    private void ShowFavoritesCatalog()
    {
        CatalogTab = CatalogTab.Favorites;
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
        OnPropertyChanged(nameof(IsCatalogAll));
        OnPropertyChanged(nameof(IsCatalogFavorites));
    }

    [RelayCommand]
    private void ToggleFavorite(CatalogProduct? product)
    {
        if (product is null)
            return;

        var nowFavorite = _database.ToggleFavorite(product.Id);
        _products = _database.ListActiveProducts();
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
        ScanHint = nowFavorite
            ? $"أُضيف للمفضلة: {product.NameAr}"
            : $"أُزيل من المفضلة: {product.NameAr}";
    }

    [RelayCommand]
    private void OpenPriceCheck()
    {
        if (NeedsLogin || ShowNotFound || IsTendering || ShowCloseShift)
            return;

        ShowPriceCheck = true;
        PriceCheckQuery = string.Empty;
        PriceCheckResult = null;
        PriceCheckError = string.Empty;
        RequestFocus(UiFocusTarget.PriceCheck);
    }

    [RelayCommand]
    private void ClosePriceCheck()
    {
        ShowPriceCheck = false;
        PriceCheckQuery = string.Empty;
        PriceCheckResult = null;
        PriceCheckError = string.Empty;
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void SubmitPriceCheck()
    {
        var code = PriceCheckQuery.Trim();
        if (code.Length == 0)
            return;

        var product = _database.FindByBarcode(code);
        PriceCheckQuery = string.Empty;
        if (product is null)
        {
            PriceCheckResult = null;
            PriceCheckError = $"غير موجود: {code}";
            RequestFocus(UiFocusTarget.PriceCheck);
            return;
        }

        PriceCheckError = string.Empty;
        PriceCheckResult = new PriceCheckResult(
            product.Barcode ?? code,
            product.NameAr,
            product.NameZh,
            product.Sku,
            product.PriceEgpMinor);
        RequestFocus(UiFocusTarget.PriceCheck);
    }

    [RelayCommand]
    private void DismissNotFound()
    {
        ShowNotFound = false;
        NotFoundBarcode = string.Empty;
        RequestFocus(UiFocusTarget.Barcode);
    }

    private void OpenNotFound(string barcode)
    {
        NotFoundBarcode = barcode;
        ShowNotFound = true;
        ScanHint = "صنف غير موجود — أكّد قبل المتابعة";
        RequestFocus(UiFocusTarget.NotFoundAck);
    }

    [RelayCommand]
    private void HoldCurrentCart()
    {
        HeldCartError = string.Empty;
        if (!CanHoldCart)
            return;

        try
        {
            var payload = Lines.Select(line => line.ToHeldPayload()).ToArray();
            _database.HoldCart(payload, _currentCashier?.Id);
            Lines.Clear();
            ClearHighlight();
            IsTendering = false;
            TenderText = string.Empty;
            SaleError = string.Empty;
            ReloadHeldCarts();
            ScanHint = "تم تعليق السلة";
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("hold cart failed", ex);
            HeldCartError = ArabicUi.FromException(ex, "تعذر تعليق السلة.");
            ScanHint = HeldCartError;
        }
    }

    [RelayCommand]
    private void OpenHeldCarts()
    {
        if (!CanOpenHeldCarts)
            return;
        HeldCartError = string.Empty;
        ReloadHeldCarts();
        ShowHeldCarts = true;
    }

    [RelayCommand]
    private void CloseHeldCarts()
    {
        ShowHeldCarts = false;
        HeldCartError = string.Empty;
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void ResumeHeldCart(HeldCartSummary? held)
    {
        HeldCartError = string.Empty;
        if (held is null || !CanOperate || IsTendering)
            return;

        try
        {
            if (HasLines)
            {
                var current = Lines.Select(line => line.ToHeldPayload()).ToArray();
                _database.HoldCart(current, _currentCashier?.Id);
            }

            var lines = _database.TakeHeldCart(held.Id);
            if (lines is null || lines.Count == 0)
            {
                HeldCartError = "السلة المعلّقة غير موجودة.";
                ReloadHeldCarts();
                return;
            }

            Lines.Clear();
            ClearHighlight();
            HasSettlement = false;
            _settlementAt = null;
            foreach (var payload in lines)
            {
                var line = new CartLine(payload, RefreshCheckout, RemoveLine, HighlightLine);
                Lines.Add(line);
            }

            if (Lines.Count > 0)
                HighlightLine(Lines[^1]);

            ShowHeldCarts = false;
            ReloadHeldCarts();
            ScanHint = $"استُرجعت سلة {held.Label}";
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("resume held cart failed", ex);
            HeldCartError = ArabicUi.FromException(ex, "تعذر استرجاع السلة.");
            ReloadHeldCarts();
        }
    }

    [RelayCommand]
    private void DiscardHeldCart(HeldCartSummary? held)
    {
        HeldCartError = string.Empty;
        if (held is null)
            return;

        try
        {
            _database.DiscardHeldCart(held.Id);
            ReloadHeldCarts();
            if (HeldCarts.Count == 0)
                ShowHeldCarts = false;
        }
        catch (Exception ex)
        {
            PosLog.Error("discard held cart failed", ex);
            HeldCartError = ArabicUi.FromException(ex, "تعذر حذف السلة المعلّقة.");
        }
    }

    private void ReloadHeldCarts()
    {
        HeldCarts.Clear();
        foreach (var cart in _database.ListHeldCarts())
            HeldCarts.Add(cart);
        OnPropertyChanged(nameof(HasHeldCarts));
        OnPropertyChanged(nameof(HeldCartsButtonText));
    }

    partial void OnHeldCartErrorChanged(string value) => OnPropertyChanged(nameof(HasHeldCartError));

    public void SetClock(DateTime now)
    {
        ClockText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var stamp = new DateTimeOffset(now);
        IdleGreetingText = CustomerDisplayProjector.IdleGreeting(stamp);
        if (HasSettlement && CustomerDisplayProjector.IsCompletedExpired(_settlementAt, stamp))
        {
            HasSettlement = false;
            _settlementAt = null;
        }

        RefreshCustomerDisplayMode(stamp);
    }

    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
    }

    partial void OnCatalogTabChanged(CatalogTab value)
    {
        OnPropertyChanged(nameof(IsCatalogAll));
        OnPropertyChanged(nameof(IsCatalogFavorites));
        OnPropertyChanged(nameof(CatalogAllEmphasis));
        OnPropertyChanged(nameof(CatalogFavoritesEmphasis));
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
    }

    partial void OnShowNotFoundChanged(bool value) => OnPropertyChanged(nameof(BlocksScanning));

    partial void OnShowPriceCheckChanged(bool value) => OnPropertyChanged(nameof(BlocksScanning));

    partial void OnShowSupervisorPinChanged(bool value)
    {
        OnPropertyChanged(nameof(BlocksScanning));
        OnPropertyChanged(nameof(CanToggleReturnMode));
        OnPropertyChanged(nameof(CanOpenDrawer));
    }

    partial void OnIsReturnModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ReturnModeButtonText));
        OnPropertyChanged(nameof(ReturnModeBanner));
        OnPropertyChanged(nameof(ShowReturnModeBanner));
        OnPropertyChanged(nameof(CanHoldCart));
        OnPropertyChanged(nameof(CanOpenHeldCarts));
        RefreshCheckout();
    }

    partial void OnClosingCashTextChanged(string value) => OnPropertyChanged(nameof(ZReportVarianceText));

    partial void OnShowOnlineOrdersChanged(bool value)
    {
        OnPropertyChanged(nameof(BlocksScanning));
        OnPropertyChanged(nameof(CanToggleReturnMode));
        OnPropertyChanged(nameof(CanOpenDrawer));
        OnPropertyChanged(nameof(CanOpenOnlineOrders));
        OnPropertyChanged(nameof(HasNoOnlineOrders));
    }

    partial void OnShowHeldCartsChanged(bool value)
    {
        OnPropertyChanged(nameof(BlocksScanning));
        OnPropertyChanged(nameof(CanHoldCart));
        OnPropertyChanged(nameof(CanOpenHeldCarts));
    }

    partial void OnPriceCheckResultChanged(PriceCheckResult? value) => OnPropertyChanged(nameof(HasPriceCheckResult));

    partial void OnPriceCheckErrorChanged(string value) => OnPropertyChanged(nameof(HasPriceCheckError));

    partial void OnIsOnlineChanged(bool value) => OnPropertyChanged(nameof(OnlineText));

    partial void OnPendingOutboxChanged(int value) => OnPropertyChanged(nameof(OutboxCaption));

    partial void OnDeadOutboxChanged(int value) => OnPropertyChanged(nameof(OutboxCaption));

    partial void OnLastSyncErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasSyncError));
        OnPropertyChanged(nameof(SyncErrorCaption));
    }

    partial void OnDisplayBannerChanged(string value) => OnPropertyChanged(nameof(HasDisplayBanner));

    partial void OnCashierAlertChanged(string value) => OnPropertyChanged(nameof(HasCashierAlert));

    partial void OnIsTenderingChanged(bool value) => RefreshCheckout();

    partial void OnTenderTextChanged(string value)
    {
        SaleError = string.Empty;
        RefreshCheckout();
    }

    partial void OnHasSettlementChanged(bool value) => RefreshCheckout();

    partial void OnSaleErrorChanged(string value) => OnPropertyChanged(nameof(TenderHint));

    [RelayCommand]
    private void ClearCart()
    {
        if (ShowSupervisorPin)
            return;
        Lines.Clear();
        ClearHighlight();
        IsTendering = false;
        HasSettlement = false;
        _settlementAt = null;
        TenderText = string.Empty;
        SaleError = string.Empty;
        _activeQueueTicketCode = null;
        OnPropertyChanged(nameof(HasActiveQueueTicket));
        OnPropertyChanged(nameof(ActiveQueueCaption));
        RefreshCheckout();
    }

    private async Task CompleteQueueTicketAsync(string code, string saleId)
    {
        try
        {
            var ok = await _erp.CompleteQueueTicketAsync(code, saleId, CancellationToken.None).ConfigureAwait(true);
            if (!ok)
                PosLog.Error($"queue ticket complete failed code={code}");
        }
        catch (Exception ex)
        {
            PosLog.Error($"queue ticket complete failed code={code}", ex);
        }
    }

    [RelayCommand]
    private void ToggleReturnMode()
    {
        if (!CanToggleReturnMode && !IsReturnMode)
            return;

        if (IsReturnMode)
        {
            IsReturnMode = false;
            ScanHint = "انتهى وضع الاسترجاع.";
            RequestFocus(UiFocusTarget.Barcode);
            return;
        }

        BeginSupervisorChallenge(SupervisorActionKind.ReturnMode);
    }

    [RelayCommand]
    private void OpenCashDrawer()
    {
        if (!CanOpenDrawer)
            return;
        BeginSupervisorChallenge(SupervisorActionKind.OpenDrawer);
    }

    [RelayCommand]
    private void CancelSupervisorPin()
    {
        ClearSupervisorChallenge();
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void SubmitSupervisorPin()
    {
        SupervisorError = string.Empty;
        if (_pendingSupervisorAction is null)
        {
            ClearSupervisorChallenge();
            return;
        }

        var result = _database.TryAuthenticatePin(SupervisorPinText);
        SupervisorPinText = string.Empty;
        if (result.Status != PinAuthStatus.Ok || result.Cashier is null)
        {
            SupervisorError = result.Message ?? "تعذر التحقق من الرقم السري.";
            return;
        }

        if (!SupervisorAuth.IsPrivileged(result.Cashier))
        {
            SupervisorError = "يلزم رقم سري لمشرف أو مدير.";
            return;
        }

        var action = _pendingSupervisorAction.Value;
        var deleteLine = _pendingDeleteLine;
        ClearSupervisorChallenge();

        switch (action)
        {
            case SupervisorActionKind.DeleteItem:
                if (deleteLine is not null && Lines.Contains(deleteLine))
                    RemoveLineNow(deleteLine);
                RequestFocus(UiFocusTarget.Barcode);
                break;
            case SupervisorActionKind.ReturnMode:
                IsReturnMode = true;
                ScanHint = "وضع الاسترجاع — امسح POS-رقم الإيصال أو أضف الأصناف.";
                RequestFocus(UiFocusTarget.Barcode);
                break;
            case SupervisorActionKind.OpenDrawer:
                _ = KickDrawerAsync();
                RequestFocus(UiFocusTarget.Barcode);
                break;
        }
    }

    private void BeginSupervisorChallenge(SupervisorActionKind action, CartLine? deleteLine = null)
    {
        _pendingSupervisorAction = action;
        _pendingDeleteLine = deleteLine;
        SupervisorTitle = SupervisorAuth.TitleAr(action);
        SupervisorHint = SupervisorAuth.HintAr(action);
        SupervisorPinText = string.Empty;
        SupervisorError = string.Empty;
        ShowSupervisorPin = true;
        RequestFocus(UiFocusTarget.SupervisorPin);
    }

    private void ClearSupervisorChallenge()
    {
        _pendingSupervisorAction = null;
        _pendingDeleteLine = null;
        ShowSupervisorPin = false;
        SupervisorPinText = string.Empty;
        SupervisorError = string.Empty;
        SupervisorTitle = string.Empty;
        SupervisorHint = string.Empty;
    }

    private async Task KickDrawerAsync()
    {
        try
        {
            if (!_printer.IsConfigured)
            {
                await SetPrintHintAsync("الطابعة غير مضبوطة — لا يمكن فتح الدرج.").ConfigureAwait(false);
                return;
            }

            await _printer.KickDrawerAsync(CancellationToken.None).ConfigureAwait(false);
            await SetPrintHintAsync("تم إرسال أمر فتح الدرج.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLog.Error("drawer kick failed", ex);
            await SetPrintHintAsync("تعذر فتح الدرج.").ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void ShowCustomerDisplay() => RequestCustomerDisplay?.Invoke();

    [RelayCommand]
    private void SubmitPin()
    {
        AuthError = string.Empty;
        var result = _database.TryAuthenticatePin(PinText);
        if (result.Status != PinAuthStatus.Ok || result.Cashier is null)
        {
            AuthError = result.Message ?? "تعذر الدخول.";
            PinText = string.Empty;
            return;
        }

        _currentCashier = result.Cashier;
        CashierName = result.Cashier.DisplayName;
        NeedsLogin = false;
        PinText = string.Empty;
        AuthError = string.Empty;
        RefreshShiftState();
        RefreshCheckout();
        RequestFocus(_currentShift is null ? UiFocusTarget.OpeningFloat : UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void LockSession()
    {
        if (_currentCashier is null)
            return;

        Lines.Clear();
        ClearHighlight();
        IsTendering = false;
        HasSettlement = false;
        _settlementAt = null;
        TenderText = string.Empty;
        SaleError = string.Empty;
        ShowCloseShift = false;
        IsReturnMode = false;
        _activeQueueTicketCode = null;
        ClearLoyalty();
        ClearSupervisorChallenge();
        _currentCashier = null;
        CashierName = string.Empty;
        NeedsLogin = true;
        SessionCaption = _currentShift is null
            ? "الجهاز مقفل. أدخل الرقم السري."
            : "الجهاز مقفل. الوردية ما زالت مفتوحة — أدخل الرقم السري للمتابعة.";
        RefreshCheckout();
        RequestFocus(UiFocusTarget.Pin);
    }

    [RelayCommand]
    private void OpenShift()
    {
        ShiftError = string.Empty;
        if (_currentCashier is null)
        {
            ShiftError = "سجّل الدخول أولاً.";
            return;
        }

        if (!Money.TryParseEgp(OpeningFloatText, out var opening) || opening < 0)
        {
            ShiftError = "أدخل نقدية الافتتاح، مثل 500 أو 500.00";
            return;
        }

        try
        {
            _currentShift = _database.OpenShift(_currentCashier.Id, opening);
            NeedsOpenShift = false;
            SessionCaption = $"{_currentCashier.DisplayName} · وردية مفتوحة";
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("open shift failed", ex);
            ShiftError = ArabicUi.FromException(ex, "تعذر فتح الوردية.");
        }
    }

    [RelayCommand]
    private void BeginCloseShift()
    {
        if (!CanOpenCloseShiftPanel || _currentShift is null)
            return;
        if (HasLines || IsTendering)
        {
            ShiftError = "أفرغ السلة قبل إغلاق الوردية.";
            return;
        }

        CurrentZReport = _database.BuildShiftZReport(_currentShift.Id);
        ClosingCashText = Money.FormatAmount(CurrentZReport?.ExpectedCashMinor ?? _currentShift.OpeningFloatMinor);
        ShiftError = string.Empty;
        ShowCloseShift = true;
        OnPropertyChanged(nameof(HasZReport));
        OnPropertyChanged(nameof(ZReportVarianceText));
        RequestFocus(UiFocusTarget.ClosingCash);
    }

    [RelayCommand]
    private void CancelCloseShift()
    {
        ShowCloseShift = false;
        ShiftError = string.Empty;
        CurrentZReport = null;
        OnPropertyChanged(nameof(HasZReport));
        OnPropertyChanged(nameof(ZReportVarianceText));
    }

    [RelayCommand]
    private void OpenOnlineOrders()
    {
        if (!CanOpenOnlineOrders)
            return;
        ShowOnlineOrders = true;
        _ = RefreshOnlineOrdersAsync();
    }

    [RelayCommand]
    private void CloseOnlineOrders()
    {
        ShowOnlineOrders = false;
        OnlineOrdersError = string.Empty;
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void RefreshOnlineOrders() => _ = RefreshOnlineOrdersAsync();

    private async Task RefreshOnlineOrdersAsync()
    {
        OnlineOrdersError = string.Empty;
        IsLookingUpErp = true;
        try
        {
            var (orders, error) = await _erp.ListOnlineOrdersAsync(CancellationToken.None).ConfigureAwait(true);
            OnlineOrders.Clear();
            foreach (var order in orders)
                OnlineOrders.Add(order);
            OnlineOrdersError = error ?? string.Empty;
            OnPropertyChanged(nameof(HasNoOnlineOrders));
            OnPropertyChanged(nameof(HasOnlineOrdersError));
        }
        catch (Exception ex)
        {
            PosLog.Error("online orders load failed", ex);
            OnlineOrdersError = ArabicUi.FromException(ex, "تعذر تحميل طلبات الأونلاين.");
            OnPropertyChanged(nameof(HasOnlineOrdersError));
            OnPropertyChanged(nameof(HasNoOnlineOrders));
        }
        finally
        {
            IsLookingUpErp = false;
            OnPropertyChanged(nameof(HasNoOnlineOrders));
        }
    }

    [RelayCommand]
    private void ConfirmCloseShift()
    {
        ShiftError = string.Empty;
        if (_currentCashier is null || _currentShift is null)
            return;
        if (!Money.TryParseEgp(ClosingCashText, out var closing) || closing < 0)
        {
            ShiftError = "أدخل نقدية الإغلاق بالجنيه.";
            return;
        }

        try
        {
            _database.CloseShift(_currentShift.Id, _currentCashier.Id, closing);
            _currentShift = null;
            ShowCloseShift = false;
            CurrentZReport = null;
            NeedsOpenShift = true;
            SessionCaption = $"{_currentCashier.DisplayName} · لا توجد وردية مفتوحة";
            OnPropertyChanged(nameof(HasZReport));
            RefreshCheckout();
            RequestFocus(UiFocusTarget.OpeningFloat);
            DatabaseBackup.Create(_database);
        }
        catch (Exception ex)
        {
            PosLog.Error("close shift failed", ex);
            ShiftError = ArabicUi.FromException(ex, "تعذر إغلاق الوردية.");
        }
    }

    [RelayCommand]
    private void BeginCashPayment()
    {
        if (!CanBeginPayment)
            return;

        HasSettlement = false;
        SaleError = string.Empty;
        SelectedPayment = PosPaymentMethod.Cash;
        ExchangeRateText = WeChatFx.DefaultEgpPerCny.ToString("0.##", CultureInfo.InvariantCulture);
        TenderText = Money.FormatAmount(TotalMinor);
        IsTendering = true;
        TenderOpened?.Invoke();
        RequestFocus(UiFocusTarget.Tender);
    }

    [RelayCommand]
    private void SelectPayment(PosPaymentMethod method)
    {
        if (!IsTendering)
            return;
        SelectedPayment = method;
        SaleError = string.Empty;
        if (!IsCashPayment)
            TenderText = Money.FormatAmount(TotalMinor);
        RefreshCheckout();
        if (IsCashPayment)
            RequestFocus(UiFocusTarget.Tender);
    }

    [RelayCommand]
    private void SetExactTender()
    {
        if (!HasLines)
            return;
        TenderText = Money.FormatAmount(TotalMinor);
    }

    [RelayCommand]
    private void CancelTender()
    {
        IsTendering = false;
        TenderText = string.Empty;
        SaleError = string.Empty;
        SelectedPayment = PosPaymentMethod.Cash;
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void ConfirmCash()
    {
        if (!CanConfirmCash || !Money.TryParseEgp(TenderText, out var tender))
            return;

        var lines = Lines
            .Select(line => new CashSaleLine(line.ProductId, line.Barcode, line.NameAr, line.Qty, line.UnitPriceMinor))
            .ToArray();

        if (BlockNewSales)
        {
            SaleError = CashierAlert.Length > 0 ? CashierAlert : "القرص ممتلئ. لا يمكن إتمام بيع جديد.";
            return;
        }

        if (_currentCashier is null || _currentShift is null)
        {
            SaleError = "سجّل الدخول وافتح وردية قبل البيع.";
            return;
        }

        decimal? wechatRate = null;
        if (IsWeChatPayment)
        {
            if (!WeChatFx.TryParseRate(ExchangeRateText, out var rate))
            {
                SaleError = "سعر الصرف غير صالح.";
                return;
            }

            wechatRate = rate;
        }

        try
        {
            var wasReturn = IsReturnMode;
            var paymentLabel = PosPaymentMethods.LabelAr(SelectedPayment);
            var paymentErp = PosPaymentMethods.ToErpId(SelectedPayment);
            var kickDrawer = IsCashPayment || wasReturn;
            var applyLoyalty = !wasReturn && _loyaltyCustomer is not null;
            var discount = applyLoyalty ? LoyaltyDiscountMinor : 0;
            var pointsRedeemed = applyLoyalty
                ? LoyaltyScan.PointsRedeemedForDiscount(discount)
                : 0;
            var pointsEarned = applyLoyalty
                ? LoyaltyScan.PointsEarnedForPayable(TotalMinor)
                : 0;
            var queueCode = _activeQueueTicketCode;
            var sale = _database.CompleteSale(
                lines,
                tender,
                _currentCashier.Id,
                _currentShift.Id,
                SelectedPayment,
                wechatRate,
                isReturn: wasReturn,
                discountMinor: discount,
                loyaltyCustomerId: applyLoyalty ? _loyaltyCustomer!.Id : null,
                pointsEarned: pointsEarned,
                pointsRedeemed: pointsRedeemed);
            LastReceiptNo = sale.ReceiptNo;
            LastChangeMinor = sale.ChangeMinor;
            _lastSaleWasReturn = wasReturn;
            TenderText = string.Empty;
            IsTendering = false;
            Lines.Clear();
            ClearHighlight();
            HasSettlement = true;
            _settlementAt = DateTimeOffset.Now;
            PendingOutbox = _database.CountPendingOutbox();
            _lastSaleId = sale.SaleId;
            IsReturnMode = false;
            _activeQueueTicketCode = null;
            ClearLoyalty();
            ScanHint = wasReturn
                ? $"تم الاسترجاع — إيصال {sale.ReceiptNo.ToString(CultureInfo.InvariantCulture)} · {paymentLabel}"
                : $"تم حفظ الإيصال {sale.ReceiptNo.ToString(CultureInfo.InvariantCulture)} · {paymentLabel}";
            PosLog.Info(
                $"cashier confirmed {(wasReturn ? "return" : "sale")} receipt {sale.ReceiptNo} method={paymentErp}");
            SelectedPayment = PosPaymentMethod.Cash;
            OnPropertyChanged(nameof(CanReprint));
            OnPropertyChanged(nameof(HasActiveQueueTicket));
            OnPropertyChanged(nameof(ActiveQueueCaption));
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
            _ = PrintSaleAsync(sale.SaleId, isReprint: false, kickDrawer: kickDrawer);
            if (!string.IsNullOrWhiteSpace(queueCode) && !wasReturn)
                _ = CompleteQueueTicketAsync(queueCode!, sale.SaleId);
        }
        catch (Exception ex)
        {
            PosLog.Error("sale confirm failed", ex);
            SaleError = ArabicUi.FromException(ex, "تعذر حفظ البيع. السلة ما زالت هنا.");
        }
    }

    partial void OnSelectedPaymentChanged(PosPaymentMethod value)
    {
        OnPropertyChanged(nameof(PaymentMethodLabel));
        OnPropertyChanged(nameof(PaymentMethodLabelZh));
        OnPropertyChanged(nameof(IsCashPayment));
        OnPropertyChanged(nameof(IsWeChatPayment));
        OnPropertyChanged(nameof(IsInstaPayPayment));
        OnPropertyChanged(nameof(ShowsCustomerQr));
        OnPropertyChanged(nameof(ShowWeChatQr));
        OnPropertyChanged(nameof(ShowInstaPayQr));
        OnPropertyChanged(nameof(WeChatCnyText));
        OnPropertyChanged(nameof(CanConfirmCash));
        OnPropertyChanged(nameof(TenderHint));
        OnPropertyChanged(nameof(ShowCustomerChange));
        OnPropertyChanged(nameof(CustomerChangeText));
    }

    partial void OnExchangeRateTextChanged(string value)
    {
        OnPropertyChanged(nameof(WeChatCnyText));
        OnPropertyChanged(nameof(CanConfirmCash));
        OnPropertyChanged(nameof(TenderHint));
    }

    public void ApplyDiskStatus(bool blockSales, string? message)
    {
        BlockNewSales = blockSales;
        CashierAlert = message ?? string.Empty;
        RefreshCheckout();
    }

    public void NotifyLoginSurfaceReady() => RequestFocus(UiFocusTarget.Pin);

    private void RequestFocus(UiFocusTarget target) => FocusRequested?.Invoke(target);

    [RelayCommand]
    private void ReprintLast()
    {
        if (!CanReprint || string.IsNullOrWhiteSpace(_lastSaleId))
            return;
        _ = PrintSaleAsync(_lastSaleId, isReprint: true, kickDrawer: false);
    }

    private async Task PrintSaleAsync(string saleId, bool isReprint, bool kickDrawer)
    {
        try
        {
            var document = _database.TryBuildReceiptDocument(saleId, isReprint, kickDrawer);
            if (document is null)
            {
                await SetPrintHintAsync("تعذر تجهيز الإيصال للطباعة.").ConfigureAwait(false);
                return;
            }

            if (!_printer.IsConfigured)
            {
                await SetPrintHintAsync(
                    isReprint
                        ? "لا توجد طابعة مضبوطة. الإيصال محفوظ في القاعدة المحلية."
                        : $"تم الحفظ — إيصال {document.ReceiptNo.ToString(CultureInfo.InvariantCulture)}. الطابعة غير مضبوطة.").ConfigureAwait(false);
                return;
            }

            await _printer.PrintAsync(document, CancellationToken.None).ConfigureAwait(false);
            await SetPrintHintAsync(
                isReprint
                    ? $"أُعيدت طباعة الإيصال {document.ReceiptNo.ToString(CultureInfo.InvariantCulture)}"
                    : $"طُبع الإيصال {document.ReceiptNo.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLog.Error(isReprint ? "reprint failed" : "print failed", ex);
            await SetPrintHintAsync("تعذر الطباعة. البيع محفوظ — جرّب إعادة الطباعة.").ConfigureAwait(false);
        }
    }

    private Task SetPrintHintAsync(string message)
    {
        void Apply() => ScanHint = message;
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(Apply).GetTask();
    }

    private void RefreshShiftState()
    {
        _currentShift = _database.GetOpenShift();
        if (_currentCashier is null)
        {
            NeedsOpenShift = false;
            return;
        }

        NeedsOpenShift = _currentShift is null;
        SessionCaption = _currentShift is null
            ? $"{_currentCashier.DisplayName} · افتح وردية للمتابعة"
            : $"{_currentCashier.DisplayName} · وردية مفتوحة";
    }

    private long ParsedTender =>
        Money.TryParseEgp(TenderText, out var tender) ? tender : 0;

    private void RefreshCheckout()
    {
        OnPropertyChanged(nameof(SubtotalMinor));
        OnPropertyChanged(nameof(TotalMinor));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(CanBeginPayment));
        OnPropertyChanged(nameof(CanHoldCart));
        OnPropertyChanged(nameof(CanOpenHeldCarts));
        OnPropertyChanged(nameof(CanToggleReturnMode));
        OnPropertyChanged(nameof(CanOpenDrawer));
        OnPropertyChanged(nameof(CanOpenOnlineOrders));
        OnPropertyChanged(nameof(CanConfirmCash));
        OnPropertyChanged(nameof(ZReportVarianceText));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(AmountLabel));
        OnPropertyChanged(nameof(AmountText));
        OnPropertyChanged(nameof(TenderHint));
        OnPropertyChanged(nameof(ShowCustomerChange));
        OnPropertyChanged(nameof(CustomerChangeText));
        OnPropertyChanged(nameof(ShowSettlement));
        OnPropertyChanged(nameof(SettlementText));
        OnPropertyChanged(nameof(IdleCustomerText));
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(CanOpenCloseShiftPanel));
        OnPropertyChanged(nameof(CanReprint));
        OnPropertyChanged(nameof(PaymentMethodLabel));
        OnPropertyChanged(nameof(WeChatCnyText));
        OnPropertyChanged(nameof(ShowWeChatQr));
        OnPropertyChanged(nameof(ShowInstaPayQr));
        OnPropertyChanged(nameof(IsCashPayment));
        OnPropertyChanged(nameof(ReturnModeButtonText));
        OnPropertyChanged(nameof(ShowReturnModeBanner));
        OnPropertyChanged(nameof(CanRedeemLoyalty));
        OnPropertyChanged(nameof(CanToggleRedeemLoyalty));
        OnPropertyChanged(nameof(RedeemLoyaltyButtonText));
        OnPropertyChanged(nameof(LoyaltyDiscountMinor));
        OnPropertyChanged(nameof(LoyaltyDiscountText));
        OnPropertyChanged(nameof(ShowLoyaltyDiscount));
        OnPropertyChanged(nameof(HasActiveQueueTicket));
        OnPropertyChanged(nameof(ActiveQueueCaption));
        RefreshCustomerDisplayMode(DateTimeOffset.Now);
    }

    private void RefreshCustomerDisplayMode(DateTimeOffset now)
    {
        var next = CustomerDisplayProjector.Resolve(
            HasLines,
            IsTendering,
            HasSettlement,
            _settlementAt,
            now);
        if (CustomerDisplayMode != next)
            CustomerDisplayMode = next;

        OnPropertyChanged(nameof(IsCfdIdle));
        OnPropertyChanged(nameof(IsCfdShopping));
        OnPropertyChanged(nameof(IsCfdPayment));
        OnPropertyChanged(nameof(IsCfdCompleted));
        OnPropertyChanged(nameof(IdleCustomerText));
    }

    partial void OnCustomerDisplayModeChanged(CustomerDisplayMode value)
    {
        OnPropertyChanged(nameof(IsCfdIdle));
        OnPropertyChanged(nameof(IsCfdShopping));
        OnPropertyChanged(nameof(IsCfdPayment));
        OnPropertyChanged(nameof(IsCfdCompleted));
        OnPropertyChanged(nameof(IdleCustomerText));
    }

    partial void OnNeedsLoginChanged(bool value) => RefreshCheckout();

    partial void OnNeedsOpenShiftChanged(bool value) => RefreshCheckout();

    private void HandleSyncChanged()
    {
        var snap = _sync.Read();
        var products = _database.ListActiveProducts();
        var demo = _database.GetSetting("catalog.source") == "demo";
        void Apply()
        {
            IsOnline = snap.IsOnline;
            SyncMessage = snap.Message;
            PendingOutbox = snap.PendingOutbox;
            DeadOutbox = snap.DeadOutbox;
            LastSyncError = snap.LastError ?? string.Empty;
            _products = products;
            CatalogCaption = FormatCatalogCaption(products.Count, demo);
            OnPropertyChanged(nameof(VisibleProducts));
            OnPropertyChanged(nameof(HasNoSearchResults));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private static string FormatCatalogCaption(int count, bool demoCatalog) =>
        demoCatalog
            ? $"{count.ToString(CultureInfo.InvariantCulture)} أصناف تجريبية في القاعدة المحلية"
            : $"{count.ToString(CultureInfo.InvariantCulture)} أصناف في القاعدة المحلية";

    private static bool Matches(CatalogProduct product, string term)
    {
        return product.NameAr.Contains(term, StringComparison.OrdinalIgnoreCase)
            || product.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (product.NameEn?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (product.NameZh?.Contains(term, StringComparison.Ordinal) ?? false)
            || (product.Barcode?.Contains(term, StringComparison.Ordinal) ?? false);
    }
}

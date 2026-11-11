using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgyChina.Pos.App.Services;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;
using EgyChina.Pos.Printing;
using EgyChina.Pos.Sync;

namespace EgyChina.Pos.App.ViewModels;

public partial class PosSession : ObservableObject
{
    private readonly PosDatabase _database;
    private IReadOnlyList<CatalogProduct> _products;
    private readonly SyncStatus _sync;
    private readonly IReceiptPrinter _printer;
    private readonly IPosErpGateway _erp;
    private readonly SyncOptions _syncOptions;
    private SyncWorker? _syncWorker;
    private string? _lastSaleId;
    private long _lastSaleTotalMinor;
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
        IPosErpGateway? erpGateway = null,
        SyncOptions? syncOptions = null)
    {
        _database = database;
        _products = products;
        _sync = sync;
        _printer = printer ?? new NullReceiptPrinter();
        _erp = erpGateway ?? NullPosErpGateway.Instance;
        _syncOptions = syncOptions ?? SyncOptions.FromEnvironment();
        DatabasePath = database.DatabasePath;
        CatalogCaption = FormatCatalogCaption(products.Count, demoCatalog, ReadCatalogUpdatedAt());
        ClockText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        IdleClockScript = CustomerDisplayProjector.IdleClockScript(DateTimeOffset.Now);
        IdleDateText = CustomerDisplayProjector.IdleDateText(DateTimeOffset.Now);
        NeedsLogin = true;
        SessionCaption = "سجّل الدخول برقم PIN الموظف (٤ أرقام من منظومة ERP)";
        PrinterCaption = _printer.IsConfigured
            ? "الطابعة جاهزة"
            : "الطابعة غير مضبوطة — البيع يُحفظ ويمكن إعادة الطباعة لاحقاً";
        IsPrinterReady = _printer.IsConfigured;

        Lines.CollectionChanged += (_, _) => RefreshCheckout();
        _sync.Changed += HandleSyncChanged;
        var snap = _sync.Read();
        IsOnline = snap.IsOnline;
        SyncMessage = snap.Message;
        PendingOutbox = snap.PendingOutbox;
        DeadOutbox = snap.DeadOutbox;
        LastSyncError = snap.LastError ?? string.Empty;
        if (_database.TryGetLastCompletedSaleSummary() is { } lastSale)
        {
            _lastSaleId = lastSale.SaleId;
            _lastSaleTotalMinor = lastSale.TotalMinor;
        }

        RefreshShiftState();
        ReloadHeldCarts();
        ReloadCategories();
        ReloadRecentSales();
        RefreshConnectivity();
        ApplyConfiguredWeChatRate();
        _ = SyncWeChatRateFromErpAsync();
        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch (Exception ex)
        {
            PosLog.Error("network availability hook failed", ex);
        }

        TryRestorePersistedSession();
    }

    public ObservableCollection<CartLine> Lines { get; } = new();

    public ObservableCollection<RecentSaleSummary> RecentSales { get; } = new();

    public string DatabasePath { get; }

    [ObservableProperty]
    private string _catalogCaption = string.Empty;

    [ObservableProperty]
    private string _catalogRefreshHint = string.Empty;

    [ObservableProperty]
    private bool _isCatalogRefreshing;

    public bool CanRefreshCatalog => !IsCatalogRefreshing;

    public bool HasCatalogRefreshHint => !string.IsNullOrWhiteSpace(CatalogRefreshHint);

    partial void OnCatalogRefreshHintChanged(string value) =>
        OnPropertyChanged(nameof(HasCatalogRefreshHint));

    public string ShellNotice { get; } = "نقد بالجنيه يُحفظ على هذا الجهاز. رفع البيع إلى المنظومة اختياري حسب إعداد المزامنة.";

    /// <summary>Wire the background worker so «تحديث الأصناف» can pulse an immediate tick.</summary>
    public void BindSyncWorker(SyncWorker worker) => _syncWorker = worker;

    [ObservableProperty]
    private string _printerCaption = string.Empty;

    [ObservableProperty]
    private bool _isPrinterReady;

    [ObservableProperty]
    private int _onlineOrdersCount;

    [ObservableProperty]
    private string _cashierAlert = string.Empty;

    [ObservableProperty]
    private string? _selectedCategoryId;

    [ObservableProperty]
    private bool _blockNewSales;

    public Action? RequestCustomerDisplay { get; set; }

    /// <summary>Toggles cashier kiosk FullScreen ↔ maximized window (taskbar visible).</summary>
    public Action? RequestToggleCashierKiosk { get; set; }

    public event Action? TenderOpened;

    public event Action<UiFocusTarget>? FocusRequested;

    /// <summary>Fired after a cart line is highlighted so cashier/CFD views can scroll to it.</summary>
    public event Action<CartLine>? CartLineRevealRequested;

    public ObservableCollection<HeldCartSummary> HeldCarts { get; } = new();

    public string LastSaleButtonText =>
        string.IsNullOrWhiteSpace(_lastSaleId)
            ? "آخر إيصال"
            : $"آخر · {Money.FormatEgp(Math.Abs(_lastSaleTotalMinor))}";

    public bool HasLastSale => !string.IsNullOrWhiteSpace(_lastSaleId);

    public ObservableCollection<CatalogCategory> Categories { get; } = new();

    [ObservableProperty]
    private CustomerDisplayMode _customerDisplayMode = CustomerDisplayMode.Idle;

    [ObservableProperty]
    private string _idleGreetingText = CustomerDisplayProjector.IdleGreeting(DateTimeOffset.Now);

    [ObservableProperty]
    private CustomerClockScript _idleClockScript = CustomerDisplayProjector.IdleClockScript(DateTimeOffset.Now);

    [ObservableProperty]
    private string _idleDateText = CustomerDisplayProjector.IdleDateText(DateTimeOffset.Now);

    public bool IsCfdIdle => CustomerDisplayMode == CustomerDisplayMode.Idle;

    public bool IsCfdShopping => CustomerDisplayMode == CustomerDisplayMode.Shopping;

    public bool IsCfdPayment => CustomerDisplayMode == CustomerDisplayMode.Payment;

    public bool IsCfdCompleted => CustomerDisplayMode == CustomerDisplayMode.Completed;

    public bool IsCfdPriceCheck => CustomerDisplayMode == CustomerDisplayMode.PriceCheck;

    public IReadOnlyList<PosPaymentMethod> PaymentOptions => PosPaymentMethods.CheckoutOptions;

    [ObservableProperty]
    private PosPaymentMethod _selectedPayment = PosPaymentMethod.Cash;

    [ObservableProperty]
    private string _exchangeRateText = WeChatFx.DefaultEgpPerCny.ToString("0.##", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isFetchingWeChatRate;

    public bool CanRefreshWeChatRate => IsTendering && IsWeChatPayment && !IsFetchingWeChatRate;

    public string WeChatRateRefreshButtonText => IsFetchingWeChatRate ? "…" : "من البنك";

    public string PaymentMethodLabel => PosPaymentMethods.LabelAr(SelectedPayment);

    public string PaymentMethodLabelEn => PosPaymentMethods.LabelEn(SelectedPayment);

    public string PaymentMethodLabelZh => PosPaymentMethods.LabelZh(SelectedPayment);

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
    private bool _showEditLine;

    [ObservableProperty]
    private string _editLineName = string.Empty;

    [ObservableProperty]
    private string _editQtyText = "1";

    [ObservableProperty]
    private string _editUnitPriceText = "0.00";

    [ObservableProperty]
    private LineDiscountKind _editDiscountKind = LineDiscountKind.None;

    [ObservableProperty]
    private string _editDiscountValueText = string.Empty;

    [ObservableProperty]
    private bool _editQtyLocked;

    [ObservableProperty]
    private string _editLineError = string.Empty;

    private CartLine? _editingLine;

    [ObservableProperty]
    private bool _showPrintPrompt;

    [ObservableProperty]
    private string _printPromptCaption = string.Empty;

    private string? _pendingPrintSaleId;
    private bool _pendingPrintKickDrawer;

    /// <summary>Ask before printing when distinct cart lines are 1–4; auto-print above that.</summary>
    private const int AutoPrintMinLineCount = 5;

    [ObservableProperty]
    private string _scanHint = IdleScanHint;

    [ObservableProperty]
    private bool _isScanToastVisible;

    [ObservableProperty]
    private bool _scanToastIsWarning;

    private CancellationTokenSource? _scanToastCts;

    private bool _connectivityToastsArmed;

    private bool _lastServerLinkUp;

    private const string IdleScanHint = "امسح الباركود أو اختر صنفاً.";

    public bool ShowScanToastInfo => IsScanToastVisible && !ScanToastIsWarning;

    public bool ShowScanToastWarning => IsScanToastVisible && ScanToastIsWarning;

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
    private bool _showOutboxPanel;

    [ObservableProperty]
    private bool _showMoreActions;

    [ObservableProperty]
    private bool _showStatusInfo;

    [ObservableProperty]
    private string _onlineOrdersError = string.Empty;

    [ObservableProperty]
    private string _outboxPanelHint = string.Empty;

    public ObservableCollection<OnlineOrderSummary> OnlineOrders { get; } = new();

    public ObservableCollection<OutboxHealthRow> OutboxRows { get; } = new();

    public bool HasNoOutboxRows => ShowOutboxPanel && OutboxRows.Count == 0;

    public bool HasOutboxPanelHint => !string.IsNullOrWhiteSpace(OutboxPanelHint);

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

    public bool IsCatalogAll => CatalogTab == CatalogTab.All && SelectedCategoryId is null;

    public bool IsCatalogFavorites => CatalogTab == CatalogTab.Favorites;

    public double CatalogAllEmphasis => IsCatalogAll ? 1.0 : 0.45;

    public double CatalogFavoritesEmphasis => IsCatalogFavorites ? 1.0 : 0.45;

    public bool HasCategories => Categories.Count > 0;

    public bool HasSelectedCategory => !string.IsNullOrWhiteSpace(SelectedCategoryId);

    public string SelectedCategoryCaption
    {
        get
        {
            if (SelectedCategoryId is null)
                return string.Empty;
            foreach (var category in Categories)
            {
                if (string.Equals(category.Id, SelectedCategoryId, StringComparison.Ordinal))
                    return $"القسم: {category.DisplayName}";
            }

            return "تصفية بقسم";
        }
    }

    public string SessionChipText =>
        NeedsLogin
            ? "تسجيل الدخول"
            : _currentShift is null
                ? "بدون وردية"
                : "وردية مفتوحة";

    public string OnlineOrdersButtonText =>
        OnlineOrdersCount > 0
            ? $"أونلاين ({OnlineOrdersCount.ToString(CultureInfo.InvariantCulture)})"
            : "طلبات أونلاين";

    public string OutboxStatusText
    {
        get
        {
            if (DeadOutbox > 0)
                return $"رفع معلّق {PendingOutbox.ToString(CultureInfo.InvariantCulture)} · فشل {DeadOutbox.ToString(CultureInfo.InvariantCulture)}";
            if (PendingOutbox <= 0)
                return "لا مبيعات بانتظار الرفع";
            return $"مبيعات بانتظار الرفع: {PendingOutbox.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    public bool IsOutboxHealthy => DeadOutbox == 0;

    public bool HasPriceCheckResult => PriceCheckResult is not null;

    public bool HasPriceCheckError => !string.IsNullOrWhiteSpace(PriceCheckError);

    public bool IsCfdPriceCheckWaiting => IsCfdPriceCheck && !HasPriceCheckResult && !HasPriceCheckError;

    public bool IsCfdPriceCheckMiss => IsCfdPriceCheck && HasPriceCheckError;

    public bool HasSupervisorError => !string.IsNullOrWhiteSpace(SupervisorError);

    public bool BlocksScanning =>
        ShowNotFound
        || ShowPriceCheck
        || ShowHeldCarts
        || ShowSupervisorPin
        || ShowOnlineOrders
        || ShowOutboxPanel
        || ShowEditLine
        || ShowPrintPrompt;

    public bool HasEditLineError => !string.IsNullOrWhiteSpace(EditLineError);

    public bool ShowEditDiscountValue => EditDiscountKind != LineDiscountKind.None;

    public bool IsEditDiscountNone => EditDiscountKind == LineDiscountKind.None;

    public bool IsEditDiscountPercent => EditDiscountKind == LineDiscountKind.Percent;

    public bool IsEditDiscountFlat => EditDiscountKind == LineDiscountKind.Flat;

    public string EditDiscountPlaceholder =>
        EditDiscountKind == LineDiscountKind.Percent
            ? "النسبة مثل 10"
            : "المبلغ بالجنيه";

    public string EditPreviewTotalText
    {
        get
        {
            if (!TryParseEditDraft(out var qty, out var unit, out var kind, out var value, out _))
                return Money.FormatEgp(0);
            return Money.FormatEgp(CartLinePricing.ComputeLineTotalMinor(qty, unit, kind, value));
        }
    }

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
    [NotifyPropertyChangedFor(nameof(InternetToolTip))]
    private bool _hasInternetLink;

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
    private int _lastPointsEarned;

    [ObservableProperty]
    private string _saleError = string.Empty;

    public bool HasLastPointsEarned => LastPointsEarned > 0;

    public string LastPointsEarnedText =>
        HasLastPointsEarned
            ? $"+{LastPointsEarned.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

    public string OnlineText => IsOnline ? "السيرفر متصل" : "السيرفر غير مربوط";

    public bool HasRecentSales => RecentSales.Count > 0;

    public string InternetToolTip =>
        HasInternetLink ? "الإنترنت متصل" : "لا يوجد اتصال إنترنت";

    /// <summary>ERP link is only meaningful when the PC actually has internet.</summary>
    public bool IsServerLinkUp => HasInternetLink && IsOnline;

    public string ServerToolTip =>
        !HasInternetLink
            ? "السيرفر غير متاح — لا إنترنت"
            : IsOnline
                ? "السيرفر متصل"
                : "السيرفر غير مربوط";

    public string OutboxCaption
    {
        get
        {
            var pending = PendingOutbox.ToString(CultureInfo.InvariantCulture);
            if (DeadOutbox > 0)
                return $"بانتظار الرفع: {pending} · فشل: {DeadOutbox.ToString(CultureInfo.InvariantCulture)}";
            return $"بانتظار الرفع: {pending}";
        }
    }

    public bool HasSyncError => !string.IsNullOrWhiteSpace(LastSyncError);

    public string SyncErrorCaption => HasSyncError ? $"آخر خطأ: {LastSyncError}" : string.Empty;

    public bool HasDisplayBanner => !string.IsNullOrWhiteSpace(DisplayBanner);

    public bool IsCartEmpty => Lines.Count == 0;

    /// <summary>Cart lines stay hidden while tendering so Confirm fits on short tills.</summary>
    public bool ShowCartLines => !IsCartEmpty && !IsTendering;

    public bool ShowEmptyCartHint => IsCartEmpty && !IsTendering;

    /// <summary>Loyalty controls are cart-stage only; discount still shows in the tender panel.</summary>
    public bool ShowLoyaltyPanelOutsideTender => HasLoyaltyCustomer && !IsTendering;

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
        CanOperate && !IsTendering && !ShowSupervisorPin && !ShowNotFound && !ShowPriceCheck && !ShowHeldCarts && !ShowOnlineOrders && !ShowEditLine;

    public bool CanOpenDrawer =>
        CanOperate && !IsTendering && !ShowSupervisorPin && !ShowNotFound && !ShowPriceCheck && !ShowHeldCarts && !ShowOnlineOrders && !ShowEditLine;

    public bool CanOpenOnlineOrders =>
        CanOperate && !IsTendering && !ShowSupervisorPin && !ShowNotFound && !ShowPriceCheck && !ShowHeldCarts && !ShowOnlineOrders && !ShowEditLine;

    public bool HasHeldCarts => HeldCarts.Count > 0;

    public bool HasHeldCartError => !string.IsNullOrWhiteSpace(HeldCartError);

    public string HeldCartsButtonText =>
        HasHeldCarts
            ? $"السلال المعلّقة ({HeldCarts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : "السلال المعلّقة";

    public bool CanLock => !NeedsLogin && _currentCashier is not null;

    public bool CanOpenCloseShiftPanel => CanOperate && !IsTendering;

    public bool CanReprint => !string.IsNullOrWhiteSpace(_lastSaleId) && !IsTendering && !NeedsLogin;

    public string LineCountText =>
        Lines.Count == 0
            ? "السلة فارغة"
            : $"{Lines.Count.ToString(CultureInfo.InvariantCulture)} أصناف";

    public string LineCountTextZh =>
        Lines.Count == 0
            ? "购物车为空"
            : $"{Lines.Count.ToString(CultureInfo.InvariantCulture)} 件商品";

    public long SubtotalMinor => Lines.Sum(line => checked(line.LineTotalMinor));

    public long TotalMinor => Math.Max(0, checked(SubtotalMinor - LoyaltyDiscountMinor));

    public string TotalText => Money.FormatEgp(TotalMinor);

    /// <summary>Digits only for CFD header (currency shown separately as ج.م / 埃镑).</summary>
    public string TotalAmountText => Money.FormatAmount(TotalMinor);

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
        IsTendering && IsCashPayment && Money.TryParseEgp(TenderText, out _) && !IsReturnMode;

    public bool CustomerChangeIsShortfall =>
        ShowCustomerChange && ParsedTender < TotalMinor;

    public bool ShowCustomerChangeDue =>
        ShowCustomerChange && !CustomerChangeIsShortfall;

    public string CustomerChangeText
    {
        get
        {
            if (!ShowCustomerChange)
                return string.Empty;
            var diff = ParsedTender - TotalMinor;
            return diff >= 0
                ? "الباقي " + Money.FormatEgp(diff)
                : "المتبقي " + Money.FormatEgp(-diff);
        }
    }

    public bool ShowQuickCashOptions =>
        IsTendering && IsCashPayment && !IsReturnMode && QuickCashOptions.Count > 0;

    public ObservableCollection<QuickCashOption> QuickCashOptions { get; } = new();

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
            // Cash may be a nearby note denomination (including slight underpayment).
            return tender > 0;
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
                    ? "اختر فئة نقدية سريعة أو اكتب المبلغ المستلم."
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
                var diff = tender - TotalMinor;
                if (diff < 0)
                    return "متبقي للتحصيل " + Money.FormatEgp(-diff);
                if (diff == 0)
                    return "المبلغ مطابق للمستحق.";
                return "الباقي للعميل " + Money.FormatEgp(diff);
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
                : SelectedCategoryId is { Length: > 0 } categoryId
                    ? _products.Where(product => string.Equals(product.CategoryId, categoryId, StringComparison.Ordinal))
                    : _products;

            var term = Search.Trim();
            if (term.Length > 0)
                return source.Where(product => Matches(product, term)).ToArray();

            // Default shelf: favorites first, then the rest (still the full catalog).
            if (CatalogTab == CatalogTab.All && SelectedCategoryId is null)
            {
                return source
                    .OrderByDescending(product => product.IsFavorite)
                    .ThenBy(product => product.NameAr, StringComparer.Ordinal)
                    .ToArray();
            }

            return source.ToArray();
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
            AnnounceHint(CashierAlert.Length > 0 ? CashierAlert : "القرص ممتلئ. لا يمكن إضافة أصناف الآن.");
            return;
        }

        if (!CanOperate)
        {
            AnnounceHint(NeedsLogin ? "سجّل الدخول أولاً" : "افتح وردية قبل البيع");
            return;
        }

        HasSettlement = false;
        _settlementAt = null;
        CartLine line;
        var existing = Lines.FirstOrDefault(item => item.ProductId == product.Id);
        if (existing is null)
        {
            line = new CartLine(product, RefreshCheckout, RemoveLine, HighlightLine, AnnounceCartLineQtyAdjusted);
            Lines.Add(line);
            HighlightLine(line);
        }
        else
        {
            existing.Qty++;
            line = existing;
            HighlightLine(line);
        }

        AnnounceHint($"أُضيف للسلة: {line.DisplayNameAr} · × {line.QtyText}");
        ScanSound.PlaySuccess();
        RequestFocus(UiFocusTarget.Barcode);
    }

    /// <summary>Scale label: one cart line priced at the embedded total (qty 1).</summary>
    public void AddScalePricedProduct(CatalogProduct product, long embeddedTotalMinor, string scaleDigits)
    {
        if (BlocksScanning)
            return;

        if (BlockNewSales)
        {
            AnnounceHint(CashierAlert.Length > 0 ? CashierAlert : "القرص ممتلئ. لا يمكن إضافة أصناف الآن.");
            return;
        }

        if (!CanOperate)
        {
            AnnounceHint(NeedsLogin ? "سجّل الدخول أولاً" : "افتح وردية قبل البيع");
            return;
        }

        if (embeddedTotalMinor <= 0)
            return;

        HasSettlement = false;
        _settlementAt = null;
        var display = product.DisplayNameAr;
        var line = new CartLine(
            product.Id,
            scaleDigits,
            display,
            product.NameZh,
            embeddedTotalMinor,
            qty: 1,
            isScalePriced: true,
            LineDiscountKind.None,
            discountValue: 0,
            RefreshCheckout,
            RemoveLine,
            HighlightLine,
            AnnounceCartLineQtyAdjusted);
        Lines.Add(line);
        HighlightLine(line);
        AnnounceHint($"أُضيف للسلة: {display} · ميزان");
        ScanSound.PlaySuccess();
        RequestFocus(UiFocusTarget.Barcode);
    }

    private void RemoveLine(CartLine line)
    {
        if (!CanOperate || IsTendering || ShowSupervisorPin)
            return;
        // Cashiers may remove cart lines freely; supervisor PIN is for returns only.
        RemoveLineNow(line);
    }

    private void RemoveLineNow(CartLine line)
    {
        if (_editingLine is not null && ReferenceEquals(_editingLine, line))
        {
            ShowEditLine = false;
            _editingLine = null;
            EditLineError = string.Empty;
        }

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
        CartLineRevealRequested?.Invoke(line);
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

        if (ScaleBarcode.TryParse(code, out var scale))
        {
            Barcode = string.Empty;
            var scaleProduct = _database.FindByBarcode(scale.Plu)
                ?? _database.FindByBarcode(scale.Plu.TrimStart('0'));
            if (scaleProduct is null)
            {
                OpenNotFound(code);
                return;
            }

            AddScalePricedProduct(scaleProduct, scale.EmbeddedTotalMinor, scale.Digits);
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
        var line = new CartLine(payload, RefreshCheckout, RemoveLine, HighlightLine, AnnounceCartLineQtyAdjusted);
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
                ClearLoyaltyState();
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
        if (!HasLoyaltyCustomer)
        {
            ClearLoyaltyState();
            return;
        }

        ClearLoyaltyState();
        AnnounceHint("أُزيل عميل الولاء من الفاتورة.");
    }

    private void ClearLoyaltyState()
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
                var line = new CartLine(payload, RefreshCheckout, RemoveLine, HighlightLine, AnnounceCartLineQtyAdjusted);
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
        SelectedCategoryId = null;
        CatalogTab = CatalogTab.All;
        NotifyCatalogFilterChanged();
    }

    [RelayCommand]
    private void ShowFavoritesCatalog()
    {
        SelectedCategoryId = null;
        CatalogTab = CatalogTab.Favorites;
        NotifyCatalogFilterChanged();
    }

    [RelayCommand]
    private void SelectCategory(CatalogCategory? category)
    {
        if (category is null)
            return;
        CatalogTab = CatalogTab.All;
        SelectedCategoryId = category.Id;
        NotifyCatalogFilterChanged();
    }

    private void NotifyCatalogFilterChanged()
    {
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
        OnPropertyChanged(nameof(IsCatalogAll));
        OnPropertyChanged(nameof(IsCatalogFavorites));
        OnPropertyChanged(nameof(CatalogAllEmphasis));
        OnPropertyChanged(nameof(CatalogFavoritesEmphasis));
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(HasSelectedCategory));
        OnPropertyChanged(nameof(SelectedCategoryCaption));
    }

    private void ReloadCategories()
    {
        Categories.Clear();
        foreach (var category in _database.ListCategories())
            Categories.Add(category);
        OnPropertyChanged(nameof(HasCategories));
        OnPropertyChanged(nameof(Categories));
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

    /// <summary>Reloads shelf data from SQLite (e.g. after live image-url sync).</summary>
    public void ReloadCatalogFromDatabase()
    {
        var demo = _database.GetSetting("catalog.source") == "demo";
        _products = _database.ListActiveProducts();
        CatalogCaption = FormatCatalogCaption(_products.Count, demo, ReadCatalogUpdatedAt());
        ReloadCategories();
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
    }

    /// <summary>Rebinds visible catalog cards after local product images are downloaded.</summary>
    public void NotifyCatalogVisualRefresh()
    {
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoFavorites));
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
        RefreshCustomerDisplayMode(DateTimeOffset.Now);
        RequestFocus(UiFocusTarget.PriceCheck);
    }

    [RelayCommand]
    private void ClosePriceCheck()
    {
        ShowPriceCheck = false;
        PriceCheckQuery = string.Empty;
        PriceCheckResult = null;
        PriceCheckError = string.Empty;
        RefreshCustomerDisplayMode(DateTimeOffset.Now);
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
            RefreshCustomerDisplayMode(DateTimeOffset.Now);
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
        RefreshCustomerDisplayMode(DateTimeOffset.Now);
        RequestFocus(UiFocusTarget.PriceCheck);
    }

    [RelayCommand]
    private void DismissNotFound()
    {
        ShowNotFound = false;
        NotFoundBarcode = string.Empty;
        RequestFocus(UiFocusTarget.Barcode);
    }

    public void OpenEditLine(CartLine line)
    {
        if (!CanOperate || IsTendering || line is null)
            return;
        if (ShowSupervisorPin || ShowNotFound || ShowPriceCheck || ShowHeldCarts || ShowOnlineOrders)
            return;

        _editingLine = line;
        EditLineName = line.DisplayNameAr;
        EditQtyText = line.QtyText;
        EditUnitPriceText = Money.FormatAmount(line.UnitPriceMinor);
        EditDiscountKind = line.DiscountKind;
        EditDiscountValueText = line.DiscountKind switch
        {
            LineDiscountKind.Percent when line.DiscountValue > 0 =>
                line.DiscountValue.ToString(CultureInfo.InvariantCulture),
            LineDiscountKind.Flat when line.DiscountValue > 0 =>
                Money.FormatAmount(line.DiscountValue),
            _ => string.Empty,
        };
        EditQtyLocked = line.IsScalePriced;
        EditLineError = string.Empty;
        ShowEditLine = true;
        HighlightLine(line);
        RequestFocus(UiFocusTarget.EditLineQty);
    }

    [RelayCommand]
    private void CloseEditLine()
    {
        ShowEditLine = false;
        _editingLine = null;
        EditLineError = string.Empty;
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void SetEditDiscountNone() => EditDiscountKind = LineDiscountKind.None;

    [RelayCommand]
    private void SetEditDiscountPercent() => EditDiscountKind = LineDiscountKind.Percent;

    [RelayCommand]
    private void SetEditDiscountFlat() => EditDiscountKind = LineDiscountKind.Flat;

    [RelayCommand]
    private void ApplyEditLine()
    {
        if (_editingLine is null || !CanOperate || IsTendering)
            return;

        if (!TryParseEditDraft(out var qty, out var unit, out var kind, out var value, out var error))
        {
            EditLineError = error;
            return;
        }

        var name = _editingLine.DisplayNameAr;
        _editingLine.ApplyEdit(qty, unit, kind, value);
        var qtyText = _editingLine.QtyText;
        ShowEditLine = false;
        _editingLine = null;
        EditLineError = string.Empty;
        RefreshCheckout();
        AnnounceHint($"تم تعديل: {name} · × {qtyText}");
        RequestFocus(UiFocusTarget.Barcode);
    }

    private bool TryParseEditDraft(
        out int qty,
        out long unitPriceMinor,
        out LineDiscountKind kind,
        out long discountValue,
        out string error)
    {
        qty = 1;
        unitPriceMinor = 0;
        kind = EditDiscountKind;
        discountValue = 0;
        error = string.Empty;

        if (EditQtyLocked && _editingLine is not null)
            qty = _editingLine.Qty;
        else if (!int.TryParse(EditQtyText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out qty)
                 || qty < 1
                 || qty > 999)
        {
            error = "الكمية يجب أن تكون رقماً صحيحاً من 1 إلى 999.";
            return false;
        }

        if (!Money.TryParseEgp(EditUnitPriceText, out unitPriceMinor))
        {
            error = "سعر الوحدة غير صالح.";
            return false;
        }

        if (kind == LineDiscountKind.None)
        {
            discountValue = 0;
            return true;
        }

        if (kind == LineDiscountKind.Percent)
        {
            if (!long.TryParse(EditDiscountValueText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pct)
                || pct <= 0
                || pct > 100)
            {
                error = "نسبة الخصم يجب أن تكون من 1 إلى 100.";
                return false;
            }

            discountValue = pct;
            return true;
        }

        if (!Money.TryParseEgp(EditDiscountValueText, out discountValue) || discountValue <= 0)
        {
            error = "مبلغ الخصم غير صالح.";
            return false;
        }

        var gross = checked(unitPriceMinor * (long)qty);
        if (discountValue > gross)
        {
            error = "الخصم أكبر من إجمالي السطر.";
            return false;
        }

        return true;
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
            AnnounceHint("تم تعليق السلة.");
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("hold cart failed", ex);
            HeldCartError = ArabicUi.FromException(ex, "تعذر تعليق السلة.");
            AnnounceHint(HeldCartError);
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
                AnnounceHint(HeldCartError);
                ReloadHeldCarts();
                return;
            }

            Lines.Clear();
            ClearHighlight();
            HasSettlement = false;
            _settlementAt = null;
            foreach (var payload in lines)
            {
                var line = new CartLine(payload, RefreshCheckout, RemoveLine, HighlightLine, AnnounceCartLineQtyAdjusted);
                Lines.Add(line);
            }

            if (Lines.Count > 0)
                HighlightLine(Lines[^1]);

            ShowHeldCarts = false;
            ReloadHeldCarts();
            AnnounceHint($"استُرجعت سلة {held.Label}");
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
        }
        catch (Exception ex)
        {
            PosLog.Error("resume held cart failed", ex);
            HeldCartError = ArabicUi.FromException(ex, "تعذر استرجاع السلة.");
            AnnounceHint(HeldCartError);
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
            AnnounceHint("تم حذف السلة المعلّقة.");
        }
        catch (Exception ex)
        {
            PosLog.Error("discard held cart failed", ex);
            HeldCartError = ArabicUi.FromException(ex, "تعذر حذف السلة المعلّقة.");
            AnnounceHint(HeldCartError);
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

    private void ReloadRecentSales()
    {
        RecentSales.Clear();
        foreach (var sale in _database.ListRecentCompletedSales(3))
            RecentSales.Add(sale);
        OnPropertyChanged(nameof(HasRecentSales));
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = RefreshConnectivityAsync(forceSaleRefresh: true));

    private void RefreshConnectivity() => _ = RefreshConnectivityAsync(forceSaleRefresh: false);

    private async Task RefreshConnectivityAsync(bool forceSaleRefresh)
    {
        // Real NCSI/TCP probe — GetIsNetworkAvailable alone stays green on LAN without internet.
        var probed = await InternetReachability.ProbeAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var changed = HasInternetLink != probed;
            HasInternetLink = probed;
            if (!_connectivityToastsArmed)
            {
                _connectivityToastsArmed = true;
                _lastServerLinkUp = IsServerLinkUp;
            }
            else if (changed)
            {
                AnnounceHint(probed
                    ? "الإنترنت رجع."
                    : "انقطع الإنترنت — البيع المحلي شغال.");
                _lastServerLinkUp = IsServerLinkUp;
            }

            if (changed || forceSaleRefresh)
                ReloadRecentSales();
        }).GetTask().ConfigureAwait(false);
    }

    partial void OnHeldCartErrorChanged(string value) => OnPropertyChanged(nameof(HasHeldCartError));

    public void SetClock(DateTime now)
    {
        ClockText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (now.Second % 5 == 0)
            RefreshConnectivity();
        var stamp = new DateTimeOffset(now);
        IdleGreetingText = CustomerDisplayProjector.IdleGreeting(stamp);
        IdleClockScript = CustomerDisplayProjector.IdleClockScript(stamp);
        IdleDateText = CustomerDisplayProjector.IdleDateText(stamp);
        if (HasSettlement && CustomerDisplayProjector.IsCompletedExpired(_settlementAt, stamp))
        {
            HasSettlement = false;
            _settlementAt = null;
            LastPointsEarned = 0;
        }

        RefreshCustomerDisplayMode(stamp);
    }

    partial void OnBarcodeChanged(string value)
    {
        // Single search/scan bar: typing filters the grid; Enter submits barcode.
        if (!string.Equals(Search, value, StringComparison.Ordinal))
            Search = value;
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

    partial void OnShowOutboxPanelChanged(bool value)
    {
        OnPropertyChanged(nameof(BlocksScanning));
        OnPropertyChanged(nameof(HasNoOutboxRows));
        OnPropertyChanged(nameof(CanToggleReturnMode));
        OnPropertyChanged(nameof(CanOpenDrawer));
        OnPropertyChanged(nameof(CanOpenOnlineOrders));
    }

    partial void OnShowHeldCartsChanged(bool value)
    {
        OnPropertyChanged(nameof(BlocksScanning));
        OnPropertyChanged(nameof(CanHoldCart));
        OnPropertyChanged(nameof(CanOpenHeldCarts));
    }

    partial void OnShowEditLineChanged(bool value) => OnPropertyChanged(nameof(BlocksScanning));

    partial void OnShowPrintPromptChanged(bool value) => OnPropertyChanged(nameof(BlocksScanning));

    partial void OnEditDiscountKindChanged(LineDiscountKind value)
    {
        OnPropertyChanged(nameof(ShowEditDiscountValue));
        OnPropertyChanged(nameof(IsEditDiscountNone));
        OnPropertyChanged(nameof(IsEditDiscountPercent));
        OnPropertyChanged(nameof(IsEditDiscountFlat));
        OnPropertyChanged(nameof(EditDiscountPlaceholder));
        OnPropertyChanged(nameof(EditPreviewTotalText));
        if (value == LineDiscountKind.None)
            EditDiscountValueText = string.Empty;
    }

    partial void OnEditQtyTextChanged(string value) => OnPropertyChanged(nameof(EditPreviewTotalText));

    partial void OnEditUnitPriceTextChanged(string value) => OnPropertyChanged(nameof(EditPreviewTotalText));

    partial void OnEditDiscountValueTextChanged(string value) => OnPropertyChanged(nameof(EditPreviewTotalText));

    partial void OnEditLineErrorChanged(string value) => OnPropertyChanged(nameof(HasEditLineError));

    partial void OnPriceCheckResultChanged(PriceCheckResult? value)
    {
        OnPropertyChanged(nameof(HasPriceCheckResult));
        OnPropertyChanged(nameof(IsCfdPriceCheckWaiting));
        OnPropertyChanged(nameof(IsCfdPriceCheckMiss));
    }

    partial void OnPriceCheckErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasPriceCheckError));
        OnPropertyChanged(nameof(IsCfdPriceCheckWaiting));
        OnPropertyChanged(nameof(IsCfdPriceCheckMiss));
    }

    partial void OnIsOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(IsServerLinkUp));
        OnPropertyChanged(nameof(ServerToolTip));
        MaybeAnnounceServerLink();
    }

    partial void OnHasInternetLinkChanged(bool value)
    {
        OnPropertyChanged(nameof(InternetToolTip));
        OnPropertyChanged(nameof(IsServerLinkUp));
        OnPropertyChanged(nameof(ServerToolTip));
    }

    partial void OnPendingOutboxChanged(int value)
    {
        OnPropertyChanged(nameof(OutboxCaption));
        OnPropertyChanged(nameof(OutboxStatusText));
        OnPropertyChanged(nameof(IsOutboxHealthy));
    }

    partial void OnDeadOutboxChanged(int value)
    {
        OnPropertyChanged(nameof(OutboxCaption));
        OnPropertyChanged(nameof(OutboxStatusText));
        OnPropertyChanged(nameof(IsOutboxHealthy));
    }

    partial void OnLastPointsEarnedChanged(int value)
    {
        OnPropertyChanged(nameof(HasLastPointsEarned));
        OnPropertyChanged(nameof(LastPointsEarnedText));
    }

    partial void OnSelectedCategoryIdChanged(string? value) => NotifyCatalogFilterChanged();

    partial void OnOnlineOrdersCountChanged(int value) =>
        OnPropertyChanged(nameof(OnlineOrdersButtonText));

    partial void OnLastSyncErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasSyncError));
        OnPropertyChanged(nameof(SyncErrorCaption));
    }

    partial void OnDisplayBannerChanged(string value) => OnPropertyChanged(nameof(HasDisplayBanner));

    partial void OnCashierAlertChanged(string value) => OnPropertyChanged(nameof(HasCashierAlert));

    partial void OnScanHintChanged(string value) => ShowScanToastFromHint(value);

    partial void OnIsScanToastVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowScanToastInfo));
        OnPropertyChanged(nameof(ShowScanToastWarning));
    }

    partial void OnScanToastIsWarningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowScanToastInfo));
        OnPropertyChanged(nameof(ShowScanToastWarning));
    }

    private void AnnounceHint(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (string.Equals(ScanHint, message, StringComparison.Ordinal))
        {
            ShowScanToastFromHint(message);
            return;
        }

        ScanHint = message;
    }

    private void AnnounceCartLineQtyAdjusted(CartLine line) =>
        AnnounceHint($"تم تعديل: {line.DisplayNameAr} · × {line.QtyText}");

    private void MaybeAnnounceServerLink()
    {
        if (!_connectivityToastsArmed)
            return;

        var now = IsServerLinkUp;
        if (now == _lastServerLinkUp)
            return;

        _lastServerLinkUp = now;
        if (!HasInternetLink)
            return;

        AnnounceHint(now
            ? "السيرفر رجع."
            : "السيرفر غير متصل — المبيعات هتتزامن لاحقاً.");
    }

    private void ShowScanToastFromHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, IdleScanHint, StringComparison.Ordinal))
        {
            IsScanToastVisible = false;
            _scanToastCts?.Cancel();
            return;
        }

        ScanToastIsWarning = LooksLikeScanWarning(value);
        IsScanToastVisible = true;
        _ = DismissScanToastAfterDelayAsync(ScanToastIsWarning ? 4200 : 2200);
    }

    private static bool LooksLikeScanWarning(string value)
    {
        return value.Contains("تعذر", StringComparison.Ordinal)
               || value.Contains("غير موجود", StringComparison.Ordinal)
               || value.Contains("غير مضبوطة", StringComparison.Ordinal)
               || value.Contains("غير متصل", StringComparison.Ordinal)
               || value.Contains("انقطع", StringComparison.Ordinal)
               || value.Contains("لا يمكن", StringComparison.Ordinal)
               || value.Contains("ممتلئ", StringComparison.Ordinal)
               || value.Contains("أكّد", StringComparison.Ordinal)
               || value.Contains("خطأ", StringComparison.Ordinal)
               || value.Contains("سجّل الدخول", StringComparison.Ordinal)
               || value.Contains("افتح وردية", StringComparison.Ordinal)
               || value.Contains("لم يُعثر", StringComparison.Ordinal)
               || value.Contains("لا يوجد", StringComparison.Ordinal)
               || value.Contains("لا توجد", StringComparison.Ordinal);
    }

    private async Task DismissScanToastAfterDelayAsync(int delayMs)
    {
        _scanToastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanToastCts = cts;
        var token = cts.Token;
        try
        {
            await Task.Delay(delayMs, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    IsScanToastVisible = false;
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    partial void OnIsTenderingChanged(bool value)
    {
        RefreshCheckout();
        OnPropertyChanged(nameof(CanRefreshWeChatRate));
    }

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

        var hadLines = HasLines;
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
        if (hadLines)
            AnnounceHint("تم تفريغ السلة.");
    }

    private async Task CompleteQueueTicketAsync(string code, string saleId)
    {
        try
        {
            var ok = await _erp.CompleteQueueTicketAsync(code, saleId, CancellationToken.None).ConfigureAwait(true);
            if (!ok)
            {
                PosLog.Error($"queue ticket complete failed code={code}");
                await SetPrintHintAsync("البيع محفوظ، لكن تعذر إغلاق القسيمة على السيرفر.").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            PosLog.Error($"queue ticket complete failed code={code}", ex);
            await SetPrintHintAsync("البيع محفوظ، لكن تعذر إغلاق القسيمة على السيرفر.").ConfigureAwait(false);
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
        ShowMoreActions = false;
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
    private void ShowCustomerDisplay()
    {
        ShowMoreActions = false;
        RequestCustomerDisplay?.Invoke();
    }

    public string CashierDisplayModeButtonText =>
        ScreenLayout.CashierKiosk ? "تصغير النافذة (إظهار شريط ويندوز)" : "ملء الشاشة بالكامل";

    [RelayCommand]
    private void ToggleCashierKiosk()
    {
        ShowMoreActions = false;
        RequestToggleCashierKiosk?.Invoke();
        OnPropertyChanged(nameof(CashierDisplayModeButtonText));
        ScanHint = ScreenLayout.CashierKiosk
            ? "وضع الكشك: الشاشة كاملة. ضغطتان على الشريط العلوي للتصغير."
            : "وضع النافذة: شريط ويندوز ظاهر. ضغطتان على الشريط العلوي لملء الشاشة.";
    }

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

        ApplyCashierLogin(result.Cashier, persist: true);
    }

    [RelayCommand]
    private void LockSession()
    {
        if (_currentCashier is null && !NeedsLogin)
            return;

        Lines.Clear();
        ClearHighlight();
        IsTendering = false;
        HasSettlement = false;
        _settlementAt = null;
        LastPointsEarned = 0;
        TenderText = string.Empty;
        SaleError = string.Empty;
        ShowCloseShift = false;
        IsReturnMode = false;
        _activeQueueTicketCode = null;
        ClearLoyaltyState();
        ClearSupervisorChallenge();
        _currentCashier = null;
        CashierName = string.Empty;
        NeedsLogin = true;
        _database.ClearCashierSession();
        SessionCaption = _currentShift is null
            ? "تم تسجيل الخروج. أدخل PIN الموظف للدخول."
            : "تم تسجيل الخروج. الوردية ما زالت مفتوحة — أدخل PIN الموظف للمتابعة.";
        OnPropertyChanged(nameof(SessionChipText));
        RefreshCheckout();
        RequestFocus(UiFocusTarget.Pin);
    }

    private void TryRestorePersistedSession()
    {
        var restored = _database.TryRestoreCashierSession();
        if (restored is null)
            return;
        ApplyCashierLogin(restored, persist: true);
    }

    private void ApplyCashierLogin(CashierRecord cashier, bool persist)
    {
        _currentCashier = cashier;
        CashierName = cashier.DisplayName;
        NeedsLogin = false;
        PinText = string.Empty;
        AuthError = string.Empty;
        if (persist)
            _database.PersistCashierSession(cashier.Id);
        RefreshShiftState();
        OnPropertyChanged(nameof(SessionChipText));
        RefreshCheckout();
        RequestFocus(_currentShift is null ? UiFocusTarget.OpeningFloat : UiFocusTarget.Barcode);
    }

    /// <summary>Called after background live staff pull finishes (new cashiers become loginable).</summary>
    public void NotifyStaffSyncCompleted()
    {
        if (string.Equals(_database.GetSetting(PosDatabase.StaffSourceSettingKey), "erp", StringComparison.Ordinal))
        {
            SessionCaption = "سجّل الدخول برقم PIN الموظف من منظومة ERP";
            if (!string.IsNullOrWhiteSpace(DeviceCredentialStore.Load()) || !string.IsNullOrWhiteSpace(_syncOptions.DeviceToken))
                SyncMessage = "الموظفون والجهاز مربوطان بسيرفر ERP.";
        }
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
            OnPropertyChanged(nameof(SessionChipText));
            RefreshCheckout();
            AnnounceHint("تم فتح الوردية.");
            RequestFocus(UiFocusTarget.Barcode);
            _ = PushOpenShiftToErpAsync(_currentShift);
        }
        catch (Exception ex)
        {
            PosLog.Error("open shift failed", ex);
            ShiftError = ArabicUi.FromException(ex, "تعذر فتح الوردية.");
            AnnounceHint(ShiftError);
        }
    }

    [RelayCommand]
    private void BeginCloseShift()
    {
        ShowMoreActions = false;
        ShowStatusInfo = false;
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
        ShowMoreActions = false;
        ShowStatusInfo = false;
        ShowOnlineOrders = true;
        _ = RefreshOnlineOrdersAsync();
    }

    [RelayCommand]
    private void OpenOutboxPanel()
    {
        ShowMoreActions = false;
        ShowStatusInfo = false;
        ShowOutboxPanel = true;
        RefreshOutboxPanel();
    }

    [RelayCommand]
    private void CloseOutboxPanel()
    {
        ShowOutboxPanel = false;
        OutboxPanelHint = string.Empty;
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void RefreshOutboxPanel()
    {
        try
        {
            OutboxRows.Clear();
            foreach (var row in _database.ListOutboxHealth())
                OutboxRows.Add(row);
            var snap = _database.ReadOutboxSnapshot();
            PendingOutbox = snap.Pending + snap.Inflight;
            DeadOutbox = snap.Dead;
            LastSyncError = snap.LastError ?? string.Empty;
            OnPropertyChanged(nameof(HasNoOutboxRows));
            OnPropertyChanged(nameof(HasSyncError));
            OnPropertyChanged(nameof(SyncErrorCaption));
        }
        catch (Exception ex)
        {
            PosLog.Error("outbox panel refresh failed", ex);
            OutboxPanelHint = ArabicUi.FromException(ex, "تعذر فتح قائمة انتظار الرفع.");
            OnPropertyChanged(nameof(HasOutboxPanelHint));
        }
    }

    [RelayCommand]
    private void RetryOutboxSync()
    {
        var dead = _database.RequeueDeadOutbox();
        var nudged = _database.NudgeAllPendingOutbox();
        RefreshOutboxPanel();
        OutboxPanelHint = $"أُعيد طابور {dead.ToString(CultureInfo.InvariantCulture)} تالف · نُبّه {nudged.ToString(CultureInfo.InvariantCulture)} معلّق. ستحاول المزامنة تلقائياً.";
        OnPropertyChanged(nameof(HasOutboxPanelHint));
        _syncWorker?.RequestImmediateTick(forceFullCatalog: false);
    }

    /// <summary>
    /// Professional hybrid: force a full public shelf rebuild now, then pulse the sync worker.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshCatalog))]
    private async Task RefreshCatalogAsync()
    {
        if (IsCatalogRefreshing)
            return;

        IsCatalogRefreshing = true;
        CatalogRefreshHint = "جاري تحديث الأصناف من ERP…";
        OnPropertyChanged(nameof(CanRefreshCatalog));
        RefreshCatalogCommand.NotifyCanExecuteChanged();
        ShowMoreActions = false;

        try
        {
            var origin = _syncOptions.BaseAddress ?? SyncOptions.DefaultErpOrigin;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1-catalog-refresh");
            var live = await LiveStoreCatalogSync.ApplyAsync(_database, origin, http, CancellationToken.None)
                .ConfigureAwait(true);
            if (live.Ok)
            {
                _database.WriteCursor(SyncWorker.LiveCatalogCursorStream, "ok", null);
                ReloadCatalogFromDatabase();
                CatalogRefreshHint =
                    $"تم التحديث الآن · {_products.Count.ToString(CultureInfo.InvariantCulture)} صنف";
                _syncWorker?.RequestImmediateTick(forceFullCatalog: false);
            }
            else
            {
                CatalogRefreshHint = $"تعذر التحديث: {live.Error ?? "خطأ غير معروف"}";
            }
        }
        catch (Exception ex)
        {
            PosLog.Error("manual catalog refresh failed", ex);
            CatalogRefreshHint = ArabicUi.FromException(ex, "تعذر تحديث الأصناف.");
        }
        finally
        {
            IsCatalogRefreshing = false;
            OnPropertyChanged(nameof(CanRefreshCatalog));
            RefreshCatalogCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void ToggleMoreActions()
    {
        ShowStatusInfo = false;
        ShowMoreActions = !ShowMoreActions;
    }

    [RelayCommand]
    private void CloseMoreActions() => ShowMoreActions = false;

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
            OnlineOrdersCount = OnlineOrders.Count;
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
    private async Task ConfirmCloseShift()
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
            var zReport = CurrentZReport ?? _database.BuildShiftZReport(_currentShift.Id);
            var printDoc = zReport?.ToPrintDocument(
                "ايجي شاينا ماركت",
                "埃及中国商场",
                closing);

            var closedLocal = _database.CloseShift(_currentShift.Id, _currentCashier.Id, closing);
            _currentShift = null;
            ShowCloseShift = false;
            CurrentZReport = null;
            NeedsOpenShift = true;
            SessionCaption = $"{_currentCashier.DisplayName} · لا توجد وردية مفتوحة";
            OnPropertyChanged(nameof(SessionChipText));
            OnPropertyChanged(nameof(HasZReport));
            RefreshCheckout();
            AnnounceHint("جاري رفع تقفيل الوردية للسيرفر…");
            RequestFocus(UiFocusTarget.OpeningFloat);
            DatabaseBackup.Create(_database);

            // Await ERP close so exiting the app right after Z-close does not leave the shift OPEN.
            var uploaded = await PushCloseShiftToErpAsync(closedLocal).ConfigureAwait(true);
            AnnounceHint(uploaded
                ? "أُغلقت الوردية ورُفعت للسيرفر."
                : "أُغلقت محلياً — التقفيل سيُرفع تلقائياً عند الاتصال.");

            if (printDoc is not null)
                await PrintZReportAsync(printDoc).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLog.Error("close shift failed", ex);
            ShiftError = ArabicUi.FromException(ex, "تعذر إغلاق الوردية.");
            AnnounceHint(ShiftError);
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
        ApplyConfiguredWeChatRate();
        TenderText = Money.FormatAmount(CashSuggestions.DefaultAmount(TotalMinor));
        IsTendering = true;
        RebuildQuickCashOptions();
        TenderOpened?.Invoke();
        RequestFocus(UiFocusTarget.Tender);
        _ = SyncWeChatRateFromErpAsync();
    }

    [RelayCommand]
    private void SelectPayment(PosPaymentMethod method)
    {
        if (!IsTendering)
            return;
        SelectedPayment = method;
        SaleError = string.Empty;
        if (IsWeChatPayment)
            ApplyConfiguredWeChatRate();
        if (IsCashPayment)
            TenderText = Money.FormatAmount(CashSuggestions.DefaultAmount(TotalMinor));
        else
            TenderText = Money.FormatAmount(TotalMinor);
        RebuildQuickCashOptions();
        RefreshCheckout();
        if (IsCashPayment)
            RequestFocus(UiFocusTarget.Tender);
        else if (IsWeChatPayment)
            RequestFocus(UiFocusTarget.Tender);
    }

    [RelayCommand]
    private async Task RefreshWeChatRateFromBankAsync()
    {
        if (!IsTendering || !IsWeChatPayment || IsFetchingWeChatRate)
            return;

        IsFetchingWeChatRate = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var safe = await WeChatLiveRate.FetchSafeEgpPerCnyAsync(http, CancellationToken.None)
                .ConfigureAwait(true);
            ExchangeRateText = WeChatFx.FormatRate(safe);
            AnnounceHint($"تم تحديث سعر وي شات من البنك (−4%): {ExchangeRateText}");
        }
        catch (Exception ex)
        {
            PosLog.Error("wechat live rate fetch failed", ex);
            AnnounceHint("تعذر جلب سعر الصرف — تحقق من الاتصال.");
        }
        finally
        {
            IsFetchingWeChatRate = false;
        }
    }

    private decimal ReadConfiguredWeChatRate()
    {
        var raw = _database.GetSetting(WeChatFx.ConfiguredRateSettingKey);
        return WeChatFx.TryParseRate(raw, out var rate) ? rate : WeChatFx.DefaultEgpPerCny;
    }

    private void ApplyConfiguredWeChatRate() =>
        ExchangeRateText = WeChatFx.FormatRate(ReadConfiguredWeChatRate());

    private async Task SyncWeChatRateFromErpAsync()
    {
        try
        {
            var origin = _syncOptions.BaseAddress ?? SyncOptions.DefaultErpOrigin;
            var url = new Uri(origin, "api/settings");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("wechat_egp_per_cny", out var node) ||
                node.ValueKind != System.Text.Json.JsonValueKind.Number)
            {
                return;
            }

            var rate = node.GetDecimal();
            if (rate <= 0)
                return;

            var text = WeChatFx.FormatRate(decimal.Round(rate, 4, MidpointRounding.AwayFromZero));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _database.WriteSetting(WeChatFx.ConfiguredRateSettingKey, text);
                // Don't clobber a cashier mid-edit during tender; next WeChat select reloads.
                if (!IsTendering)
                    ExchangeRateText = text;
            });
        }
        catch (Exception ex)
        {
            PosLog.Error("wechat rate sync from ERP failed", ex);
        }
    }

    [RelayCommand]
    private void SetExactTender()
    {
        if (!HasLines)
            return;
        TenderText = Money.FormatAmount(TotalMinor);
    }

    [RelayCommand]
    private void SelectQuickCash(long amountMinor)
    {
        if (!IsTendering || !IsCashPayment || amountMinor <= 0)
            return;
        SaleError = string.Empty;
        TenderText = Money.FormatAmount(amountMinor);
        RebuildQuickCashOptions();
    }

    private void RebuildQuickCashOptions()
    {
        QuickCashOptions.Clear();
        if (!IsTendering || !IsCashPayment || IsReturnMode || !HasLines)
        {
            OnPropertyChanged(nameof(ShowQuickCashOptions));
            return;
        }

        var suggested = CashSuggestions.Suggest(TotalMinor);
        var defaultAmount = CashSuggestions.DefaultAmount(TotalMinor);
        var hasParsed = Money.TryParseEgp(TenderText, out var selected);
        foreach (var amount in suggested)
        {
            var selectedNow = hasParsed ? amount == selected : amount == defaultAmount;
            QuickCashOptions.Add(new QuickCashOption(
                amount,
                Money.FormatAmount(amount),
                selectedNow));
        }

        OnPropertyChanged(nameof(ShowQuickCashOptions));
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
            .Select(line => line.ToCashSaleLine())
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
            LastPointsEarned = pointsEarned;
            _lastSaleWasReturn = wasReturn;
            TenderText = string.Empty;
            IsTendering = false;
            Lines.Clear();
            ClearHighlight();
            HasSettlement = true;
            _settlementAt = DateTimeOffset.Now;
            PendingOutbox = _database.CountPendingOutbox();
            _lastSaleId = sale.SaleId;
            _lastSaleTotalMinor = sale.TotalMinor;
            IsReturnMode = false;
            _activeQueueTicketCode = null;
            ClearLoyaltyState();
            ScanHint = wasReturn
                ? $"تم الاسترجاع — إيصال {sale.ReceiptNo.ToString(CultureInfo.InvariantCulture)} · {paymentLabel}"
                : $"تم حفظ الإيصال {sale.ReceiptNo.ToString(CultureInfo.InvariantCulture)} · {paymentLabel}";
            PosLog.Info(
                $"cashier confirmed {(wasReturn ? "return" : "sale")} receipt {sale.ReceiptNo} method={paymentErp}");
            SelectedPayment = PosPaymentMethod.Cash;
            OnPropertyChanged(nameof(CanReprint));
            OnPropertyChanged(nameof(HasLastSale));
            OnPropertyChanged(nameof(LastSaleButtonText));
            OnPropertyChanged(nameof(HasActiveQueueTicket));
            OnPropertyChanged(nameof(ActiveQueueCaption));
            ReloadRecentSales();
            RefreshCheckout();
            RequestFocus(UiFocusTarget.Barcode);
            if (!string.IsNullOrWhiteSpace(queueCode) && !wasReturn)
                _ = CompleteQueueTicketAsync(queueCode!, sale.SaleId);

            // Distinct lines (not qty): 1–4 → ask cashier; 5+ → print immediately.
            if (lines.Length > 0 && lines.Length < AutoPrintMinLineCount)
                OpenPrintPrompt(sale.SaleId, kickDrawer, sale.ReceiptNo, lines.Length, wasReturn);
            else
                _ = PrintSaleAsync(sale.SaleId, isReprint: false, kickDrawer: kickDrawer);
        }
        catch (Exception ex)
        {
            PosLog.Error("sale confirm failed", ex);
            SaleError = ArabicUi.FromException(ex, "تعذر حفظ البيع. السلة ما زالت هنا.");
        }
    }

    private void OpenPrintPrompt(string saleId, bool kickDrawer, int receiptNo, int lineCount, bool wasReturn)
    {
        _pendingPrintSaleId = saleId;
        _pendingPrintKickDrawer = kickDrawer;
        PrintPromptCaption = wasReturn
            ? $"الاسترجاع محفوظ (إيصال {receiptNo.ToString(CultureInfo.InvariantCulture)}) · {lineCount.ToString(CultureInfo.InvariantCulture)} أصناف. هل تريد طباعة الإيصال؟"
            : $"البيع محفوظ (إيصال {receiptNo.ToString(CultureInfo.InvariantCulture)}) · {lineCount.ToString(CultureInfo.InvariantCulture)} أصناف. هل تريد طباعة الإيصال؟";
        ShowPrintPrompt = true;
    }

    [RelayCommand]
    private void ConfirmPrintReceipt()
    {
        var saleId = _pendingPrintSaleId;
        var kickDrawer = _pendingPrintKickDrawer;
        ClearPrintPrompt();
        if (string.IsNullOrWhiteSpace(saleId))
            return;
        _ = PrintSaleAsync(saleId, isReprint: false, kickDrawer: kickDrawer);
        RequestFocus(UiFocusTarget.Barcode);
    }

    [RelayCommand]
    private void SkipPrintReceipt()
    {
        var kickDrawer = _pendingPrintKickDrawer;
        ClearPrintPrompt();
        if (kickDrawer)
            _ = KickDrawerOnlyAsync();
        else
            ScanHint = "تم الحفظ بدون طباعة. يمكن إعادة الطباعة من «آخر بيع».";
        RequestFocus(UiFocusTarget.Barcode);
    }

    private void ClearPrintPrompt()
    {
        ShowPrintPrompt = false;
        PrintPromptCaption = string.Empty;
        _pendingPrintSaleId = null;
        _pendingPrintKickDrawer = false;
    }

    private async Task KickDrawerOnlyAsync()
    {
        try
        {
            if (!_printer.IsConfigured)
            {
                await SetPrintHintAsync("تم الحفظ بدون طباعة. الطابعة غير مضبوطة — لم يُفتح الدرج.").ConfigureAwait(false);
                return;
            }

            await _printer.KickDrawerAsync(CancellationToken.None).ConfigureAwait(false);
            await SetPrintHintAsync("تم الحفظ بدون طباعة · فُتح درج النقدية.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLog.Error("kick drawer after skip-print failed", ex);
            await SetPrintHintAsync("تم الحفظ بدون طباعة. تعذر فتح الدرج.").ConfigureAwait(false);
        }
    }

    partial void OnSelectedPaymentChanged(PosPaymentMethod value)
    {
        OnPropertyChanged(nameof(PaymentMethodLabel));
        OnPropertyChanged(nameof(PaymentMethodLabelEn));
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
        OnPropertyChanged(nameof(CanRefreshWeChatRate));
    }

    partial void OnIsFetchingWeChatRateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRefreshWeChatRate));
        OnPropertyChanged(nameof(WeChatRateRefreshButtonText));
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
        ShowMoreActions = false;
        ShowStatusInfo = false;
        if (!CanReprint || string.IsNullOrWhiteSpace(_lastSaleId))
        {
            ScanHint = "لا يوجد إيصال سابق لإعادة الطباعة.";
            return;
        }

        _ = PrintSaleAsync(_lastSaleId, isReprint: true, kickDrawer: false);
    }

    [RelayCommand]
    private void ReprintRecentSale(RecentSaleSummary? sale)
    {
        if (sale is null || string.IsNullOrWhiteSpace(sale.SaleId) || NeedsLogin || IsTendering)
            return;
        _lastSaleId = sale.SaleId;
        _lastSaleTotalMinor = sale.TotalMinor;
        OnPropertyChanged(nameof(CanReprint));
        OnPropertyChanged(nameof(HasLastSale));
        OnPropertyChanged(nameof(LastSaleButtonText));
        _ = PrintSaleAsync(sale.SaleId, isReprint: true, kickDrawer: false);
    }

    [RelayCommand]
    private void ToggleStatusInfo()
    {
        ShowMoreActions = false;
        ShowStatusInfo = !ShowStatusInfo;
    }

    [RelayCommand]
    private void CloseStatusInfo() => ShowStatusInfo = false;

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

    private async Task PushOpenShiftToErpAsync(ShiftRecord localShift)
    {
        try
        {
            var token = !string.IsNullOrWhiteSpace(_syncOptions.DeviceToken)
                ? _syncOptions.DeviceToken
                : DeviceCredentialStore.Load();
            var origin = _syncOptions.BaseAddress ?? SyncOptions.DefaultErpOrigin;
            var ok = await ShiftErpSync.TryPushOpenNowAsync(
                    _database,
                    origin,
                    token,
                    localShift,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (ok)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_currentShift is not null &&
                        string.Equals(_currentShift.Id, localShift.Id, StringComparison.Ordinal))
                    {
                        RefreshShiftState();
                    }
                });
                _syncWorker?.RequestImmediateTick(forceFullCatalog: false);
            }
        }
        catch (Exception ex)
        {
            PosLog.Error("push open shift to erp failed", ex);
            _syncWorker?.RequestImmediateTick(forceFullCatalog: false);
        }
    }

    private async Task<bool> PushCloseShiftToErpAsync(ShiftRecord closedLocal)
    {
        try
        {
            var token = !string.IsNullOrWhiteSpace(_syncOptions.DeviceToken)
                ? _syncOptions.DeviceToken
                : DeviceCredentialStore.Load();
            var origin = _syncOptions.BaseAddress ?? SyncOptions.DefaultErpOrigin;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ok = await ShiftErpSync.TryPushCloseNowAsync(
                    _database,
                    origin,
                    token,
                    closedLocal,
                    cts.Token)
                .ConfigureAwait(false);
            _syncWorker?.RequestImmediateTick(forceFullCatalog: false);
            return ok;
        }
        catch (Exception ex)
        {
            PosLog.Error("push close shift to erp failed", ex);
            _syncWorker?.RequestImmediateTick(forceFullCatalog: false);
            return false;
        }
    }

    private async Task PrintZReportAsync(ZReportDocument document)
    {
        try
        {
            if (!_printer.IsConfigured)
            {
                await SetPrintHintAsync("أُغلقت الوردية. الطابعة غير مضبوطة — تقرير Z لم يُطبع.").ConfigureAwait(false);
                return;
            }

            await _printer.PrintZReportAsync(document, CancellationToken.None).ConfigureAwait(false);
            await SetPrintHintAsync("طُبع تقرير Z لإغلاق الوردية.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLog.Error("z-report print failed", ex);
            await SetPrintHintAsync("أُغلقت الوردية. تعذر طباعة تقرير Z.").ConfigureAwait(false);
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
        OnPropertyChanged(nameof(TotalAmountText));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(ShowCartLines));
        OnPropertyChanged(nameof(ShowEmptyCartHint));
        OnPropertyChanged(nameof(ShowLoyaltyPanelOutsideTender));
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
        OnPropertyChanged(nameof(LineCountTextZh));
        OnPropertyChanged(nameof(AmountLabel));
        OnPropertyChanged(nameof(AmountText));
        OnPropertyChanged(nameof(TenderHint));
        OnPropertyChanged(nameof(ShowCustomerChange));
        OnPropertyChanged(nameof(CustomerChangeText));
        OnPropertyChanged(nameof(CustomerChangeIsShortfall));
        OnPropertyChanged(nameof(ShowCustomerChangeDue));
        OnPropertyChanged(nameof(ShowSettlement));
        OnPropertyChanged(nameof(SettlementText));
        OnPropertyChanged(nameof(IdleCustomerText));
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanLock));
        OnPropertyChanged(nameof(CanOpenCloseShiftPanel));
        OnPropertyChanged(nameof(CanReprint));
        OnPropertyChanged(nameof(LastSaleButtonText));
        OnPropertyChanged(nameof(HasLastSale));
        OnPropertyChanged(nameof(PaymentMethodLabel));
        OnPropertyChanged(nameof(WeChatCnyText));
        OnPropertyChanged(nameof(ShowWeChatQr));
        OnPropertyChanged(nameof(ShowInstaPayQr));
        OnPropertyChanged(nameof(IsCashPayment));
        OnPropertyChanged(nameof(ShowQuickCashOptions));
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
        if (IsTendering)
            RebuildQuickCashOptions();
        RefreshCustomerDisplayMode(DateTimeOffset.Now);
    }

    private void RefreshCustomerDisplayMode(DateTimeOffset now)
    {
        var next = CustomerDisplayProjector.Resolve(
            HasLines,
            IsTendering,
            HasSettlement,
            _settlementAt,
            now,
            ShowPriceCheck);
        if (CustomerDisplayMode != next)
            CustomerDisplayMode = next;

        OnPropertyChanged(nameof(IsCfdIdle));
        OnPropertyChanged(nameof(IsCfdShopping));
        OnPropertyChanged(nameof(IsCfdPayment));
        OnPropertyChanged(nameof(IsCfdCompleted));
        OnPropertyChanged(nameof(IsCfdPriceCheck));
        OnPropertyChanged(nameof(IsCfdPriceCheckWaiting));
        OnPropertyChanged(nameof(IsCfdPriceCheckMiss));
        OnPropertyChanged(nameof(IdleCustomerText));
    }

    partial void OnCustomerDisplayModeChanged(CustomerDisplayMode value)
    {
        OnPropertyChanged(nameof(IsCfdIdle));
        OnPropertyChanged(nameof(IsCfdShopping));
        OnPropertyChanged(nameof(IsCfdPayment));
        OnPropertyChanged(nameof(IsCfdCompleted));
        OnPropertyChanged(nameof(IsCfdPriceCheck));
        OnPropertyChanged(nameof(IsCfdPriceCheckWaiting));
        OnPropertyChanged(nameof(IsCfdPriceCheckMiss));
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
            CatalogCaption = FormatCatalogCaption(products.Count, demo, ReadCatalogUpdatedAt());
            OnPropertyChanged(nameof(VisibleProducts));
            OnPropertyChanged(nameof(HasNoSearchResults));
            RefreshConnectivity();
            ReloadRecentSales();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private DateTimeOffset? ReadCatalogUpdatedAt()
    {
        DateTimeOffset? best = null;
        foreach (var stream in new[]
                 {
                     SyncWorker.LiveCatalogCursorStream,
                     PosDatabase.CatalogCursorStream,
                 })
        {
            var row = _database.ReadCursor(stream);
            if (row?.LastSuccessAt is null)
                continue;
            if (!DateTimeOffset.TryParse(row.LastSuccessAt, null, DateTimeStyles.RoundtripKind, out var at))
                continue;
            if (best is null || at > best)
                best = at;
        }

        return best;
    }

    private static string FormatCatalogCaption(int count, bool demoCatalog, DateTimeOffset? updatedAt)
    {
        var n = count.ToString(CultureInfo.InvariantCulture);
        if (demoCatalog)
            return $"كتالوج تجريبي ({n} أصناف) — حدّث الأصناف من «المزيد» بعد ربط ERP";

        if (updatedAt is null)
            return $"كتالوج ERP · {n} أصناف · بانتظار أول مزامنة";

        var local = updatedAt.Value.ToLocalTime();
        var age = DateTimeOffset.Now - updatedAt.Value;
        var ageText = age.TotalSeconds < 90
            ? "الآن"
            : age.TotalMinutes < 60
                ? $"منذ {(int)age.TotalMinutes} د"
                : local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return $"كتالوج ERP · {n} أصناف · آخر تحديث {ageText}";
    }

    private static bool Matches(CatalogProduct product, string term)
    {
        return product.NameAr.Contains(term, StringComparison.OrdinalIgnoreCase)
            || product.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (product.NameEn?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (product.NameZh?.Contains(term, StringComparison.Ordinal) ?? false)
            || (product.Barcode?.Contains(term, StringComparison.Ordinal) ?? false);
    }
}

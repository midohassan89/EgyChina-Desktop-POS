using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.App.ViewModels;

public partial class PosSession : ObservableObject
{
    private readonly PosDatabase _database;
    private IReadOnlyList<CatalogProduct> _products;
    private readonly SyncStatus _sync;

    public PosSession(PosDatabase database, IReadOnlyList<CatalogProduct> products, SyncStatus sync, bool demoCatalog)
    {
        _database = database;
        _products = products;
        _sync = sync;
        DatabasePath = database.DatabasePath;
        CatalogCaption = FormatCatalogCaption(products.Count, demoCatalog);
        ClockText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        NeedsLogin = true;
        SessionCaption = "سجّل الدخول برقم سري من ٦ أرقام";

        Lines.CollectionChanged += (_, _) => RefreshCheckout();
        _sync.Changed += HandleSyncChanged;
        var snap = _sync.Read();
        IsOnline = snap.IsOnline;
        SyncMessage = snap.Message;
        PendingOutbox = snap.PendingOutbox;
        DeadOutbox = snap.DeadOutbox;
        LastSyncError = snap.LastError ?? string.Empty;
        RefreshShiftState();
    }

    public ObservableCollection<CartLine> Lines { get; } = new();

    public string DatabasePath { get; }

    [ObservableProperty]
    private string _catalogCaption = string.Empty;

    public string ShellNotice { get; } = "نقد بالجنيه يُحفظ على هذا الجهاز. الطباعة ورفع البيع إلى المنظومة لم يُفعَّلا.";

    public Action? RequestCustomerDisplay { get; set; }

    public event Action? TenderOpened;

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
    private string _scanHint = "امسح الباركود أو اختر صنفاً.";

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

    public bool CanBeginPayment => CanOperate && HasLines && !IsTendering;

    public bool CanLock => !NeedsLogin && _currentCashier is not null;

    public bool CanOpenCloseShiftPanel => CanOperate && !IsTendering;

    public string LineCountText => Lines.Count == 0 ? "السلة فارغة" : $"{Lines.Count.ToString(CultureInfo.InvariantCulture)} سطور";

    public long TotalMinor => Lines.Sum(line => checked(line.UnitPriceMinor * line.Qty));

    public string TotalText => Money.FormatEgp(TotalMinor);

    public bool ShowSettlement => HasSettlement && IsCartEmpty;

    public string AmountLabel
    {
        get
        {
            if (IsTendering)
                return "المستحق";
            if (ShowSettlement)
                return "الباقي";
            return "الإجمالي";
        }
    }

    public string AmountText => ShowSettlement ? Money.FormatEgp(LastChangeMinor) : TotalText;

    public string SettlementText =>
        $"تم البيع — إيصال {LastReceiptNo.ToString(CultureInfo.InvariantCulture)}";

    public string IdleCustomerText => ShowSettlement ? "شكراً لكم" : "في انتظار المسح";

    public bool ShowCustomerChange => IsTendering && CanConfirmCash;

    public string CustomerChangeText => ShowCustomerChange
        ? "الباقي " + Money.FormatEgp(ParsedTender - TotalMinor)
        : string.Empty;

    public bool CanConfirmCash =>
        CanOperate
        && IsTendering
        && HasLines
        && Money.TryParseEgp(TenderText, out var tender)
        && tender >= TotalMinor;

    public string TenderHint
    {
        get
        {
            if (!string.IsNullOrEmpty(SaleError))
                return SaleError;
            if (!IsTendering)
                return string.Empty;
            if (!Money.TryParseEgp(TenderText, out var tender))
                return "اكتب المبلغ المستلم، مثل 200 أو 200.50";
            if (tender < TotalMinor)
                return "المبلغ أقل من المستحق";
            return "الباقي " + Money.FormatEgp(tender - TotalMinor);
        }
    }

    public IReadOnlyList<CatalogProduct> VisibleProducts
    {
        get
        {
            var term = Search.Trim();
            if (term.Length == 0)
                return _products;

            return _products.Where(product => Matches(product, term)).ToArray();
        }
    }

    public bool HasNoSearchResults => VisibleProducts.Count == 0;

    public void AddProduct(CatalogProduct product)
    {
        if (!CanOperate)
        {
            ScanHint = NeedsLogin ? "سجّل الدخول أولاً" : "افتح وردية قبل البيع";
            return;
        }

        HasSettlement = false;
        var existing = Lines.FirstOrDefault(line => line.ProductId == product.Id);
        if (existing is null)
            Lines.Add(new CartLine(product, RefreshCheckout, line => Lines.Remove(line)));
        else
            existing.Qty++;

        ScanHint = product.NameAr;
    }

    public void SubmitBarcode()
    {
        if (!CanOperate)
        {
            ScanHint = NeedsLogin ? "سجّل الدخول أولاً" : "افتح وردية قبل البيع";
            return;
        }

        var code = Barcode.Trim();
        if (code.Length == 0)
            return;

        var product = _database.FindByBarcode(code);
        if (product is null)
        {
            ScanHint = "الباركود غير موجود في النسخة المحلية";
            return;
        }

        AddProduct(product);
        Barcode = string.Empty;
    }

    public void SetClock(DateTime now)
    {
        ClockText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    partial void OnSearchChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleProducts));
        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    partial void OnIsOnlineChanged(bool value) => OnPropertyChanged(nameof(OnlineText));

    partial void OnPendingOutboxChanged(int value) => OnPropertyChanged(nameof(OutboxCaption));

    partial void OnDeadOutboxChanged(int value) => OnPropertyChanged(nameof(OutboxCaption));

    partial void OnLastSyncErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasSyncError));
        OnPropertyChanged(nameof(SyncErrorCaption));
    }

    partial void OnDisplayBannerChanged(string value) => OnPropertyChanged(nameof(HasDisplayBanner));

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
        Lines.Clear();
        IsTendering = false;
        HasSettlement = false;
        TenderText = string.Empty;
        SaleError = string.Empty;
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
    }

    [RelayCommand]
    private void LockSession()
    {
        if (_currentCashier is null)
            return;

        Lines.Clear();
        IsTendering = false;
        HasSettlement = false;
        TenderText = string.Empty;
        SaleError = string.Empty;
        ShowCloseShift = false;
        _currentCashier = null;
        CashierName = string.Empty;
        NeedsLogin = true;
        SessionCaption = _currentShift is null
            ? "الجهاز مقفل. أدخل الرقم السري."
            : "الجهاز مقفل. الوردية ما زالت مفتوحة — أدخل الرقم السري للمتابعة.";
        RefreshCheckout();
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
        }
        catch (Exception ex)
        {
            PosLog.Error("open shift failed", ex);
            ShiftError = ex.Message;
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

        ClosingCashText = Money.FormatAmount(_currentShift.OpeningFloatMinor);
        ShiftError = string.Empty;
        ShowCloseShift = true;
    }

    [RelayCommand]
    private void CancelCloseShift()
    {
        ShowCloseShift = false;
        ShiftError = string.Empty;
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
            NeedsOpenShift = true;
            SessionCaption = $"{_currentCashier.DisplayName} · لا توجد وردية مفتوحة";
            RefreshCheckout();
        }
        catch (Exception ex)
        {
            PosLog.Error("close shift failed", ex);
            ShiftError = ex.Message;
        }
    }

    [RelayCommand]
    private void BeginCashPayment()
    {
        if (!CanBeginPayment)
            return;

        HasSettlement = false;
        SaleError = string.Empty;
        TenderText = Money.FormatAmount(TotalMinor);
        IsTendering = true;
        TenderOpened?.Invoke();
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
    }

    [RelayCommand]
    private void ConfirmCash()
    {
        if (!CanConfirmCash || !Money.TryParseEgp(TenderText, out var tender))
            return;

        var lines = Lines
            .Select(line => new CashSaleLine(line.ProductId, line.Barcode, line.NameAr, line.Qty, line.UnitPriceMinor))
            .ToArray();

        if (_currentCashier is null || _currentShift is null)
        {
            SaleError = "سجّل الدخول وافتح وردية قبل البيع.";
            return;
        }

        try
        {
            var sale = _database.CompleteCashSale(lines, tender, _currentCashier.Id, _currentShift.Id);
            LastReceiptNo = sale.ReceiptNo;
            LastChangeMinor = sale.ChangeMinor;
            TenderText = string.Empty;
            IsTendering = false;
            HasSettlement = true;
            Lines.Clear();
            PendingOutbox = _database.CountPendingOutbox();
            ScanHint = $"تم حفظ الإيصال {sale.ReceiptNo.ToString(CultureInfo.InvariantCulture)}";
            PosLog.Info($"cashier confirmed receipt {sale.ReceiptNo}");
        }
        catch (Exception ex)
        {
            PosLog.Error("cash sale failed", ex);
            SaleError = string.IsNullOrWhiteSpace(ex.Message) ? "تعذر حفظ البيع. السلة ما زالت هنا." : ex.Message;
        }
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
        OnPropertyChanged(nameof(TotalMinor));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(CanBeginPayment));
        OnPropertyChanged(nameof(CanConfirmCash));
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

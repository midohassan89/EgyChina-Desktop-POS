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
    private readonly IReadOnlyList<CatalogProduct> _products;
    private readonly SyncStatus _sync;

    public PosSession(PosDatabase database, IReadOnlyList<CatalogProduct> products, SyncStatus sync, bool demoCatalog)
    {
        _database = database;
        _products = products;
        _sync = sync;
        DatabasePath = database.DatabasePath;
        CatalogCaption = demoCatalog
            ? $"{products.Count} أصناف تجريبية في القاعدة المحلية"
            : $"{products.Count} أصناف في القاعدة المحلية";
        ClockText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        Lines.CollectionChanged += (_, _) => RefreshCheckout();
        _sync.Changed += HandleSyncChanged;
        var snap = _sync.Read();
        IsOnline = snap.IsOnline;
        SyncMessage = snap.Message;
        PendingOutbox = snap.PendingOutbox;
    }

    public ObservableCollection<CartLine> Lines { get; } = new();

    public string DatabasePath { get; }

    public string CatalogCaption { get; }

    public string ShellNotice { get; } = "نقد بالجنيه يُحفظ على هذا الجهاز. الطباعة ورفع البيع إلى المنظومة لم يُفعَّلا.";

    public Action? RequestCustomerDisplay { get; set; }

    public event Action? TenderOpened;

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

    public string OutboxCaption => $"صندوق الصادر: {PendingOutbox.ToString(CultureInfo.InvariantCulture)}";

    public bool HasDisplayBanner => !string.IsNullOrWhiteSpace(DisplayBanner);

    public bool IsCartEmpty => Lines.Count == 0;

    public bool HasLines => Lines.Count > 0;

    public bool CanBeginPayment => HasLines && !IsTendering;

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
        IsTendering
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
    private void BeginCashPayment()
    {
        if (!HasLines || IsTendering)
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

        try
        {
            var sale = _database.CompleteCashSale(lines, tender);
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
            SaleError = "تعذر حفظ البيع. السلة ما زالت هنا.";
        }
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
    }

    private void HandleSyncChanged()
    {
        var snap = _sync.Read();
        void Apply()
        {
            IsOnline = snap.IsOnline;
            SyncMessage = snap.Message;
            PendingOutbox = snap.PendingOutbox;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
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

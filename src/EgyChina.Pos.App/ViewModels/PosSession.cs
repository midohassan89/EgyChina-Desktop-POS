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

        Lines.CollectionChanged += (_, _) => NotifyTotals();
        _sync.Changed += HandleSyncChanged;
        var snap = _sync.Read();
        IsOnline = snap.IsOnline;
        SyncMessage = snap.Message;
        PendingOutbox = snap.PendingOutbox;
    }

    public ObservableCollection<CartLine> Lines { get; } = new();

    public string DatabasePath { get; }

    public string CatalogCaption { get; }

    public string ShellNotice { get; } = "هيكل التشغيل فقط — الدفع والطباعة وربط المنظومة يأتون في مرحلة البيع.";

    public Action? RequestCustomerDisplay { get; set; }

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

    public string OnlineText => IsOnline ? "متصل" : "غير متصل";

    public string OutboxCaption => $"صندوق الصادر: {PendingOutbox.ToString(CultureInfo.InvariantCulture)}";

    public bool HasDisplayBanner => !string.IsNullOrWhiteSpace(DisplayBanner);

    public bool IsCartEmpty => Lines.Count == 0;

    public bool HasLines => Lines.Count > 0;

    public string LineCountText => Lines.Count == 0 ? "السلة فارغة" : $"{Lines.Count.ToString(CultureInfo.InvariantCulture)} سطور";

    public long TotalMinor => Lines.Sum(line => line.UnitPriceMinor * line.Qty);

    public string TotalText => Money.FormatEgp(TotalMinor);

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
        var existing = Lines.FirstOrDefault(line => line.ProductId == product.Id);
        if (existing is null)
            Lines.Add(new CartLine(product, NotifyTotals, line => Lines.Remove(line)));
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

    [RelayCommand]
    private void ClearCart() => Lines.Clear();

    [RelayCommand]
    private void ShowCustomerDisplay() => RequestCustomerDisplay?.Invoke();

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(TotalMinor));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(LineCountText));
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

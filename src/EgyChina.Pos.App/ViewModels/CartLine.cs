using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.App.ViewModels;

public partial class CartLine : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<CartLine> _remove;
    private readonly Action<CartLine>? _touched;

    public CartLine(
        CatalogProduct product,
        Action changed,
        Action<CartLine> remove,
        Action<CartLine>? touched = null)
        : this(
            product.Id,
            product.Barcode,
            product.NameAr,
            product.NameZh,
            product.PriceEgpMinor,
            qty: 1,
            changed,
            remove,
            touched)
    {
    }

    public CartLine(
        HeldCartLinePayload payload,
        Action changed,
        Action<CartLine> remove,
        Action<CartLine>? touched = null)
        : this(
            payload.ProductId,
            payload.Barcode,
            payload.NameAr,
            payload.NameZh,
            payload.UnitPriceMinor,
            Math.Max(1, payload.Qty),
            changed,
            remove,
            touched)
    {
    }

    public CartLine(
        string productId,
        string? barcode,
        string nameAr,
        string? nameZh,
        long unitPriceMinor,
        int qty,
        Action changed,
        Action<CartLine> remove,
        Action<CartLine>? touched = null)
    {
        LineId = Guid.NewGuid().ToString("N");
        ProductId = productId;
        Barcode = barcode;
        NameAr = nameAr;
        NameZh = nameZh;
        UnitPriceMinor = unitPriceMinor;
        _changed = changed;
        _remove = remove;
        _touched = touched;
        _qty = qty;
    }

    public string LineId { get; }

    public string ProductId { get; }

    public string? Barcode { get; }

    public string NameAr { get; }

    public string? NameZh { get; }

    public string DisplayNameAr => ProductDisplayNames.ForShelf(NameAr, NameZh, Barcode);

    public bool HasNameZh => !string.IsNullOrWhiteSpace(NameZh);

    public bool ShowCustomerNameZh => ProductDisplayNames.ShowSecondaryZh(NameAr, NameZh);

    public long UnitPriceMinor { get; }

    public string UnitPriceText => Money.FormatEgp(UnitPriceMinor);

    public string LineTotalText => Money.FormatEgp(UnitPriceMinor * Qty);

    public string QtyText => Qty.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string QtyCaption => "× " + QtyText;

    public HeldCartLinePayload ToHeldPayload() =>
        new(ProductId, Barcode, NameAr, NameZh, Qty, UnitPriceMinor);

    [ObservableProperty]
    private int _qty;

    [ObservableProperty]
    private bool _isHighlighted;

    partial void OnQtyChanged(int value)
    {
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(QtyText));
        OnPropertyChanged(nameof(QtyCaption));
        _changed();
        _touched?.Invoke(this);
    }

    [RelayCommand]
    private void Increment()
    {
        if (Qty >= 999)
            return;
        Qty++;
    }

    [RelayCommand]
    private void Decrement()
    {
        if (Qty <= 1)
            return;
        Qty--;
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}

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
    {
        LineId = Guid.NewGuid().ToString("N");
        ProductId = product.Id;
        Barcode = product.Barcode;
        NameAr = product.NameAr;
        NameZh = product.NameZh;
        UnitPriceMinor = product.PriceEgpMinor;
        _changed = changed;
        _remove = remove;
        _touched = touched;
        _qty = 1;
    }

    public string LineId { get; }

    public string ProductId { get; }

    public string? Barcode { get; }

    public string NameAr { get; }

    public string? NameZh { get; }

    public bool HasNameZh => !string.IsNullOrWhiteSpace(NameZh);

    public long UnitPriceMinor { get; }

    public string UnitPriceText => Money.FormatEgp(UnitPriceMinor);

    public string LineTotalText => Money.FormatEgp(UnitPriceMinor * Qty);

    public string QtyText => $"× {Qty}";

    [ObservableProperty]
    private int _qty;

    [ObservableProperty]
    private bool _isHighlighted;

    partial void OnQtyChanged(int value)
    {
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(QtyText));
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
            _remove(this);
        else
            Qty--;
    }
}

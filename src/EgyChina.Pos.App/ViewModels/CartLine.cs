using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.App.ViewModels;

public partial class CartLine : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<CartLine> _remove;

    public CartLine(CatalogProduct product, Action changed, Action<CartLine> remove)
    {
        ProductId = product.Id;
        NameAr = product.NameAr;
        NameZh = product.NameZh;
        UnitPriceMinor = product.PriceEgpMinor;
        _changed = changed;
        _remove = remove;
        Qty = 1;
    }

    public string ProductId { get; }

    public string NameAr { get; }

    public string? NameZh { get; }

    public bool HasNameZh => !string.IsNullOrWhiteSpace(NameZh);

    public long UnitPriceMinor { get; }

    public string UnitPriceText => Money.FormatEgp(UnitPriceMinor);

    public string LineTotalText => Money.FormatEgp(UnitPriceMinor * Qty);

    public string QtyText => $"× {Qty}";

    [ObservableProperty]
    private int _qty;

    partial void OnQtyChanged(int value)
    {
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(QtyText));
        _changed();
    }

    [RelayCommand]
    private void Increment() => Qty++;

    [RelayCommand]
    private void Decrement()
    {
        if (Qty <= 1)
            _remove(this);
        else
            Qty--;
    }
}

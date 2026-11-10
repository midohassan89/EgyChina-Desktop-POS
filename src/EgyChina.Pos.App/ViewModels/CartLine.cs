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
            isScalePriced: false,
            LineDiscountKind.None,
            discountValue: 0,
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
            payload.IsScalePriced,
            CartLinePricing.ParseStoredKind(payload.DiscountKind),
            payload.DiscountValue,
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
        : this(
            productId,
            barcode,
            nameAr,
            nameZh,
            unitPriceMinor,
            qty,
            isScalePriced: false,
            LineDiscountKind.None,
            discountValue: 0,
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
        bool isScalePriced,
        LineDiscountKind discountKind,
        long discountValue,
        Action changed,
        Action<CartLine> remove,
        Action<CartLine>? touched = null)
    {
        LineId = Guid.NewGuid().ToString("N");
        ProductId = productId;
        Barcode = barcode;
        NameAr = nameAr;
        NameZh = nameZh;
        IsScalePriced = isScalePriced;
        _unitPriceMinor = Math.Max(0, unitPriceMinor);
        _discountKind = discountKind;
        _discountValue = Math.Max(0, discountValue);
        _changed = changed;
        _remove = remove;
        _touched = touched;
        _qty = Math.Max(1, qty);
    }

    public string LineId { get; }

    public string ProductId { get; }

    public string? Barcode { get; }

    public string NameAr { get; }

    public string? NameZh { get; }

    public bool IsScalePriced { get; }

    public string DisplayNameAr => ProductDisplayNames.ForShelf(NameAr, NameZh, Barcode);

    public bool HasNameZh => !string.IsNullOrWhiteSpace(NameZh);

    public bool ShowCustomerNameZh => ProductDisplayNames.ShowSecondaryZh(NameAr, NameZh);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPriceText))]
    [NotifyPropertyChangedFor(nameof(LineTotalMinor))]
    [NotifyPropertyChangedFor(nameof(LineTotalText))]
    [NotifyPropertyChangedFor(nameof(DiscountMinor))]
    [NotifyPropertyChangedFor(nameof(HasDiscount))]
    [NotifyPropertyChangedFor(nameof(DiscountCaption))]
    private long _unitPriceMinor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotalMinor))]
    [NotifyPropertyChangedFor(nameof(LineTotalText))]
    [NotifyPropertyChangedFor(nameof(DiscountMinor))]
    [NotifyPropertyChangedFor(nameof(HasDiscount))]
    [NotifyPropertyChangedFor(nameof(DiscountCaption))]
    private LineDiscountKind _discountKind;

    /// <summary>
    /// Percent: whole percent 0–100. Flat: EGP piasters.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotalMinor))]
    [NotifyPropertyChangedFor(nameof(LineTotalText))]
    [NotifyPropertyChangedFor(nameof(DiscountMinor))]
    [NotifyPropertyChangedFor(nameof(HasDiscount))]
    [NotifyPropertyChangedFor(nameof(DiscountCaption))]
    private long _discountValue;

    public string UnitPriceText => Money.FormatEgp(UnitPriceMinor);

    public long DiscountMinor =>
        CartLinePricing.ComputeDiscountMinor(Qty, UnitPriceMinor, DiscountKind, DiscountValue);

    public long LineTotalMinor =>
        CartLinePricing.ComputeLineTotalMinor(Qty, UnitPriceMinor, DiscountKind, DiscountValue);

    public string LineTotalText => Money.FormatEgp(LineTotalMinor);

    public bool HasDiscount => DiscountMinor > 0;

    public string DiscountCaption
    {
        get
        {
            if (!HasDiscount)
                return string.Empty;
            return DiscountKind switch
            {
                LineDiscountKind.Percent =>
                    $"خصم {DiscountValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}%",
                LineDiscountKind.Flat =>
                    "خصم " + Money.FormatEgp(DiscountValue),
                _ => string.Empty,
            };
        }
    }

    public string QtyText => Qty.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string QtyCaption => "× " + QtyText;

    public HeldCartLinePayload ToHeldPayload() =>
        new(
            ProductId,
            Barcode,
            NameAr,
            NameZh,
            Qty,
            UnitPriceMinor,
            CartLinePricing.ToStoredKind(DiscountKind),
            DiscountValue,
            IsScalePriced);

    public CashSaleLine ToCashSaleLine() =>
        new(ProductId, Barcode, NameAr, Qty, UnitPriceMinor, DiscountMinor);

    public void ApplyEdit(int qty, long unitPriceMinor, LineDiscountKind kind, long discountValue)
    {
        if (IsScalePriced)
            qty = Qty;
        else
            qty = Math.Clamp(qty, 1, 999);

        UnitPriceMinor = Math.Max(0, unitPriceMinor);
        DiscountKind = kind == LineDiscountKind.None || discountValue <= 0
            ? LineDiscountKind.None
            : kind;
        DiscountValue = DiscountKind == LineDiscountKind.None
            ? 0
            : Math.Max(0, discountValue);
        if (Qty != qty)
            Qty = qty;
        else
            NotifyPricingChanged();
    }

    [ObservableProperty]
    private int _qty;

    [ObservableProperty]
    private bool _isHighlighted;

    partial void OnQtyChanged(int value)
    {
        OnPropertyChanged(nameof(LineTotalMinor));
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(DiscountMinor));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(DiscountCaption));
        OnPropertyChanged(nameof(QtyText));
        OnPropertyChanged(nameof(QtyCaption));
        _changed();
        _touched?.Invoke(this);
    }

    partial void OnUnitPriceMinorChanged(long value) => NotifyPricingSideEffects();

    partial void OnDiscountKindChanged(LineDiscountKind value) => NotifyPricingSideEffects();

    partial void OnDiscountValueChanged(long value) => NotifyPricingSideEffects();

    private void NotifyPricingChanged()
    {
        OnPropertyChanged(nameof(UnitPriceText));
        OnPropertyChanged(nameof(LineTotalMinor));
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(DiscountMinor));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(DiscountCaption));
        _changed();
        _touched?.Invoke(this);
    }

    private void NotifyPricingSideEffects()
    {
        _changed();
        _touched?.Invoke(this);
    }

    [RelayCommand]
    private void Increment()
    {
        if (IsScalePriced || Qty >= 999)
            return;
        Qty++;
    }

    [RelayCommand]
    private void Decrement()
    {
        if (IsScalePriced || Qty <= 1)
            return;
        Qty--;
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}

namespace EgyChina.Pos.Core;

/// <summary>Per-line cashier discount applied after qty × unit price.</summary>
public enum LineDiscountKind
{
    None = 0,
    Percent = 1,
    Flat = 2,
}

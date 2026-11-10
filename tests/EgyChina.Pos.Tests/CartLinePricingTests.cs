using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public class CartLinePricingTests
{
    [Fact]
    public void No_discount_is_qty_times_unit()
    {
        Assert.Equal(2800, CartLinePricing.ComputeLineTotalMinor(2, 1400, LineDiscountKind.None, 0));
        Assert.Equal(0, CartLinePricing.ComputeDiscountMinor(2, 1400, LineDiscountKind.None, 99));
    }

    [Fact]
    public void Percent_discount_rounds_half_up()
    {
        // 14.00 × 2 = 28.00, 10% → 2.80
        Assert.Equal(280, CartLinePricing.ComputeDiscountMinor(2, 1400, LineDiscountKind.Percent, 10));
        Assert.Equal(2520, CartLinePricing.ComputeLineTotalMinor(2, 1400, LineDiscountKind.Percent, 10));
    }

    [Fact]
    public void Flat_discount_caps_at_gross()
    {
        Assert.Equal(500, CartLinePricing.ComputeDiscountMinor(1, 1400, LineDiscountKind.Flat, 500));
        Assert.Equal(900, CartLinePricing.ComputeLineTotalMinor(1, 1400, LineDiscountKind.Flat, 500));
        Assert.Equal(1400, CartLinePricing.ComputeDiscountMinor(1, 1400, LineDiscountKind.Flat, 9999));
        Assert.Equal(0, CartLinePricing.ComputeLineTotalMinor(1, 1400, LineDiscountKind.Flat, 9999));
    }

    [Fact]
    public void CashSaleLine_embeds_discount_in_line_total()
    {
        var line = new CashSaleLine("p1", "123", "زبادي", 2, 1400, DiscountMinor: 280);
        Assert.Equal(2520, line.LineTotalMinor);
    }
}

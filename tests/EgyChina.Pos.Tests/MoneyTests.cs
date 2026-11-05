using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData(0, "\u20660.00\u2069\u00A0\u2067ج.م\u2069")]
    [InlineData(100, "\u20661.00\u2069\u00A0\u2067ج.م\u2069")]
    [InlineData(6250, "\u206662.50\u2069\u00A0\u2067ج.م\u2069")]
    [InlineData(8500, "\u206685.00\u2069\u00A0\u2067ج.م\u2069")]
    [InlineData(-50, "\u2066-0.50\u2069\u00A0\u2067ج.م\u2069")]
    public void FormatEgp_uses_minor_units(long minor, string expected)
    {
        Assert.Equal(expected, Money.FormatEgp(minor));
    }

    [Fact]
    public void ApplyRate_rounds_half_up_away_from_zero()
    {
        Assert.Equal(711, Money.ApplyRate(100, 7107, 1000));
        Assert.Equal(-711, Money.ApplyRate(-100, 7107, 1000));
        Assert.Equal(1, Money.ApplyRate(1, 1, 2));
        Assert.Equal(0, Money.ApplyRate(1, 1, 3));
        Assert.Equal(150, Money.ApplyRate(100, 3, 2));
    }

    [Fact]
    public void ApplyRate_rejects_a_non_positive_denominator()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.ApplyRate(100, 1, 0));
    }
}

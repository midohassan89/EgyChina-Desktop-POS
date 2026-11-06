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

    [Theory]
    [InlineData("0", 0)]
    [InlineData("85", 8500)]
    [InlineData("85.5", 8550)]
    [InlineData("85.50", 8550)]
    [InlineData("  200.00 ", 20000)]
    [InlineData("0.05", 5)]
    public void TryParseEgp_reads_pounds_and_piasters(string text, long expected)
    {
        Assert.True(Money.TryParseEgp(text, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12.345")]
    [InlineData("12.3.4")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1,50")]
    public void TryParseEgp_rejects_bad_input(string text)
    {
        Assert.False(Money.TryParseEgp(text, out _));
    }

    [Fact]
    public void ApplyRate_rejects_a_non_positive_denominator()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.ApplyRate(100, 1, 0));
    }
}

using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class CashSuggestionsTests
{
    [Fact]
    public void Suggest_606_matches_store_cash_ladder()
    {
        var options = CashSuggestions.Suggest(60_600);
        Assert.Equal(new long[] { 60_500, 61_000, 65_000, 70_000, 80_000 }, options);
        Assert.Equal(61_000, CashSuggestions.DefaultAmount(60_600));
    }

    [Fact]
    public void Suggest_exact_600_collapses_to_one()
    {
        var options = CashSuggestions.Suggest(60_000);
        Assert.Equal(new long[] { 60_000 }, options);
        Assert.Equal(60_000, CashSuggestions.DefaultAmount(60_000));
    }

    [Fact]
    public void Suggest_601_includes_underpay_and_ceil_ladder()
    {
        var options = CashSuggestions.Suggest(60_100);
        Assert.Equal(new long[] { 60_000, 61_000, 65_000, 70_000, 80_000 }, options);
        Assert.Equal(61_000, CashSuggestions.DefaultAmount(60_100));
    }

    [Fact]
    public void Suggest_with_piasters_ceils_pound_first()
    {
        var options = CashSuggestions.Suggest(60_650); // 606.50 -> base 607
        Assert.Contains(60_500L, options); // floor-5 of original
        Assert.Contains(61_000L, options); // ceil-10 of 607
        Assert.Equal(61_000, CashSuggestions.DefaultAmount(60_650));
    }
}

using EgyChina.Pos.Core;
using Xunit;

namespace EgyChina.Pos.Tests;

public sealed class CustomerDisplayProjectorTests
{
    [Fact]
    public void Resolve_prefers_payment_when_tendering_with_lines()
    {
        var mode = CustomerDisplayProjector.Resolve(
            hasLines: true,
            isTendering: true,
            showCompleted: false,
            completedAt: null,
            now: DateTimeOffset.Parse("2026-11-07T12:00:00Z"));

        Assert.Equal(CustomerDisplayMode.Payment, mode);
    }

    [Fact]
    public void Resolve_shopping_when_cart_has_lines()
    {
        var mode = CustomerDisplayProjector.Resolve(
            hasLines: true,
            isTendering: false,
            showCompleted: true,
            completedAt: DateTimeOffset.Parse("2026-11-07T12:00:00Z"),
            now: DateTimeOffset.Parse("2026-11-07T12:00:05Z"));

        Assert.Equal(CustomerDisplayMode.Shopping, mode);
    }

    [Fact]
    public void Resolve_completed_within_hold_window()
    {
        var completedAt = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        var mode = CustomerDisplayProjector.Resolve(
            hasLines: false,
            isTendering: false,
            showCompleted: true,
            completedAt: completedAt,
            now: completedAt.AddSeconds(19));

        Assert.Equal(CustomerDisplayMode.Completed, mode);
        Assert.False(CustomerDisplayProjector.IsCompletedExpired(completedAt, completedAt.AddSeconds(19)));
    }

    [Fact]
    public void Resolve_idle_after_completed_hold_expires()
    {
        var completedAt = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        var later = completedAt + CustomerDisplayTiming.CompletedHold;

        Assert.True(CustomerDisplayProjector.IsCompletedExpired(completedAt, later));
        var mode = CustomerDisplayProjector.Resolve(
            hasLines: false,
            isTendering: false,
            showCompleted: true,
            completedAt: completedAt,
            now: later);

        Assert.Equal(CustomerDisplayMode.Idle, mode);
    }

    [Fact]
    public void IdleGreeting_rotates_across_languages()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(0);
        var t1 = DateTimeOffset.FromUnixTimeSeconds(4);
        var t2 = DateTimeOffset.FromUnixTimeSeconds(8);

        Assert.Contains("ايجي", CustomerDisplayProjector.IdleGreeting(t0), StringComparison.Ordinal);
        Assert.Contains("Welcome", CustomerDisplayProjector.IdleGreeting(t1), StringComparison.Ordinal);
        Assert.Contains("欢迎", CustomerDisplayProjector.IdleGreeting(t2), StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_hold_matches_web_twenty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), CustomerDisplayTiming.CompletedHold);
    }
}

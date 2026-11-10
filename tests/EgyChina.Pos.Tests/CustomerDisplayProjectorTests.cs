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
    public void IdleGreeting_rotates_arabic_and_chinese_only()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(0);
        var t1 = DateTimeOffset.FromUnixTimeSeconds(4);

        Assert.Contains("ايجي", CustomerDisplayProjector.IdleGreeting(t0), StringComparison.Ordinal);
        Assert.Contains("欢迎", CustomerDisplayProjector.IdleGreeting(t1), StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome", CustomerDisplayProjector.IdleGreeting(t0), StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome", CustomerDisplayProjector.IdleGreeting(t1), StringComparison.Ordinal);
    }

    [Fact]
    public void Idle_clock_script_and_date_rotate_with_greeting()
    {
        var arabicAt = new DateTimeOffset(2026, 11, 7, 16, 30, 0, TimeSpan.FromHours(2));
        // Force buckets: unix/4 % 2 → Arabic ↔ Chinese
        var t0 = DateTimeOffset.FromUnixTimeSeconds(0);
        var t1 = DateTimeOffset.FromUnixTimeSeconds(4);

        Assert.Equal(CustomerClockScript.Arabic, CustomerDisplayProjector.IdleClockScript(t0));
        Assert.Equal(CustomerClockScript.Chinese, CustomerDisplayProjector.IdleClockScript(t1));

        Assert.Equal("٤", CustomerAnalogClock.HourLabel(4, CustomerClockScript.Arabic));
        Assert.Equal("4", CustomerAnalogClock.HourLabel(4, CustomerClockScript.Latin));
        Assert.Equal("四", CustomerAnalogClock.HourLabel(4, CustomerClockScript.Chinese));
        Assert.Equal("١٢", CustomerAnalogClock.HourLabel(12, CustomerClockScript.Arabic));
        Assert.Equal("十二", CustomerAnalogClock.HourLabel(12, CustomerClockScript.Chinese));

        var arDate = CustomerAnalogClock.FormatDate(arabicAt, CustomerClockScript.Arabic);
        Assert.Contains("السبت", arDate, StringComparison.Ordinal);
        Assert.Contains("نوفمبر", arDate, StringComparison.Ordinal);
        Assert.Contains("٢٠٢٦", arDate, StringComparison.Ordinal);

        var zhDate = CustomerAnalogClock.FormatDate(arabicAt, CustomerClockScript.Chinese);
        Assert.Contains("二零二六", zhDate, StringComparison.Ordinal);
        Assert.Contains("十一月", zhDate, StringComparison.Ordinal);
        Assert.Contains("星期六", zhDate, StringComparison.Ordinal);

        var (hour, minute, second) = CustomerAnalogClock.HandAngles(new DateTime(2026, 11, 7, 16, 30, 0));
        Assert.Equal(135.0, hour, 3); // 4:30 → halfway to 5
        Assert.Equal(180.0, minute, 3);
        Assert.Equal(0.0, second, 3);
    }

    [Fact]
    public void Completed_hold_matches_web_twenty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), CustomerDisplayTiming.CompletedHold);
    }
}

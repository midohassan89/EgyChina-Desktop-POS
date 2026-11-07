namespace EgyChina.Pos.Core;

/// <summary>
/// Customer-facing display modes aligned with the web CFD
/// (<c>IDLE</c> / <c>SHOPPING</c> / <c>PAYMENT</c> / <c>COMPLETED</c>).
/// </summary>
public enum CustomerDisplayMode
{
    Idle = 0,
    Shopping = 1,
    Payment = 2,
    Completed = 3,
}

public static class CustomerDisplayTiming
{
    /// <summary>Matches web <c>CFD_COMPLETED_MS</c>.</summary>
    public static readonly TimeSpan CompletedHold = TimeSpan.FromSeconds(20);

    /// <summary>Matches web idle language rotation interval.</summary>
    public static readonly TimeSpan IdleGreetingRotate = TimeSpan.FromSeconds(4);
}

/// <summary>
/// Pure projector so CFD mode rules stay testable without Avalonia.
/// </summary>
public static class CustomerDisplayProjector
{
    public static CustomerDisplayMode Resolve(
        bool hasLines,
        bool isTendering,
        bool showCompleted,
        DateTimeOffset? completedAt,
        DateTimeOffset now)
    {
        if (hasLines && isTendering)
            return CustomerDisplayMode.Payment;

        if (hasLines)
            return CustomerDisplayMode.Shopping;

        if (showCompleted
            && completedAt is { } at
            && now - at < CustomerDisplayTiming.CompletedHold)
        {
            return CustomerDisplayMode.Completed;
        }

        return CustomerDisplayMode.Idle;
    }

    public static string IdleGreeting(DateTimeOffset now)
    {
        var index = (int)(now.ToUnixTimeSeconds() / (long)CustomerDisplayTiming.IdleGreetingRotate.TotalSeconds % 3);
        return index switch
        {
            0 => "أهلاً بكم في ايجي شاينا",
            1 => "Welcome to EgyChina",
            _ => "欢迎光临埃及中国超市",
        };
    }

    public static bool IsCompletedExpired(DateTimeOffset? completedAt, DateTimeOffset now) =>
        completedAt is { } at && now - at >= CustomerDisplayTiming.CompletedHold;
}

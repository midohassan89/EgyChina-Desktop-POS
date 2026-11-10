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
        return IdleClockScript(now) switch
        {
            CustomerClockScript.Arabic => "أهلاً بكم في ايجي شاينا",
            _ => "欢迎光临埃及中国超市",
        };
    }

    /// <summary>Same 4s rotation as the idle greeting: Arabic ↔ Chinese only (no English).</summary>
    public static CustomerClockScript IdleClockScript(DateTimeOffset now)
    {
        var index = (int)(now.ToUnixTimeSeconds() / (long)CustomerDisplayTiming.IdleGreetingRotate.TotalSeconds % 2);
        return index == 0 ? CustomerClockScript.Arabic : CustomerClockScript.Chinese;
    }

    public static string IdleDateText(DateTimeOffset now) =>
        CustomerAnalogClock.FormatDate(now, IdleClockScript(now));

    public static bool IsCompletedExpired(DateTimeOffset? completedAt, DateTimeOffset now) =>
        completedAt is { } at && now - at >= CustomerDisplayTiming.CompletedHold;
}

/// <summary>Numeral script for the CFD idle analog clock face.</summary>
public enum CustomerClockScript
{
    Arabic = 0,
    Latin = 1,
    Chinese = 2,
}

/// <summary>Pure helpers for the CFD analog clock (angles, numerals, localized date).</summary>
public static class CustomerAnalogClock
{
    private static readonly string[] ArabicWeekdays =
    [
        "الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت",
    ];

    private static readonly string[] ArabicMonths =
    [
        "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو",
        "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر",
    ];

    private static readonly string[] ChineseWeekdays =
    [
        "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六",
    ];

    private static readonly string[] ChineseHours =
    [
        "十二", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十", "十一",
    ];

    private static readonly char[] EasternDigits =
    [
        '٠', '١', '٢', '٣', '٤', '٥', '٦', '٧', '٨', '٩',
    ];

    private static readonly char[] ChineseDigits =
    [
        '零', '一', '二', '三', '四', '五', '六', '七', '八', '九',
    ];

    /// <summary>Hour 1–12 label for the dial.</summary>
    public static string HourLabel(int hour1To12, CustomerClockScript script)
    {
        var hour = ((hour1To12 - 1) % 12 + 12) % 12 + 1;
        return script switch
        {
            CustomerClockScript.Arabic => ToEastern(hour.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            CustomerClockScript.Chinese => ChineseHours[hour % 12],
            _ => hour.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public static string FormatDate(DateTimeOffset now, CustomerClockScript script)
    {
        var local = now.ToLocalTime().DateTime;
        return script switch
        {
            CustomerClockScript.Arabic =>
                $"{ArabicWeekdays[(int)local.DayOfWeek]}، {ToEastern(local.Day.ToString(System.Globalization.CultureInfo.InvariantCulture))} {ArabicMonths[local.Month - 1]} {ToEastern(local.Year.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
            CustomerClockScript.Chinese =>
                $"{ToChineseYear(local.Year)}年{ToChineseMonth(local.Month)}月{ToChineseDay(local.Day)}日{ChineseWeekdays[(int)local.DayOfWeek]}",
            _ => local.ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
        };
    }

    /// <summary>Degrees clockwise from 12 o'clock for hour / minute / second hands.</summary>
    public static (double HourDegrees, double MinuteDegrees, double SecondDegrees) HandAngles(DateTime now)
    {
        var second = now.Second + now.Millisecond / 1000.0;
        var minute = now.Minute + second / 60.0;
        var hour = (now.Hour % 12) + minute / 60.0;
        return (hour * 30.0, minute * 6.0, second * 6.0);
    }

    public static string ToEastern(string westernDigits)
    {
        var chars = westernDigits.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '0' and <= '9')
                chars[i] = EasternDigits[chars[i] - '0'];
        }

        return new string(chars);
    }

    private static string ToChineseYear(int year)
    {
        var raw = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = ChineseDigits[chars[i] - '0'];
        return new string(chars);
    }

    private static string ToChineseMonth(int month) => month switch
    {
        10 => "十",
        11 => "十一",
        12 => "十二",
        _ => ChineseDigits[month].ToString(),
    };

    private static string ToChineseDay(int day)
    {
        if (day <= 10)
            return day == 10 ? "十" : ChineseDigits[day].ToString();
        if (day < 20)
            return "十" + ChineseDigits[day - 10];
        if (day == 20)
            return "二十";
        if (day < 30)
            return "二十" + ChineseDigits[day - 20];
        if (day == 30)
            return "三十";
        return "三十" + ChineseDigits[day - 30];
    }
}

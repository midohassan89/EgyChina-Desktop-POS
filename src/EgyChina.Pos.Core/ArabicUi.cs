namespace EgyChina.Pos.Core;

/// <summary>Maps known exception text to short Arabic cashier copy.</summary>
public static class ArabicUi
{
    public static string FromException(Exception ex, string fallback)
    {
        var message = ex.Message?.Trim() ?? string.Empty;
        if (message.Length == 0)
            return fallback;

        if (message.Contains("وردية", StringComparison.Ordinal)
            || message.Contains("كاشير", StringComparison.Ordinal)
            || message.Contains("الرقم السري", StringComparison.Ordinal)
            || message.Contains("القرص", StringComparison.Ordinal)
            || message.Contains("أفرغ", StringComparison.Ordinal)
            || message.Contains("أدخل", StringComparison.Ordinal)
            || message.Contains("سجّل", StringComparison.Ordinal)
            || message.Contains("يجب", StringComparison.Ordinal)
            || message.Contains("تعذر", StringComparison.Ordinal)
            || message.Contains("هناك", StringComparison.Ordinal)
            || message.Contains("إغلاق", StringComparison.Ordinal))
        {
            return message;
        }

        if (message.Contains("Tender is below", StringComparison.OrdinalIgnoreCase))
            return "المبلغ المستلم أقل من الإجمالي.";
        if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("constraint", StringComparison.OrdinalIgnoreCase))
        {
            return "تعذر حفظ السطر بسبب قيد قاعدة البيانات. السلة ما زالت هنا.";
        }

        return fallback + " (" + Truncate(message, 80) + ")";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

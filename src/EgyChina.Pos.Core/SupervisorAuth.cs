namespace EgyChina.Pos.Core;

public enum SupervisorActionKind
{
    DeleteItem = 0,
    ReturnMode = 1,
    OpenDrawer = 2,
}

public static class SupervisorAuth
{
    public static bool IsPrivileged(CashierRole role) =>
        role is CashierRole.Supervisor or CashierRole.Manager;

    public static bool IsPrivileged(CashierRecord? cashier) =>
        cashier is not null && IsPrivileged(cashier.Role);

    public static string TitleAr(SupervisorActionKind action) => action switch
    {
        SupervisorActionKind.DeleteItem => "حذف صنف — إذن مشرف",
        SupervisorActionKind.ReturnMode => "وضع الاسترجاع — إذن مشرف",
        SupervisorActionKind.OpenDrawer => "فتح الدرج — إذن مشرف",
        _ => "إذن مشرف",
    };

    public static string HintAr(SupervisorActionKind action) => action switch
    {
        SupervisorActionKind.DeleteItem => "أدخل رقم المشرف السري لحذف الصنف من السلة.",
        SupervisorActionKind.ReturnMode => "أدخل رقم المشرف السري لتفعيل وضع الاسترجاع.",
        SupervisorActionKind.OpenDrawer => "أدخل رقم المشرف السري لفتح درج النقدية.",
        _ => "أدخل رقم المشرف أو المدير السري.",
    };
}

public sealed record LocalReceiptLine(
    string ProductId,
    string? Barcode,
    string NameAr,
    string? NameZh,
    int Qty,
    long UnitPriceMinor);

public static class ReceiptCodes
{
    /// <summary>
    /// Local till codes only: <c>POS-123</c> or a sale GUID. Avoids clashing with product EANs.
    /// </summary>
    public static bool LooksLikeReceiptCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var code = raw.Trim();
        if (code.StartsWith("POS-", StringComparison.OrdinalIgnoreCase)
            && code.Length > 4
            && code.AsSpan(4).ToString().All(char.IsAsciiDigit))
        {
            return true;
        }

        return Guid.TryParse(code, out _);
    }

    public static bool TryParseReceiptNumber(string raw, out int receiptNo)
    {
        receiptNo = 0;
        var code = raw.Trim();
        if (!code.StartsWith("POS-", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(
            code.AsSpan(4),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out receiptNo)
            && receiptNo > 0;
    }
}

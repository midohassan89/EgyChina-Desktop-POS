namespace EgyChina.Pos.Core;

/// <summary>Local Z-report for the open till shift (SQLite), aligned with web <c>buildZReport</c>.</summary>
public sealed record ShiftZReport(
    string ShiftId,
    string CashierName,
    string OpenedAt,
    long OpeningFloatMinor,
    long CashSalesMinor,
    long VisaSalesMinor,
    long WalletSalesMinor,
    long InstaPaySalesMinor,
    long WeChatSalesMinor,
    long CashRefundsMinor,
    int TicketCount,
    int ReturnTicketCount,
    int CashTicketCount,
    int CardTicketCount)
{
    public long CardSalesMinor =>
        checked(VisaSalesMinor + WalletSalesMinor + InstaPaySalesMinor + WeChatSalesMinor);

    public long TotalSalesMinor => checked(CashSalesMinor + CardSalesMinor);

    /// <summary>Physical drawer expectation: opening float + net cash (sales minus cash returns).</summary>
    public long ExpectedCashMinor => checked(OpeningFloatMinor + CashSalesMinor);

    public string OpeningFloatText => Money.FormatEgp(OpeningFloatMinor);

    public string CashSalesText => Money.FormatEgp(CashSalesMinor);

    public string VisaSalesText => Money.FormatEgp(VisaSalesMinor);

    public string WalletSalesText => Money.FormatEgp(WalletSalesMinor);

    public string InstaPaySalesText => Money.FormatEgp(InstaPaySalesMinor);

    public string WeChatSalesText => Money.FormatEgp(WeChatSalesMinor);

    public string TotalSalesText => Money.FormatEgp(TotalSalesMinor);

    public string ExpectedCashText => Money.FormatEgp(ExpectedCashMinor);

    public string CashRefundsText => Money.FormatEgp(CashRefundsMinor);

    public string TicketCountText =>
        TicketCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string ReturnTicketCountText =>
        ReturnTicketCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public long VarianceMinor(long actualClosingCashMinor) =>
        checked(actualClosingCashMinor - ExpectedCashMinor);

    public string VarianceText(long actualClosingCashMinor) =>
        Money.FormatEgp(VarianceMinor(actualClosingCashMinor));
}

public sealed record OnlineOrderItem(string Id, string Name, decimal Quantity, decimal Price);

public sealed record OnlineOrderSummary(
    string Id,
    string OrderNumber,
    string CustomerName,
    string Phone,
    string? Address,
    string? Notes,
    decimal TotalAmount,
    string Status,
    string CreatedAt,
    string? AssignedDriverName,
    IReadOnlyList<OnlineOrderItem> Items)
{
    public string TotalText => Money.FormatEgp((long)Math.Round(TotalAmount * 100m, MidpointRounding.AwayFromZero));

    public string StatusLabelAr => Status switch
    {
        "PENDING" => "قيد الانتظار",
        "CONFIRMED" => "مؤكد",
        "PREPARING" => "جاري التجهيز",
        "SHIPPED" => "مع السائق",
        "COMPLETED" => "مكتمل",
        "CANCELLED" => "ملغى",
        "RETURNED" => "مرتجع",
        _ => Status,
    };

    public bool HasAssignedDriver => !string.IsNullOrWhiteSpace(AssignedDriverName);

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public bool HasAddress => !string.IsNullOrWhiteSpace(Address);

    public string ItemsSummary
    {
        get
        {
            if (Items.Count == 0)
                return string.Empty;
            return string.Join(
                " · ",
                Items.Take(4).Select(item =>
                    $"{item.Name} × {item.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        }
    }
}

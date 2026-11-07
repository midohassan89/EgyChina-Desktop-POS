namespace EgyChina.Pos.Core;

/// <summary>
/// ERP <c>paymentMethod</c> ids used by the web checkout (except staff meal — later phase).
/// </summary>
public enum PosPaymentMethod
{
    Cash = 0,
    Visa = 1,
    Wallet = 2,
    InstaPay = 3,
    WeChat = 4,
}

public static class PosPaymentMethods
{
    public static IReadOnlyList<PosPaymentMethod> CheckoutOptions { get; } =
    [
        PosPaymentMethod.Cash,
        PosPaymentMethod.Visa,
        PosPaymentMethod.Wallet,
        PosPaymentMethod.InstaPay,
        PosPaymentMethod.WeChat,
    ];

    public static string ToErpId(PosPaymentMethod method) => method switch
    {
        PosPaymentMethod.Cash => "cash",
        PosPaymentMethod.Visa => "visa",
        PosPaymentMethod.Wallet => "wallet",
        PosPaymentMethod.InstaPay => "instapay",
        PosPaymentMethod.WeChat => "wechat",
        _ => "cash",
    };

    public static string LabelAr(PosPaymentMethod method) => method switch
    {
        PosPaymentMethod.Cash => "كاش",
        PosPaymentMethod.Visa => "فيزا",
        PosPaymentMethod.Wallet => "محفظة",
        PosPaymentMethod.InstaPay => "انستا باي",
        PosPaymentMethod.WeChat => "وي شات",
        _ => "كاش",
    };

    public static string LabelArZh(PosPaymentMethod method) => method switch
    {
        PosPaymentMethod.Cash => "النقدية / 现金",
        PosPaymentMethod.Visa => "فيزا / 信用卡",
        PosPaymentMethod.Wallet => "محفظة / 电子钱包",
        PosPaymentMethod.InstaPay => "انستا باي / InstaPay",
        PosPaymentMethod.WeChat => "وي شات / 微信支付",
        _ => LabelAr(method),
    };

    /// <summary>Local SQLite <c>payment.method</c> value.</summary>
    public static string ToLocalDbMethod(PosPaymentMethod method) => method switch
    {
        PosPaymentMethod.Cash => "cash_egp",
        PosPaymentMethod.Visa => "card",
        PosPaymentMethod.Wallet => "wallet",
        PosPaymentMethod.InstaPay => "instapay",
        PosPaymentMethod.WeChat => "wechat",
        _ => "cash_egp",
    };

    public static bool IsCash(PosPaymentMethod method) => method == PosPaymentMethod.Cash;

    public static bool IsWeChat(PosPaymentMethod method) => method == PosPaymentMethod.WeChat;

    public static bool IsInstaPay(PosPaymentMethod method) => method == PosPaymentMethod.InstaPay;

    public static bool IsExactDigital(PosPaymentMethod method) =>
        method is PosPaymentMethod.WeChat or PosPaymentMethod.InstaPay
            or PosPaymentMethod.Visa or PosPaymentMethod.Wallet;

    public static bool ShowsCustomerQr(PosPaymentMethod method) =>
        IsWeChat(method) || IsInstaPay(method);
}

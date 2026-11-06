namespace EgyChina.Pos.Sync;

/// <summary>
/// How to turn local piasters into the JSON numbers <c>POST /api/checkout</c> sends.
/// The live client forwards <c>total</c> and <c>unitPrice</c> unchanged. It does not say whether those
/// numbers are pounds or piasters. Neither value is verified, so the default is <see cref="Unverified"/>
/// and the worker will not post.
/// </summary>
public enum ErpMoneyScale
{
    Unverified = 0,
    MajorUnits = 1,
    MinorUnits = 2,
}

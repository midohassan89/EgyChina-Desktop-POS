using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

public sealed class SyncOptions
{
    public const string EnvironmentVariable = "EGYCHINA_POS_API";
    public const string DeviceTokenVariable = "EGYCHINA_POS_DEVICE_TOKEN";
    /// <summary>Legacy staff cookie — never enough alone to transmit (phase 3).</summary>
    public const string CookieVariable = "EGYCHINA_POS_COOKIE";
    public const string PaymentMethodVariable = "EGYCHINA_POS_PAYMENT_METHOD";
    public const string MoneyScaleVariable = "EGYCHINA_POS_MONEY_SCALE";
    public const string CatalogPullVariable = "EGYCHINA_POS_CATALOG_PULL";

    /// <summary>
    /// Site origin, for example <c>https://erp.echinamart.com/</c>.
    /// The worker appends <c>api/checkout</c> and <c>api/products</c>. Null means no socket is opened.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>
    /// Enrolled device bearer from DPAPI / env. Required for transmission.
    /// Never log this value.
    /// </summary>
    public string? DeviceToken { get; init; }

    /// <summary>
    /// Value of <c>paymentMethod</c> on <c>POST /api/checkout</c>.
    /// </summary>
    public string? PaymentMethod { get; init; }

    public ErpMoneyScale MoneyScale { get; init; } = ErpMoneyScale.Unverified;

    public bool CatalogPull { get; init; }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public string? TransmissionBlock()
    {
        if (BaseAddress is null)
            return "not-configured";
        if (string.IsNullOrWhiteSpace(DeviceToken))
            return "auth-missing";
        if (MoneyScale == ErpMoneyScale.Unverified)
            return "money-scale-unverified";
        if (string.IsNullOrWhiteSpace(PaymentMethod))
            return "payment-method-unverified";
        return null;
    }

    public bool CanTransmit => TransmissionBlock() is null;

    public static SyncOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Uri? uri = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var text = raw.Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                PosLog.Error($"{EnvironmentVariable} is not an absolute http(s) URL.");
            }
            else
            {
                uri = parsed;
            }
        }

        var scale = Environment.GetEnvironmentVariable(MoneyScaleVariable)?.Trim().ToLowerInvariant() switch
        {
            "major" => ErpMoneyScale.MajorUnits,
            "minor" => ErpMoneyScale.MinorUnits,
            _ => ErpMoneyScale.Unverified,
        };

        var deviceToken = DeviceCredentialStore.ImportFromEnvironmentIfPresent();

        return new SyncOptions
        {
            BaseAddress = uri,
            DeviceToken = deviceToken,
            PaymentMethod = Environment.GetEnvironmentVariable(PaymentMethodVariable),
            MoneyScale = scale,
            CatalogPull = string.Equals(Environment.GetEnvironmentVariable(CatalogPullVariable), "1", StringComparison.Ordinal),
        };
    }
}

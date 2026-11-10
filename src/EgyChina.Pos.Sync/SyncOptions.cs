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

    /// <summary>
    /// Per-request timeout for ERP sync. Cashier Wi‑Fi/4G often needs more than a few seconds
    /// for <c>POST /api/store/pos-checkout</c>; 5s caused false transport cancellations on tills.
    /// Override with <c>EGYCHINA_POS_HTTP_TIMEOUT</c> (seconds, 5–120).
    /// </summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public const string HttpTimeoutVariable = "EGYCHINA_POS_HTTP_TIMEOUT";

    public string? TransmissionBlock()
    {
        if (BaseAddress is null)
            return "not-configured";
        // Device Bearer is optional: sales upload via /api/store/pos-checkout when missing.
        if (MoneyScale == ErpMoneyScale.Unverified)
            return "money-scale-unverified";
        // Payment method comes from each sale payload (cash/visa/…). Env override is optional.
        return null;
    }

    public bool CanTransmit => TransmissionBlock() is null;

    /// <summary>Production ERP origin used when <c>EGYCHINA_POS_API</c> is unset.</summary>
    public static readonly Uri DefaultErpOrigin = new("https://erp.echinamart.com/");

    public static SyncOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Uri? uri = DefaultErpOrigin;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var text = raw.Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                PosLog.Error($"{EnvironmentVariable} is not an absolute http(s) URL.");
                uri = DefaultErpOrigin;
            }
            else
            {
                uri = parsed;
            }
        }

        var scale = Environment.GetEnvironmentVariable(MoneyScaleVariable)?.Trim().ToLowerInvariant() switch
        {
            "minor" => ErpMoneyScale.MinorUnits,
            "unverified" => ErpMoneyScale.Unverified,
            // ERP checkout expects major EGP units (22.00). Default major when unset.
            _ => ErpMoneyScale.MajorUnits,
        };

        var deviceToken = DeviceCredentialStore.ImportFromEnvironmentIfPresent();
        var httpTimeout = TimeSpan.FromSeconds(45);
        var timeoutRaw = Environment.GetEnvironmentVariable(HttpTimeoutVariable)?.Trim();
        if (int.TryParse(timeoutRaw, out var timeoutSec))
        {
            timeoutSec = Math.Clamp(timeoutSec, 5, 120);
            httpTimeout = TimeSpan.FromSeconds(timeoutSec);
        }

        return new SyncOptions
        {
            BaseAddress = uri,
            DeviceToken = deviceToken,
            PaymentMethod = Environment.GetEnvironmentVariable(PaymentMethodVariable),
            MoneyScale = scale,
            CatalogPull = string.Equals(Environment.GetEnvironmentVariable(CatalogPullVariable), "1", StringComparison.Ordinal),
            HttpTimeout = httpTimeout,
        };
    }
}

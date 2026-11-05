using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

public sealed class SyncOptions
{
    public const string EnvironmentVariable = "EGYCHINA_POS_API";

    /// <summary>
    /// Absolute base URL ending with <c>/</c>, for example
    /// <c>https://erp.echinamart.com/api/pos/v1/</c> once that API exists.
    /// Null means the worker stays local and does not open a socket.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public static SyncOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return new SyncOptions();

        var text = raw.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            PosLog.Error($"{EnvironmentVariable} is not an absolute http(s) URL.");
            return new SyncOptions();
        }

        return new SyncOptions { BaseAddress = uri };
    }
}

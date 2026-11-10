using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EgyChina.Pos.App.Services;

/// <summary>
/// Real internet probe — not "any NIC is up". Matches Windows NCSI when possible.
/// </summary>
public static class InternetReachability
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly object Gate = new();
    private static bool _lastKnown;
    private static DateTimeOffset _lastCheckedAt = DateTimeOffset.MinValue;
    private static int _probeBusy;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "EgyChina-Desktop-POS/internet-check");
        return client;
    }

    public static bool LastKnown => _lastKnown;

    /// <summary>
    /// Fast cached answer for UI ticks. Starts a background probe when the cache is stale.
    /// </summary>
    public static bool GetCachedOrStartProbe(Action<bool>? onUpdated = null, int maxAgeSeconds = 4)
    {
        lock (Gate)
        {
            if ((DateTimeOffset.UtcNow - _lastCheckedAt).TotalSeconds <= maxAgeSeconds)
                return _lastKnown;
        }

        _ = ProbeAsync(onUpdated);
        lock (Gate)
            return _lastKnown;
    }

    public static async Task<bool> ProbeAsync(Action<bool>? onUpdated = null)
    {
        if (Interlocked.CompareExchange(ref _probeBusy, 1, 0) != 0)
        {
            lock (Gate)
                return _lastKnown;
        }

        try
        {
            var online = await ProbeCoreAsync().ConfigureAwait(false);
            lock (Gate)
            {
                _lastKnown = online;
                _lastCheckedAt = DateTimeOffset.UtcNow;
            }

            onUpdated?.Invoke(online);
            return online;
        }
        finally
        {
            Interlocked.Exchange(ref _probeBusy, 0);
        }
    }

    private static async Task<bool> ProbeCoreAsync()
    {
        // 1) Windows Network Connectivity Status Indicator — same family as the taskbar globe.
        try
        {
            using var response = await Http.GetAsync("http://www.msftconnecttest.com/connecttest.txt")
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                if (string.Equals(body, "Microsoft Connect Test", StringComparison.Ordinal))
                    return true;
                // Some captive portals rewrite the body — treat HTTP 200 on NCSI as online enough.
                if (body.Length > 0)
                    return true;
            }
        }
        catch
        {
            // Fall through to TCP probe.
        }

        // 2) Direct TCP to public DNS — works when HTTP is blocked but the link is live.
        if (await CanConnectAsync("1.1.1.1", 443).ConfigureAwait(false))
            return true;
        if (await CanConnectAsync("8.8.8.8", 53).ConfigureAwait(false))
            return true;

        // 3) Last resort: at least one operational interface that is not loopback/tunnel-only.
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return false;
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static async Task<bool> CanConnectAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

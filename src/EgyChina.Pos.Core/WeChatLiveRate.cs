using System.Net.Http;
using System.Text.Json;

namespace EgyChina.Pos.Core;

/// <summary>
/// Fetches live CNY→EGP from the same public feed the web POS uses, then applies
/// <see cref="WeChatFx.SafetyMargin"/> so the till never under-quotes yuan.
/// </summary>
public static class WeChatLiveRate
{
    public const string FeedUrl =
        "https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/cny.json";

    public static async Task<decimal> FetchSafeEgpPerCnyAsync(
        HttpClient http,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var response = await http.GetAsync(FeedUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("cny", out var cny) ||
            !cny.TryGetProperty("egp", out var egp) ||
            egp.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException("EGP rate missing from live feed.");
        }

        var raw = egp.GetDecimal();
        if (raw <= 0)
            throw new InvalidOperationException("EGP rate from live feed is not positive.");
        return WeChatFx.ApplySafetyMargin(raw);
    }
}

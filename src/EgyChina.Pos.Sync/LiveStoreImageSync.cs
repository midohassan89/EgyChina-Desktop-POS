using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Pulls product image URLs from the public live storefront catalog
/// (<c>GET /api/store/products</c> on production ERP) and writes them into the local till DB.
/// Does not require a device Bearer — this route is CORS-public for the web shop.
/// </summary>
public static class LiveStoreImageSync
{
    public static async Task<LiveImageSyncResult> ApplyAsync(
        PosDatabase database,
        Uri erpOrigin,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var origin = erpOrigin.AbsoluteUri.TrimEnd('/') + "/";
        var updated = 0;
        var pages = 0;
        var seen = 0;
        const int limit = 100;
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"{origin}api/store/products?page={page}&limit={limit}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new LiveImageSyncResult(
                    false,
                    pages,
                    seen,
                    updated,
                    $"http-{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<StoreProductsResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (body?.Products is null || body.Products.Count == 0)
                break;

            pages++;
            var batch = new List<(string ProductId, string ImageUrl)>();
            foreach (var product in body.Products)
            {
                seen++;
                if (string.IsNullOrWhiteSpace(product.Id) || string.IsNullOrWhiteSpace(product.Image))
                    continue;
                batch.Add((product.Id.Trim(), product.Image.Trim()));
            }

            if (batch.Count > 0)
                updated += database.UpdateProductImageUrls(batch);

            var hasMore = body.Meta?.HasMore == true;
            if (!hasMore)
                break;
            page++;
            if (page > 500)
                break;
        }

        return new LiveImageSyncResult(true, pages, seen, updated, null);
    }
}

public sealed record LiveImageSyncResult(
    bool Ok,
    int Pages,
    int Seen,
    int Updated,
    string? Error);

internal sealed class StoreProductsResponse
{
    [JsonPropertyName("products")]
    public List<StoreProductDto>? Products { get; set; }

    [JsonPropertyName("meta")]
    public StoreMetaDto? Meta { get; set; }
}

internal sealed class StoreProductDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }
}

internal sealed class StoreMetaDto
{
    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Rebuilds the local shelf from the public live store catalog
/// (<c>GET https://erp.echinamart.com/api/store/products</c>)
/// and departments from <c>GET /api/store/categories</c>.
/// Prefer device Bearer <c>/api/products</c> when enrolled; this path unblocks tills
/// that only have the public storefront route today.
/// </summary>
public static class LiveStoreCatalogSync
{
    public static async Task<LiveCatalogSyncResult> ApplyAsync(
        PosDatabase database,
        Uri erpOrigin,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var origin = erpOrigin.AbsoluteUri.TrimEnd('/') + "/";
        await SyncCategoriesAsync(database, origin, http, cancellationToken).ConfigureAwait(false);

        var products = new List<object>();
        var keepIds = new HashSet<string>(StringComparer.Ordinal);
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
                return new LiveCatalogSyncResult(
                    false,
                    pages,
                    seen,
                    0,
                    0,
                    $"http-{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<StoreCatalogResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (body?.Products is null || body.Products.Count == 0)
                break;

            pages++;
            foreach (var product in body.Products)
            {
                seen++;
                if (string.IsNullOrWhiteSpace(product.Id) || string.IsNullOrWhiteSpace(product.Name))
                    continue;

                var numericId = product.WcId is > 0
                    ? product.WcId.Value
                    : StablePositiveId(product.Id);
                var sku = string.IsNullOrWhiteSpace(product.Sku)
                    ? "ERP:" + numericId.ToString(CultureInfo.InvariantCulture)
                    : product.Sku.Trim();
                var barcode = FirstBarcode(product);
                var regular = product.Price;
                var sale = product.SalePrice is > 0 ? product.SalePrice.Value : (double?)null;
                // Shelf price = promo when present, otherwise regular (never sell at pre-discount).
                var shelf = sale ?? regular;
                if (shelf < 0)
                    continue;

                keepIds.Add(product.Id.Trim());
                var categoryId = string.IsNullOrWhiteSpace(product.CategoryId)
                    ? null
                    : product.CategoryId.Trim();
                products.Add(new Dictionary<string, object?>
                {
                    ["id"] = numericId,
                    ["wcId"] = numericId,
                    ["prismaId"] = product.Id.Trim(),
                    ["posKey"] = product.Id.Trim(),
                    ["sku"] = sku,
                    ["barcode"] = barcode,
                    ["name"] = product.Name.Trim(),
                    ["nameEn"] = product.NameEn,
                    ["nameZh"] = product.NameZh,
                    ["price"] = shelf.ToString("0.##", CultureInfo.InvariantCulture),
                    ["regular_price"] = regular.ToString("0.##", CultureInfo.InvariantCulture),
                    ["sale_price"] = sale is > 0
                        ? sale.Value.ToString("0.##", CultureInfo.InvariantCulture)
                        : "",
                    ["salePrice"] = sale is > 0
                        ? sale.Value.ToString("0.##", CultureInfo.InvariantCulture)
                        : "",
                    ["images"] = string.IsNullOrWhiteSpace(product.Image)
                        ? Array.Empty<object>()
                        : new object[] { new Dictionary<string, object?> { ["id"] = 0, ["src"] = product.Image } },
                    ["meta_data"] = string.IsNullOrWhiteSpace(barcode)
                        ? Array.Empty<object>()
                        : new object[] { new Dictionary<string, object?> { ["key"] = "_op_barcode", ["value"] = barcode } },
                    ["isPackage"] = false,
                    ["categoryId"] = categoryId,
                });

                if (product.Packages is null)
                    continue;

                var index = 0;
                foreach (var pkg in product.Packages)
                {
                    if (string.IsNullOrWhiteSpace(pkg.Id))
                        continue;
                    var packSize = Math.Max(1, pkg.PackSize ?? 1);
                    var packPrice = pkg.Price is > 0 ? pkg.Price.Value : shelf * packSize;
                    var posKey = product.Id.Trim() + "-pkg-" + pkg.Id.Trim();
                    keepIds.Add(posKey);
                    products.Add(new Dictionary<string, object?>
                    {
                        ["id"] = numericId,
                        ["prismaId"] = product.Id.Trim(),
                        ["posKey"] = posKey,
                        ["sku"] = "PKG:" + posKey,
                        ["name"] = string.IsNullOrWhiteSpace(pkg.Name) ? product.Name + " - كرتونة" : pkg.Name.Trim(),
                        ["nameEn"] = pkg.NameEn ?? product.NameEn,
                        ["nameZh"] = pkg.NameZh ?? product.NameZh,
                        ["price"] = packPrice.ToString("0.##", CultureInfo.InvariantCulture),
                        ["regular_price"] = packPrice.ToString("0.##", CultureInfo.InvariantCulture),
                        ["sale_price"] = "",
                        ["images"] = string.IsNullOrWhiteSpace(product.Image)
                            ? Array.Empty<object>()
                            : new object[] { new Dictionary<string, object?> { ["id"] = 0, ["src"] = product.Image } },
                        ["meta_data"] = string.IsNullOrWhiteSpace(pkg.Barcode)
                            ? Array.Empty<object>()
                            : new object[] { new Dictionary<string, object?> { ["key"] = "_op_barcode", ["value"] = pkg.Barcode } },
                        ["isPackage"] = true,
                        ["categoryId"] = categoryId,
                    });
                    index++;
                }
            }

            if (body.Meta?.HasMore != true)
                break;
            page++;
            if (page > 500)
                break;
        }

        if (products.Count == 0)
            return new LiveCatalogSyncResult(false, pages, seen, 0, 0, "empty-catalog");

        var payload = JsonSerializer.Serialize(new
        {
            serverTime = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            products,
            removedPrismaIds = Array.Empty<string>(),
        });

        var applied = database.ApplyCatalogFromErpJson(payload);
        var deactivated = database.DeactivateProductsNotIn(keepIds);
        database.RebindFavorites();
        return new LiveCatalogSyncResult(true, pages, seen, applied.Upserted, deactivated, null);
    }

    private static async Task SyncCategoriesAsync(
        PosDatabase database,
        string origin,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = origin + "api/store/categories";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            var rows = await response.Content.ReadFromJsonAsync<List<StoreCategoryDto>>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (rows is null || rows.Count == 0)
                return;

            var drafts = new List<CatalogCategoryDraft>();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Name))
                    continue;
                var localized = ProductDisplayNames.PreferLocalized(row.Name, row.NameEn, row.NameZh);
                drafts.Add(new CatalogCategoryDraft(
                    row.Id.Trim(),
                    localized,
                    row.NameEn,
                    row.NameZh,
                    row.Slug,
                    row.Image,
                    row.ProductCount));
            }

            if (drafts.Count > 0)
                database.UpsertCategories(drafts);
        }
        catch
        {
            // Category sync is best-effort; product shelf still proceeds.
        }
    }

    private static string? FirstBarcode(StoreCatalogProductDto product)
    {
        if (!string.IsNullOrWhiteSpace(product.Barcode))
            return product.Barcode.Trim();
        if (product.Packages is null)
            return null;
        foreach (var pkg in product.Packages)
        {
            if (!string.IsNullOrWhiteSpace(pkg.Barcode))
                return pkg.Barcode.Trim();
        }

        return null;
    }

    private static long StablePositiveId(string prismaId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prismaId.Trim()));
        var value = BitConverter.ToInt64(hash, 0);
        if (value == long.MinValue)
            value = 1;
        value = Math.Abs(value);
        return (value % 900_000_000L) + 100_000_000L;
    }
}

public sealed record LiveCatalogSyncResult(
    bool Ok,
    int Pages,
    int Seen,
    int Upserted,
    int Deactivated,
    string? Error);

internal sealed class StoreCatalogResponse
{
    [JsonPropertyName("products")]
    public List<StoreCatalogProductDto>? Products { get; set; }

    [JsonPropertyName("meta")]
    public StoreMetaDto? Meta { get; set; }
}

internal sealed class StoreCatalogProductDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("wcId")]
    public long? WcId { get; set; }

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nameEn")]
    public string? NameEn { get; set; }

    [JsonPropertyName("nameZh")]
    public string? NameZh { get; set; }

    [JsonPropertyName("price")]
    public double Price { get; set; }

    [JsonPropertyName("salePrice")]
    public double? SalePrice { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("categoryId")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("packages")]
    public List<StoreCatalogPackageDto>? Packages { get; set; }
}

internal sealed class StoreCategoryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nameEn")]
    public string? NameEn { get; set; }

    [JsonPropertyName("nameZh")]
    public string? NameZh { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("productCount")]
    public int ProductCount { get; set; }
}

internal sealed class StoreCatalogPackageDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nameEn")]
    public string? NameEn { get; set; }

    [JsonPropertyName("nameZh")]
    public string? NameZh { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("price")]
    public double? Price { get; set; }

    [JsonPropertyName("packSize")]
    public int? PackSize { get; set; }
}

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace EgyChina.Pos.Data;

/// <summary>
/// Downloads ERP product images into the till data folder for offline display.
/// </summary>
public static class ProductImageCache
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    public static string CacheDirectory()
    {
        var dir = Path.Combine(PosPaths.Root(), "product-images");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string? TryLocalPath(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;

        var stem = FileStem(productId);
        var dir = CacheDirectory();
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(dir, stem + ext);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;
        }

        return null;
    }

    public static Uri? ResolveMediaUri(string? imageUrl, Uri? mediaBase)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        var text = imageUrl.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        var baseAddress = mediaBase ?? DefaultMediaBase();
        if (!baseAddress.AbsoluteUri.EndsWith('/'))
            baseAddress = new Uri(baseAddress.AbsoluteUri + "/", UriKind.Absolute);

        var relative = text.TrimStart('/');
        return new Uri(baseAddress, relative);
    }

    public static Uri DefaultMediaBase()
    {
        var raw = Environment.GetEnvironmentVariable(SyncEnvNames.Api);
        if (!string.IsNullOrWhiteSpace(raw)
            && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var fromEnv)
            && (fromEnv.Scheme == Uri.UriSchemeHttp || fromEnv.Scheme == Uri.UriSchemeHttps))
        {
            return fromEnv;
        }

        return new Uri("https://erp.echinamart.com/", UriKind.Absolute);
    }

    public static async Task<string?> EnsureLocalAsync(
        string productId,
        string? imageUrl,
        Uri? mediaBase,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var remote = ResolveMediaUri(imageUrl, mediaBase);
        if (remote is null)
            return TryLocalPath(productId);

        var ext = GuessExtension(remote);
        var local = Path.Combine(CacheDirectory(), FileStem(productId) + ext);
        var meta = local + ".src";
        if (File.Exists(local)
            && new FileInfo(local).Length > 0
            && File.Exists(meta)
            && string.Equals(File.ReadAllText(meta).Trim(), remote.AbsoluteUri, StringComparison.Ordinal))
        {
            return local;
        }

        try
        {
            using var response = await http.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return TryLocalPath(productId);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return TryLocalPath(productId);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var finalExt = ExtFromContentType(contentType) ?? ext;
            local = Path.Combine(CacheDirectory(), FileStem(productId) + finalExt);
            meta = local + ".src";
            await File.WriteAllBytesAsync(local, bytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(meta, remote.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            return local;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return TryLocalPath(productId);
        }
    }

    public static async Task<int> WarmAsync(
        IEnumerable<(string ProductId, string? ImageUrl)> items,
        Uri? mediaBase,
        int maxDownloads,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheDirectory());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1-images");

        var warmed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (warmed >= maxDownloads)
                break;
            if (string.IsNullOrWhiteSpace(item.ImageUrl))
                continue;
            if (TryLocalPath(item.ProductId) is not null)
                continue;

            var path = await EnsureLocalAsync(item.ProductId, item.ImageUrl, mediaBase, http, cancellationToken)
                .ConfigureAwait(false);
            if (path is not null)
                warmed++;
        }

        return warmed;
    }

    private static string FileStem(string productId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(productId.Trim())))
            .ToLowerInvariant();
        return hash[..24];
    }

    private static string GuessExtension(Uri uri)
    {
        var path = uri.AbsolutePath;
        foreach (var ext in Extensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return ext;
        }

        return ".jpg";
    }

    private static string? ExtFromContentType(string? contentType) =>
        contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => null,
        };
}

/// <summary>Avoids a project cycle: Data must not reference Sync.Env constants.</summary>
internal static class SyncEnvNames
{
    public const string Api = "EGYCHINA_POS_API";
}

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace EgyChina.Pos.Data;

/// <summary>
/// Caches ERP product images under the till data folder.
/// Prefers a same-machine copy from <c>ERP/public/uploads/...</c>, then HTTP.
/// </summary>
public static class ProductImageCache
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "lookaside.instagram.com",
        "instagram.com",
        "cdninstagram.com",
        "fbcdn.net",
    };

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
            if (BlockedHosts.Any(h => absolute.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                      || absolute.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

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
        if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(imageUrl))
            return TryLocalPath(productId);

        var existing = TryLocalPath(productId);
        var remote = ResolveMediaUri(imageUrl, mediaBase);
        if (existing is not null)
        {
            if (remote is null)
                return existing;
            var meta = existing + ".src";
            if (File.Exists(meta)
                && string.Equals(File.ReadAllText(meta).Trim(), remote.AbsoluteUri, StringComparison.Ordinal))
            {
                return existing;
            }
        }

        // Prefer live ERP HTTP (production uploads), then same-machine public folder copy.
        if (remote is not null)
        {
            try
            {
                using var response = await http.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        var contentType = response.Content.Headers.ContentType?.MediaType;
                        var finalExt = ExtFromContentType(contentType) ?? GuessExtension(remote);
                        var local = Path.Combine(CacheDirectory(), FileStem(productId) + finalExt);
                        var meta = local + ".src";
                        await File.WriteAllBytesAsync(local, bytes, cancellationToken).ConfigureAwait(false);
                        await File.WriteAllTextAsync(meta, remote.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                        return local;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Fall through to local public copy.
            }
        }

        var copied = TryCopyFromLocalPublicFolder(productId, imageUrl, remote);
        if (copied is not null)
            return copied;

        return TryLocalPath(productId);
    }

    public static async Task<WarmResult> WarmAsync(
        IReadOnlyList<(string ProductId, string? ImageUrl)> items,
        Uri? mediaBase,
        int maxDownloads,
        int parallelism,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheDirectory());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1-images");

        var pending = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ImageUrl) && TryLocalPath(item.ProductId) is null)
            .Take(Math.Max(0, maxDownloads))
            .ToList();

        if (pending.Count == 0)
            return new WarmResult(0, 0, 0);

        var gate = new SemaphoreSlim(Math.Clamp(parallelism, 1, 16));
        var warmed = 0;
        var failed = 0;
        var tasks = pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = await EnsureLocalAsync(item.ProductId, item.ImageUrl, mediaBase, http, cancellationToken)
                    .ConfigureAwait(false);
                if (path is not null)
                {
                    var count = Interlocked.Increment(ref warmed);
                    progress?.Report(count);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new WarmResult(pending.Count, warmed, failed);
    }

    private static string? TryCopyFromLocalPublicFolder(string productId, string imageUrl, Uri? remote)
    {
        foreach (var source in EnumerateLocalPublicCandidates(imageUrl))
        {
            try
            {
                if (!File.Exists(source) || new FileInfo(source).Length == 0)
                    continue;

                var ext = Path.GetExtension(source);
                if (string.IsNullOrWhiteSpace(ext) || !Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    ext = ".jpg";

                var local = Path.Combine(CacheDirectory(), FileStem(productId) + ext.ToLowerInvariant());
                File.Copy(source, local, overwrite: true);
                var meta = local + ".src";
                var stamp = remote?.AbsoluteUri ?? source;
                File.WriteAllText(meta, stamp);
                return local;
            }
            catch (Exception)
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLocalPublicCandidates(string imageUrl)
    {
        var text = imageUrl.Trim();
        string relative;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            relative = absolute.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        }
        else
        {
            relative = text.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        }

        if (string.IsNullOrWhiteSpace(relative))
            yield break;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            yield return Path.Combine(desktop, "ERP", "public", relative);
            yield return Path.Combine(desktop, "egychina-erp", "public", relative);
        }

        // Walk up from the running binary / repo checkout.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            yield return Path.Combine(dir.FullName, "ERP", "public", relative);
            yield return Path.Combine(dir.FullName, "public", relative);
            dir = dir.Parent;
        }
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

public sealed record WarmResult(int Attempted, int Warmed, int Failed);

/// <summary>Avoids a project cycle: Data must not reference Sync.Env constants.</summary>
internal static class SyncEnvNames
{
    public const string Api = "EGYCHINA_POS_API";
}

using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

[Collection("PosPathsSerial")]
public sealed class ProductImageCacheTests : IDisposable
{
    private readonly string _directory;

    public ProductImageCacheTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "egychina-pos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        PosPaths.SetRootOverrideForTests(_directory);
    }

    public void Dispose()
    {
        PosPaths.SetRootOverrideForTests(null);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ResolveMediaUri_skips_instagram_lookaside()
    {
        var uri = ProductImageCache.ResolveMediaUri(
            "https://lookaside.instagram.com/seo/google_widget/crawler/?media_id=1",
            new Uri("https://erp.echinamart.com/"));
        Assert.Null(uri);
    }

    [Fact]
    public void ResolveMediaUri_joins_relative_upload_path()
    {
        var uri = ProductImageCache.ResolveMediaUri(
            "/uploads/products/demo.jpg",
            new Uri("https://erp.echinamart.com/"));
        Assert.Equal("https://erp.echinamart.com/uploads/products/demo.jpg", uri!.AbsoluteUri);
    }

    [Fact]
    public async Task EnsureLocalAsync_copies_from_local_erp_public_folder()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var source = Path.Combine(desktop, "ERP", "public", "uploads", "products");
        if (!Directory.Exists(source))
            return; // machine without local ERP checkout

        var sample = Directory.EnumerateFiles(source, "*.jpg").FirstOrDefault()
            ?? Directory.EnumerateFiles(source, "*.png").FirstOrDefault()
            ?? Directory.EnumerateFiles(source, "*.webp").FirstOrDefault();
        if (sample is null)
            return;

        var relative = "/uploads/products/" + Path.GetFileName(sample);
        using var http = new HttpClient();
        var path = await ProductImageCache.EnsureLocalAsync(
            "local-copy-product",
            relative,
            new Uri("https://erp.echinamart.com/"),
            http,
            CancellationToken.None);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path!).Length > 0);
        Assert.StartsWith(Path.Combine(_directory, "product-images"), path, StringComparison.OrdinalIgnoreCase);
    }
}

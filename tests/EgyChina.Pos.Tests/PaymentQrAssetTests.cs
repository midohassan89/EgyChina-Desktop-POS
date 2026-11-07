using EgyChina.Pos.Data;
using Xunit;

namespace EgyChina.Pos.Tests;

[Collection("PosPathsSerial")]
public sealed class PaymentQrAssetTests : IDisposable
{
    private readonly string _directory;

    public PaymentQrAssetTests()
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
    public void EnsurePaymentQrAssets_copies_from_repo_assets_when_present()
    {
        var repoAssets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets"));
        var sourceWechat = Path.Combine(repoAssets, "wechat-qr.jpg");
        if (!File.Exists(sourceWechat))
        {
            // CI / odd layout: seed a tiny stand-in under a candidate path next to BaseDirectory.
            var bundled = Path.Combine(AppContext.BaseDirectory, "assets");
            Directory.CreateDirectory(bundled);
            File.WriteAllBytes(Path.Combine(bundled, "wechat-qr.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
            File.WriteAllBytes(Path.Combine(bundled, "instapay-qr.jpeg"), [0xFF, 0xD8, 0xFF, 0xD9]);
        }

        PosPaths.EnsurePaymentQrAssets();
        Assert.True(File.Exists(PosPaths.WeChatQrFile()));
        Assert.True(new FileInfo(PosPaths.WeChatQrFile()).Length > 0);
        Assert.True(File.Exists(PosPaths.InstaPayQrFile()));
        Assert.StartsWith(Path.Combine(_directory, "assets"), PosPaths.WeChatQrFile(), StringComparison.OrdinalIgnoreCase);
    }
}

namespace EgyChina.Pos.Data;

public static class PosPaths
{
    private static string? _rootOverride;

    /// <summary>Test-only redirect of the data directory. Pass null to clear.</summary>
    public static void SetRootOverrideForTests(string? absoluteRoot) =>
        _rootOverride = string.IsNullOrWhiteSpace(absoluteRoot) ? null : absoluteRoot;

    public static string Root()
    {
        if (_rootOverride is not null)
            return _rootOverride;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "EgyChina", "Pos");
    }

    public static string DatabaseFile() => Path.Combine(Root(), "pos.db");

    public static string LogDirectory() => Path.Combine(Root(), "logs");

    public static string BackupDirectory() => Path.Combine(Root(), "backups");

    public static string AssetsDirectory() => Path.Combine(Root(), "assets");

    public static string WeChatQrFile() => ResolveAssetFile("wechat-qr.jpg");

    public static string InstaPayQrFile() => ResolveAssetFile("instapay-qr.jpeg");

    /// <summary>
    /// Prefers the writable till data folder, then copies from app/content/dev folders if needed.
    /// </summary>
    public static string ResolveAssetFile(string fileName)
    {
        Directory.CreateDirectory(AssetsDirectory());
        var primary = Path.Combine(AssetsDirectory(), fileName);
        if (File.Exists(primary) && new FileInfo(primary).Length > 0)
            return primary;

        foreach (var candidate in EnumerateAssetCandidates(fileName))
        {
            try
            {
                if (!File.Exists(candidate) || new FileInfo(candidate).Length == 0)
                    continue;
                File.Copy(candidate, primary, overwrite: true);
                return primary;
            }
            catch (Exception)
            {
                // Try the next candidate; never crash the till for a missing QR.
            }
        }

        return primary;
    }

    /// <summary>
    /// Seeds both payment QR files into the till assets directory from known locations.
    /// </summary>
    public static void EnsurePaymentQrAssets()
    {
        _ = ResolveAssetFile("wechat-qr.jpg");
        _ = ResolveAssetFile("instapay-qr.jpeg");
    }

    private static IEnumerable<string> EnumerateAssetCandidates(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "assets", fileName);
        yield return Path.Combine(baseDir, fileName);

        // Debug / repo checkout: .../src/EgyChina.Pos.App/bin/Debug/net8.0 → repo assets/
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            yield return Path.Combine(dir.FullName, "assets", fileName);
            dir = dir.Parent;
        }

        // Optional: same machine ERP public uploads used by the web CFD.
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            yield return Path.Combine(desktop, "EgyChina-Desktop-POS", "assets", fileName);
            yield return Path.Combine(desktop, "ERP", "public", "uploads", "images", fileName);
            yield return Path.Combine(desktop, "ERP", "assets", fileName);
        }
    }
}

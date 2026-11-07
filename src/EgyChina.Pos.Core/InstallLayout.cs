namespace EgyChina.Pos.Core;

/// <summary>
/// File-system helpers for the versioned till install. Junction creation is Windows-only
/// and done by the install scripts; this type tracks the previous version pointer.
/// </summary>
public static class InstallLayout
{
    public static void EnsureRoot(string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(PosInstallPaths.AppDirectory(installRoot));
    }

    public static void WritePreviousVersion(string installRoot, string? version)
    {
        var path = PosInstallPaths.PreviousVersionFile(installRoot);
        if (string.IsNullOrWhiteSpace(version))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        File.WriteAllText(path, version.Trim() + Environment.NewLine);
    }

    public static string? ReadPreviousVersion(string installRoot)
    {
        var path = PosInstallPaths.PreviousVersionFile(installRoot);
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static string? TryResolveCurrentVersion(string installRoot)
    {
        var current = PosInstallPaths.CurrentDirectory(installRoot);
        if (!Directory.Exists(current))
            return null;

        string target;
        try
        {
            target = Path.GetFullPath(current);
            var link = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
            if (link is not null)
                target = link.FullName;
        }
        catch (IOException)
        {
            target = Path.GetFullPath(current);
        }

        var appRoot = Path.GetFullPath(PosInstallPaths.AppDirectory(installRoot));
        if (!target.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = Path.GetRelativePath(appRoot, target);
        if (string.IsNullOrWhiteSpace(relative) ||
            relative.Contains("..", StringComparison.Ordinal) ||
            relative.Contains(Path.DirectorySeparatorChar) ||
            relative.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        return relative;
    }

    public static bool VersionLooksInstalled(string installRoot, string version) =>
        File.Exists(PosInstallPaths.VersionExe(version, installRoot));
}

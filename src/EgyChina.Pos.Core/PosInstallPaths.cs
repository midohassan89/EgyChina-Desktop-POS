namespace EgyChina.Pos.Core;

/// <summary>
/// Versioned Windows install layout under <c>C:\EgyChina\Pos</c> (override with env).
/// Data (SQLite, logs, backups) stays under LocalAppData — not inside <c>app\&lt;version&gt;</c>.
/// </summary>
public static class PosInstallPaths
{
    public const string DefaultRoot = @"C:\EgyChina\Pos";
    public const string RootEnvironmentVariable = "EGYCHINA_POS_INSTALL_ROOT";
    public const string ExeName = "EgyChina.Pos.App.exe";

    public static string Root()
    {
        var fromEnv = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv.Trim());
        return DefaultRoot;
    }

    public static string AppDirectory(string? installRoot = null) =>
        Path.Combine(installRoot ?? Root(), "app");

    public static string VersionDirectory(string version, string? installRoot = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version is required.", nameof(version));
        var safe = version.Trim();
        if (safe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            safe.Contains("..", StringComparison.Ordinal) ||
            safe.Contains('/', StringComparison.Ordinal) ||
            safe.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Version must be a single path segment.", nameof(version));
        }

        return Path.Combine(AppDirectory(installRoot), safe);
    }

    public static string CurrentDirectory(string? installRoot = null) =>
        Path.Combine(installRoot ?? Root(), "current");

    public static string PreviousVersionFile(string? installRoot = null) =>
        Path.Combine(installRoot ?? Root(), "previous.version");

    public static string CurrentExe(string? installRoot = null) =>
        Path.Combine(CurrentDirectory(installRoot), ExeName);

    public static string VersionExe(string version, string? installRoot = null) =>
        Path.Combine(VersionDirectory(version, installRoot), ExeName);
}

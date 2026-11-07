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
}

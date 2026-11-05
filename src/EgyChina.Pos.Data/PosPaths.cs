namespace EgyChina.Pos.Data;

public static class PosPaths
{
    public static string Root()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "EgyChina", "Pos");
    }

    public static string DatabaseFile() => Path.Combine(Root(), "pos.db");

    public static string LogDirectory() => Path.Combine(Root(), "logs");
}

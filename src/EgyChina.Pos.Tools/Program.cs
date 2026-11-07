using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "restore" => RunRestore(args.Skip(1).ToArray()),
    "heartbeat-check" => RunHeartbeatCheck(args.Skip(1).ToArray()),
    "has-open-shift" => RunHasOpenShift(args.Skip(1).ToArray()),
    "import-catalog" => RunImportCatalog(args.Skip(1).ToArray()),
    _ => Fail($"Unknown command '{args[0]}'."),
};

static int RunRestore(string[] args)
{
    if (args.Length < 2)
        return Fail("Usage: restore <backup.db> <live-pos.db> [--force]");

    var backup = args[0];
    var live = args[1];
    var force = args.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));

    PosLog.SetDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(live)) ?? ".", "logs"));
    var result = DatabaseRestore.RestoreFromBackup(backup, live, requireNoOpenShift: !force);
    if (!result.Restored)
        return Fail(result.Error ?? "restore failed");

    Console.WriteLine("OK restored");
    if (result.SafetyCopyPath is not null)
        Console.WriteLine("safety=" + result.SafetyCopyPath);
    return 0;
}

static int RunHeartbeatCheck(string[] args)
{
    if (args.Length < 2)
        return Fail("Usage: heartbeat-check <data-root> <expected-version> [max-age-seconds]");

    var dataRoot = args[0];
    var version = args[1];
    var maxAgeSeconds = args.Length >= 3 && int.TryParse(args[2], out var parsed) ? parsed : 120;
    var payload = AppHeartbeat.TryRead(dataRoot);
    var ok = AppHeartbeat.MatchesVersion(payload, version, TimeSpan.FromSeconds(maxAgeSeconds));
    if (!ok)
    {
        Console.Error.WriteLine(
            payload is null
                ? "heartbeat missing"
                : $"heartbeat mismatch version={payload.Version} at={payload.WrittenAt}");
        return 2;
    }

    Console.WriteLine($"OK heartbeat version={payload!.Version} at={payload.WrittenAt}");
    return 0;
}

static int RunHasOpenShift(string[] args)
{
    if (args.Length < 1)
        return Fail("Usage: has-open-shift <pos.db>");

    var path = args[0];
    if (!File.Exists(path))
    {
        Console.WriteLine("0");
        return 0;
    }

    var open = DatabaseRestore.HasOpenShift(path);
    Console.WriteLine(open ? "1" : "0");
    return open ? 10 : 0;
}

static int RunImportCatalog(string[] args)
{
    if (args.Length < 2)
        return Fail("Usage: import-catalog <erp-products.json> <pos.db>");

    var jsonPath = Path.GetFullPath(args[0]);
    var dbPath = Path.GetFullPath(args[1]);
    if (!File.Exists(jsonPath))
        return Fail("JSON not found: " + jsonPath);

    Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
    var json = File.ReadAllText(jsonPath);
    var database = new PosDatabase(dbPath);
    database.Initialize();
    var before = database.CountProducts();
    var result = database.ApplyCatalogFromErpJson(json);
    var after = database.CountProducts();
    Console.WriteLine(
        $"OK catalog upserted={result.Upserted} deactivated={result.Deactivated} demoRemoved={result.DemoRemoved} before={before} after={after} source={database.GetSetting("catalog.source")}");
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        EgyChina.Pos.Tools
          restore <backup.db> <live-pos.db> [--force]
          heartbeat-check <data-root> <expected-version> [max-age-seconds]
          has-open-shift <pos.db>
          import-catalog <erp-products.json> <pos.db>
        """);
}

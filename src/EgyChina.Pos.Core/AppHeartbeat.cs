using System.Text.Json;

namespace EgyChina.Pos.Core;

public sealed record HeartbeatPayload(
    string Version,
    string WrittenAt,
    string ProcessId,
    string MachineName);

/// <summary>
/// Written after a successful shell start so an updater can confirm the new build boots.
/// Lives next to SQLite under the data root, not inside the versioned app folder.
/// </summary>
public static class AppHeartbeat
{
    public const string FileName = "heartbeat.json";

    public static string FilePath(string dataRoot) =>
        Path.Combine(dataRoot, FileName);

    public static void Write(string dataRoot, string version, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version is required.", nameof(version));

        Directory.CreateDirectory(dataRoot);
        var payload = new HeartbeatPayload(
            version.Trim(),
            (now ?? DateTimeOffset.UtcNow).ToString("O"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Environment.MachineName);

        var path = FilePath(dataRoot);
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temp, json);
        File.Copy(temp, path, overwrite: true);
        File.Delete(temp);
    }

    public static HeartbeatPayload? TryRead(string dataRoot)
    {
        var path = FilePath(dataRoot);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HeartbeatPayload>(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool MatchesVersion(HeartbeatPayload? payload, string expectedVersion, TimeSpan maxAge, DateTimeOffset? now = null)
    {
        if (payload is null || string.IsNullOrWhiteSpace(expectedVersion))
            return false;
        if (!string.Equals(payload.Version, expectedVersion.Trim(), StringComparison.Ordinal))
            return false;
        if (!DateTimeOffset.TryParse(payload.WrittenAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var written))
            return false;

        var age = (now ?? DateTimeOffset.UtcNow) - written;
        return age >= TimeSpan.Zero && age <= maxAge;
    }
}

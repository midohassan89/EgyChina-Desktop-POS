using System.Text.Json;

namespace EgyChina.Pos.Core;

/// <summary>
/// Public update feed settings. No secrets — HTTPS read-only Velopack channel folders.
/// Precedence: <c>EGYCHINA_POS_UPDATE_URL</c> → channel from env/file → default stable.
/// </summary>
public sealed class PosUpdateSettings
{
    public const string UpdateUrlVariable = "EGYCHINA_POS_UPDATE_URL";
    public const string UpdateChannelVariable = "EGYCHINA_POS_UPDATE_CHANNEL";
    public const string SettingsFileName = "update.settings.json";
    public const string DefaultChannel = "stable";
    public const string BetaChannel = "beta";
    public const string DefaultUpdatesOrigin = "https://updates.echinamart.com/";

    public string Channel { get; init; } = DefaultChannel;

    /// <summary>Absolute feed URL ending with / (channel folder).</summary>
    public Uri FeedUrl { get; init; } = BuildFeedUrl(DefaultChannel);

    public string SettingsPath { get; init; } = string.Empty;

    public static PosUpdateSettings Load(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Directory.CreateDirectory(dataRoot);
        var settingsPath = Path.Combine(dataRoot, SettingsFileName);

        string? fileChannel = null;
        try
        {
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("channel", out var channelNode)
                    && channelNode.ValueKind == JsonValueKind.String)
                {
                    fileChannel = channelNode.GetString();
                }
            }
        }
        catch (Exception)
        {
            // Keep defaults; PosUpdateService logs when it starts.
        }

        var urlRaw = Environment.GetEnvironmentVariable(UpdateUrlVariable)?.Trim();
        if (!string.IsNullOrWhiteSpace(urlRaw)
            && Uri.TryCreate(urlRaw, UriKind.Absolute, out var explicitUrl)
            && (explicitUrl.Scheme == Uri.UriSchemeHttps || explicitUrl.Scheme == Uri.UriSchemeHttp))
        {
            var normalized = NormalizeFeedUrl(explicitUrl);
            var channelFromUrl = GuessChannelFromUrl(normalized) ?? NormalizeChannel(fileChannel) ?? DefaultChannel;
            return new PosUpdateSettings
            {
                Channel = channelFromUrl,
                FeedUrl = normalized,
                SettingsPath = settingsPath,
            };
        }

        var channel = NormalizeChannel(Environment.GetEnvironmentVariable(UpdateChannelVariable))
            ?? NormalizeChannel(fileChannel)
            ?? DefaultChannel;

        return new PosUpdateSettings
        {
            Channel = channel,
            FeedUrl = BuildFeedUrl(channel),
            SettingsPath = settingsPath,
        };
    }

    public static void WriteChannel(string dataRoot, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalized = NormalizeChannel(channel) ?? DefaultChannel;
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, SettingsFileName);
        var json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["channel"] = normalized },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static Uri BuildFeedUrl(string channel)
    {
        var safe = NormalizeChannel(channel) ?? DefaultChannel;
        var origin = DefaultUpdatesOrigin.TrimEnd('/') + "/";
        return new Uri(new Uri(origin, UriKind.Absolute), safe + "/");
    }

    public static string? NormalizeChannel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var value = raw.Trim().ToLowerInvariant();
        return value is DefaultChannel or BetaChannel ? value : null;
    }

    private static Uri NormalizeFeedUrl(Uri url)
    {
        var text = url.ToString();
        if (!text.EndsWith('/'))
            text += "/";
        return new Uri(text, UriKind.Absolute);
    }

    private static string? GuessChannelFromUrl(Uri feed)
    {
        var parts = feed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        return NormalizeChannel(parts[^1]);
    }
}

using System.Text.RegularExpressions;

namespace EgyChina.Pos.Data;

/// <summary>
/// Strips credentials from log lines before they hit disk. Never log PIN or device bearer.
/// </summary>
public static partial class LogRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var value = text.Replace('\r', ' ').Replace('\n', ' ');
        value = Bearer().Replace(value, "Bearer [REDACTED]");
        value = AuthorizationHeader().Replace(value, "Authorization: [REDACTED]");
        value = EnvToken().Replace(value, "$1=[REDACTED]");
        value = NamedSecret().Replace(value, "$1=[REDACTED]");
        value = LongBase64().Replace(value, "[REDACTED_BLOB]");
        return value;
    }

    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"Authorization\s*:\s*\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(
        @"(EGYCHINA_POS_DEVICE_TOKEN|EGYCHINA_POS_COOKIE|DEVICE_TOKEN)\s*=\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnvToken();

    [GeneratedRegex(
        @"\b(token|password|passwd|pin|secret|cookie|authorization)\s*[=:]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"\b[A-Za-z0-9+/]{40,}={0,2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongBase64();
}

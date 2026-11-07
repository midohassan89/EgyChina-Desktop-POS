using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Silent till enrollment + helpers. Staff PINs sync via <see cref="LiveStaffSync"/> (no manager PIN UI).
/// </summary>
public static class DeviceEnrollmentClient
{
    public static async Task<DeviceEnrollResult> AutoEnrollAsync(
        Uri erpOrigin,
        string terminalKey,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(terminalKey))
            return new DeviceEnrollResult(false, null, "معرّف الجهاز غير موجود.");

        var existing = DeviceCredentialStore.Load();
        if (!string.IsNullOrWhiteSpace(existing))
            return new DeviceEnrollResult(true, existing, null);

        var origin = erpOrigin.AbsoluteUri.TrimEnd('/') + "/";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1-auto-enroll");

        var payload = new
        {
            terminalKey = terminalKey.Trim(),
            storeCode = "OBOUR",
            displayName = string.IsNullOrWhiteSpace(displayName) ? "EgyChina Desktop POS" : displayName.Trim(),
        };

        try
        {
            using var response = await http.PostAsJsonAsync(
                origin + "api/pos/device/auto-enroll",
                payload,
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var err = TryReadError(body) ?? $"HTTP {(int)response.StatusCode}";
                return new DeviceEnrollResult(false, null, err);
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("token", out var tokenNode)
                || tokenNode.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tokenNode.GetString()))
            {
                return new DeviceEnrollResult(false, null, "لم يُرجع السيرفر رمز الجهاز.");
            }

            var token = tokenNode.GetString()!.Trim();
            DeviceCredentialStore.Save(token);
            return new DeviceEnrollResult(true, token, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new DeviceEnrollResult(false, null, ex.Message);
        }
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}

public sealed record DeviceEnrollResult(bool Ok, string? Token, string? Error);

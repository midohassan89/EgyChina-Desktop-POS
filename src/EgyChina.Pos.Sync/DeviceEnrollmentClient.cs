using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// One-time till enrollment via manager PIN, plus staff PIN pull for offline login.
/// </summary>
public static class DeviceEnrollmentClient
{
    public static async Task<DeviceEnrollResult> EnrollWithPinAsync(
        Uri erpOrigin,
        string terminalKey,
        string managerPin,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(terminalKey))
            return new DeviceEnrollResult(false, null, "معرّف الجهاز غير موجود.");
        if (!PinHasherLooksValid(managerPin))
            return new DeviceEnrollResult(false, null, "أدخل رقم PIN للمدير (4 أرقام على الأقل).");

        var origin = erpOrigin.AbsoluteUri.TrimEnd('/') + "/";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1-enroll");

        var payload = new
        {
            terminalKey = terminalKey.Trim(),
            pin = managerPin.Trim(),
            storeCode = "OBOUR",
            displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
        };

        try
        {
            using var response = await http.PostAsJsonAsync(
                origin + "api/pos/device/enroll-with-pin",
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

    public static async Task<(int Upserted, string? Error)> SyncStaffAsync(
        PosDatabase database,
        Uri erpOrigin,
        string deviceToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            return (0, "الجهاز غير مربوط.");

        var origin = erpOrigin.AbsoluteUri.TrimEnd('/') + "/";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EgyChina-POS/0.1-staff");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken.Trim());

        try
        {
            using var response = await http.GetAsync(origin + "api/pos/staff", cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (0, TryReadError(body) ?? $"HTTP {(int)response.StatusCode}");

            var parsed = JsonSerializer.Deserialize<StaffResponse>(body);
            if (parsed?.Staff is null || parsed.Staff.Count == 0)
                return (0, "لا يوجد موظفون لديهم PIN في المنظومة.");

            var drafts = new List<ErpStaffDraft>();
            foreach (var row in parsed.Staff)
            {
                if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Username))
                    continue;
                if (string.IsNullOrWhiteSpace(row.PinCode))
                    continue;
                drafts.Add(new ErpStaffDraft(
                    row.Id.Trim(),
                    row.Username.Trim(),
                    row.Role ?? "CASHIER",
                    row.PinCode.Trim(),
                    string.Equals(row.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)));
            }

            var upserted = database.UpsertErpStaff(drafts);
            return (upserted, upserted > 0 ? null : "تعذر حفظ الموظفين محلياً.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return (0, ex.Message);
        }
    }

    private static bool PinHasherLooksValid(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < 4 || pin.Length > 12)
            return false;
        for (var i = 0; i < pin.Length; i++)
        {
            if (!char.IsAsciiDigit(pin[i]))
                return false;
        }

        return true;
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

    private sealed class StaffResponse
    {
        [JsonPropertyName("staff")]
        public List<StaffRow>? Staff { get; set; }
    }

    private sealed class StaffRow
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("pinCode")]
        public string? PinCode { get; set; }
    }
}

public sealed record DeviceEnrollResult(bool Ok, string? Token, string? Error);

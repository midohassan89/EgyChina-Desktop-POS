using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Pulls ERP staff PINs from <c>GET /api/store/pos-staff</c> into the local cashier table
/// (same pattern as live product catalog — no manager PIN dialog).
/// </summary>
public static class LiveStaffSync
{
    public static async Task<LiveStaffSyncResult> ApplyAsync(
        PosDatabase database,
        Uri erpOrigin,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var origin = erpOrigin.AbsoluteUri.TrimEnd('/') + "/";
        using var response = await http.GetAsync(origin + "api/store/pos-staff", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new LiveStaffSyncResult(
                false,
                0,
                0,
                $"http-{(int)response.StatusCode}");
        }

        var body = await response.Content.ReadFromJsonAsync<StaffCatalogResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (body?.Staff is null)
            return new LiveStaffSyncResult(false, 0, 0, "empty-staff");

        var drafts = new List<ErpStaffDraft>();
        var keepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in body.Staff)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Username))
                continue;
            if (string.IsNullOrWhiteSpace(row.PinCode) || !PinHasher.IsValidFormat(row.PinCode))
                continue;

            var active = string.Equals(row.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
            keepIds.Add(row.Id.Trim());
            drafts.Add(new ErpStaffDraft(
                row.Id.Trim(),
                row.Username.Trim(),
                row.Role ?? "CASHIER",
                row.PinCode.Trim(),
                active));
        }

        if (drafts.Count == 0)
            return new LiveStaffSyncResult(false, 0, 0, "no-staff-with-pin");

        var upserted = database.UpsertErpStaff(drafts);
        var deactivated = database.DeactivateCashiersNotIn(keepIds);
        return new LiveStaffSyncResult(true, upserted, deactivated, null);
    }

    private sealed class StaffCatalogResponse
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

public sealed record LiveStaffSyncResult(bool Ok, int Upserted, int Deactivated, string? Error);

using System.Globalization;
using System.Text.Json;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Pushes local shift open/close to ERP so dashboard Z-reports and treasury stay complete.
/// Requires an enrolled device Bearer (same as protected <c>/api/checkout</c>).
/// </summary>
public static class ShiftErpSync
{
    public static async Task<ShiftErpSyncResult> PushPendingAsync(
        PosDatabase database,
        Uri erpOrigin,
        string? deviceBearerToken,
        CancellationToken cancellationToken)
    {
        // Store shift routes do not require Bearer; token is optional for future protected paths.
        var pending = database.ListShiftsNeedingErpSync(20);
        if (pending.Count == 0)
            return new ShiftErpSyncResult(true, 0, 0, null);

        using var client = new ErpPosClient(erpOrigin, deviceBearerToken);
        var opened = 0;
        var closed = 0;

        foreach (var row in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shift = row;
            try
            {
                if (string.IsNullOrWhiteSpace(shift.ServerId))
                {
                    var bound = await EnsureOpenedAsync(database, client, shift, cancellationToken)
                        .ConfigureAwait(false);
                    if (bound is null)
                        continue;
                    opened++;
                    shift = bound;
                }

                if (string.Equals(shift.Status, "closed", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(shift.SyncStatus, "synced", StringComparison.OrdinalIgnoreCase))
                {
                    if (await CloseOnErpAsync(database, client, shift, cancellationToken).ConfigureAwait(false))
                        closed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLog.Error($"shift erp sync failed id={shift.Id}", ex);
                database.MarkShiftSyncStatus(shift.Id, "error");
            }
        }

        return new ShiftErpSyncResult(true, opened, closed, null);
    }

    /// <summary>Best-effort immediate open after local OpenShift (UI path).</summary>
    public static async Task<bool> TryPushOpenNowAsync(
        PosDatabase database,
        Uri? erpOrigin,
        string? deviceBearerToken,
        ShiftRecord localShift,
        CancellationToken cancellationToken)
    {
        if (erpOrigin is null)
            return false;
        using var client = new ErpPosClient(erpOrigin, deviceBearerToken);
        var bound = await EnsureOpenedAsync(database, client, localShift, cancellationToken).ConfigureAwait(false);
        return bound?.ServerId is not null;
    }

    /// <summary>Best-effort immediate close after local CloseShift (UI path).</summary>
    public static async Task<bool> TryPushCloseNowAsync(
        PosDatabase database,
        Uri? erpOrigin,
        string? deviceBearerToken,
        ShiftRecord localShift,
        CancellationToken cancellationToken)
    {
        if (erpOrigin is null)
            return false;
        using var client = new ErpPosClient(erpOrigin, deviceBearerToken);
        var bound = localShift;
        if (string.IsNullOrWhiteSpace(bound.ServerId))
        {
            var opened = await EnsureOpenedAsync(database, client, bound, cancellationToken).ConfigureAwait(false);
            if (opened is null)
                return false;
            bound = opened with
            {
                Status = "closed",
                ClosingCashMinor = localShift.ClosingCashMinor,
                ClosedAt = localShift.ClosedAt,
                SyncStatus = "pending",
            };
        }

        return await CloseOnErpAsync(database, client, bound, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ShiftRecord?> EnsureOpenedAsync(
        PosDatabase database,
        ErpPosClient client,
        ShiftRecord localShift,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(localShift.ServerId))
            return localShift;

        var startingMajor = localShift.OpeningFloatMinor / 100m;
        var body = JsonSerializer.Serialize(new
        {
            startingCash = startingMajor,
            cashierUserId = localShift.CashierId,
        });

        var result = await client.PostShiftOpenAsync(body, cancellationToken).ConfigureAwait(false);
        if (result.Accepted || result.StatusCode == 409)
        {
            var serverId = TryReadShiftId(result.Body);
            if (string.IsNullOrWhiteSpace(serverId))
            {
                database.MarkShiftSyncStatus(localShift.Id, "error");
                return null;
            }

            var syncStatus = string.Equals(localShift.Status, "closed", StringComparison.OrdinalIgnoreCase)
                ? "pending"
                : "synced";
            database.BindShiftServerId(localShift.Id, serverId, syncStatus);
            PosLog.Info($"shift opened on ERP local={localShift.Id} server={serverId}");
            return localShift with { ServerId = serverId, SyncStatus = syncStatus };
        }

        PosLog.Info($"shift open rejected local={localShift.Id} status={result.StatusCode} err={result.Error}");
        // Keep retrying after ERP deploy / brief outages (do not stick on permanent error).
        database.MarkShiftSyncStatus(
            localShift.Id,
            result.StatusCode is 401 or 404 or 502 or 503 ? "pending" : "error");
        return null;
    }

    private static async Task<bool> CloseOnErpAsync(
        PosDatabase database,
        ErpPosClient client,
        ShiftRecord localShift,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(localShift.ServerId))
            return false;

        var actualMajor = (localShift.ClosingCashMinor ?? localShift.OpeningFloatMinor) / 100m;
        long.TryParse(localShift.ServerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shiftIdNum);
        var payload = new Dictionary<string, object?>
        {
            ["actualCash"] = actualMajor,
            ["cashierUserId"] = localShift.CashierId,
        };
        if (shiftIdNum > 0)
            payload["shiftId"] = shiftIdNum;

        var json = JsonSerializer.Serialize(payload);
        var result = await client.PostShiftCloseAsync(json, cancellationToken).ConfigureAwait(false);
        if (result.Accepted || result.StatusCode == 404)
        {
            // 404 = already closed / missing on server — treat local as synced to avoid loops.
            database.BindShiftServerId(localShift.Id, localShift.ServerId!, "synced");
            PosLog.Info($"shift closed on ERP local={localShift.Id} server={localShift.ServerId} http={result.StatusCode}");
            return result.Accepted;
        }

        PosLog.Info($"shift close rejected local={localShift.Id} status={result.StatusCode} err={result.Error}");
        database.MarkShiftSyncStatus(localShift.Id, "error");
        return false;
    }

    private static string? TryReadShiftId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("shift", out var shift)
                || shift.ValueKind != JsonValueKind.Object)
                return null;
            if (!shift.TryGetProperty("id", out var idNode))
                return null;
            return idNode.ValueKind switch
            {
                JsonValueKind.Number when idNode.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.String => idNode.GetString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ShiftErpSyncResult(bool Ok, int Opened, int Closed, string? Error);

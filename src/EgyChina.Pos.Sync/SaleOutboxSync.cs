using System.Globalization;
using System.Text.Json;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Sends pending cash sales to <c>POST /api/checkout</c>.
/// Inflight rows are recovered on each run. Transient failures stay pending with backoff.
/// Poison payloads become <c>dead</c> without deleting the local sale.
/// </summary>
public sealed class SaleOutboxSync
{
    private static readonly HashSet<string> PoisonBlocks = new(StringComparer.Ordinal)
    {
        "payload-missing-sale-id",
        "payload-missing-total",
        "payload-missing-lines",
        "line-missing-product-id",
        "line-missing-name",
        "qty-not-whole",
        "line-missing-price",
        "line-missing-line-total",
        "line-total-mismatch",
    };

    private readonly PosDatabase _database;
    private readonly ErpPosClient _client;
    private readonly SyncOptions _options;
    private readonly bool _postSales;

    public SaleOutboxSync(PosDatabase database, ErpPosClient client, SyncOptions options, bool postSales = true)
    {
        _database = database;
        _client = client;
        _options = options;
        _postSales = postSales;
    }

    public static bool IsPoisonBlock(string? block) =>
        !string.IsNullOrWhiteSpace(block) && PoisonBlocks.Contains(block);

    public async Task<SyncTickResult> RunAsync(CancellationToken cancellationToken)
    {
        var recovered = _database.RecoverInflightOutbox();
        if (_options.CatalogPull)
        {
            var since = _database.ReadCursor(PosDatabase.CatalogCursorStream)?.Cursor;
            var catalog = await _client.GetProductsAsync(since, cancellationToken).ConfigureAwait(false);
            if (!catalog.ResponseCompleted)
                return Stop("transport", catalog.Error ?? "catalog transport failed", posted: 0, accepted: 0, recovered);
            if (catalog.Unauthorized)
                return Stop("unauthorized", "401", posted: 0, accepted: 0, recovered);
            if (!catalog.Accepted || catalog.Body is null)
                return Stop("catalog-rejected", catalog.Error ?? "catalog rejected", posted: 0, accepted: 0, recovered);

            var applied = _database.ApplyCatalogFromErpJson(catalog.Body);
            if (!string.IsNullOrWhiteSpace(applied.ServerTime))
                _database.WriteCursor(PosDatabase.CatalogCursorStream, applied.ServerTime, null);
            else
                _database.WriteCursor(PosDatabase.CatalogCursorStream, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), null);
        }

        if (!_postSales)
        {
            var catalogOnlySnap = _database.ReadOutboxSnapshot();
            return new SyncTickResult(
                Code: "idle",
                Detail: "catalog-only",
                Posted: 0,
                Accepted: 0,
                Pending: catalogOnlySnap.Pending + catalogOnlySnap.Inflight,
                Recovered: recovered,
                MarkedDead: 0,
                Snapshot: catalogOnlySnap);
        }

        var rows = _database.ListPendingSales(20);
        var posted = 0;
        var accepted = 0;
        var dead = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ErpCheckoutMapper.TryMap(row.PayloadJson, _options.MoneyScale, _options.PaymentMethod, _database.TryGetErpNumericId, out var template, out var block))
            {
                var reason = block ?? "blocked";
                if (IsPoisonBlock(reason))
                {
                    _database.MarkOutboxDead(row.Id, reason);
                    dead++;
                }
                else
                {
                    // e.g. product-id-not-numeric — retry after catalog link; sale kept.
                    _database.RecordOutboxFailure(row.Id, reason);
                }

                continue;
            }

            var json = WithCreatedAt(template, ReadCreatedAt(row.PayloadJson, row.AggregateId));
            if (!_database.MarkOutboxInflight(row.Id))
                continue;

            posted++;
            ErpCallResult result;
            try
            {
                result = await _client.PostCheckoutAsync(json, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _database.RecordOutboxFailure(row.Id, "cancelled");
                throw;
            }

            if (result.Accepted)
            {
                _database.MarkSaleAccepted(row.Id, row.AggregateId);
                accepted++;
                continue;
            }

            var error = result.Unauthorized
                ? "unauthorized"
                : result.TransportFailed
                    ? "transport:" + (result.Error ?? "failed")
                    : "http-" + result.StatusCode + ":" + (result.Error ?? "rejected");
            _database.RecordOutboxFailure(row.Id, error);
            if (result.Unauthorized)
                return Stop("unauthorized", error, posted, accepted, recovered, dead);
            if (result.TransportFailed)
                return Stop("transport", error, posted, accepted, recovered, dead);
        }

        var snapshot = _database.ReadOutboxSnapshot();
        var detail = accepted > 0
            ? "accepted"
            : dead > 0
                ? "dead:" + dead
                : snapshot.Pending + snapshot.Inflight > 0
                    ? "pending"
                    : "idle";
        return new SyncTickResult("idle", detail, posted, accepted, snapshot.Pending + snapshot.Inflight, recovered, dead, snapshot);
    }

    private static SyncTickResult Stop(
        string code,
        string error,
        int posted,
        int accepted,
        int recovered,
        int dead = 0)
    {
        return new SyncTickResult(code, error, posted, accepted, -1, recovered, dead, null);
    }

    private static string WithCreatedAt(string json, string createdAt)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("createdAt"))
                    writer.WriteString("createdAt", createdAt);
                else
                    property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private string ReadCreatedAt(string payloadJson, string saleId)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.TryGetProperty("createdAt", out var created) && created.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(created.GetString()))
            return created.GetString()!;
        return _database.GetSaleCompletedAt(saleId)
            ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }
}

public sealed record SyncTickResult(
    string Code,
    string Detail,
    int Posted,
    int Accepted,
    int Pending,
    int Recovered = 0,
    int MarkedDead = 0,
    OutboxSnapshot? Snapshot = null);

using System.Globalization;
using System.Text.Json;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Sends pending cash sales to the live <c>POST /api/checkout</c> route.
/// The row stays pending unless a full 2xx response arrives. Receipt amounts in SQLite are not edited.
/// </summary>
public sealed class SaleOutboxSync
{
    private readonly PosDatabase _database;
    private readonly ErpPosClient _client;
    private readonly SyncOptions _options;

    public SaleOutboxSync(PosDatabase database, ErpPosClient client, SyncOptions options)
    {
        _database = database;
        _client = client;
        _options = options;
    }

    public async Task<SyncTickResult> RunAsync(CancellationToken cancellationToken)
    {
        _database.RecoverInflightOutbox();
        if (_options.CatalogPull)
        {
            var catalog = await _client.GetProductsAsync(null, cancellationToken).ConfigureAwait(false);
            if (!catalog.ResponseCompleted)
                return Stop("transport", catalog.Error ?? "catalog transport failed", posted: 0, accepted: 0);
            if (catalog.Unauthorized)
                return Stop("unauthorized", "401", posted: 0, accepted: 0);
            if (!catalog.Accepted || catalog.Body is null)
                return Stop("catalog-rejected", catalog.Error ?? "catalog rejected", posted: 0, accepted: 0);
            _database.LinkCatalogFromErpJson(catalog.Body);
        }

        var rows = _database.ListPendingSales(20);
        var posted = 0;
        var accepted = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ErpCheckoutMapper.TryMap(row.PayloadJson, _options.MoneyScale, _options.PaymentMethod, _database.TryGetErpNumericId, out var template, out var block))
            {
                _database.RecordOutboxFailure(row.Id, block ?? "blocked");
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
                return Stop("unauthorized", error, posted, accepted);
            if (result.TransportFailed)
                return Stop("transport", error, posted, accepted);
        }

        var pending = _database.CountPendingOutbox();
        return new SyncTickResult("idle", accepted > 0 ? "accepted" : "pending", posted, accepted, pending);
    }

    private static SyncTickResult Stop(string code, string error, int posted, int accepted) =>
        new(code, error, posted, accepted, -1);

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

public sealed record SyncTickResult(string Code, string Detail, int Posted, int Accepted, int Pending);

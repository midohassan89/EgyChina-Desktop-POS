using System.Globalization;
using System.Text.Json;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Device-Bearer calls to existing ERP POS routes for queue tickets and loyalty lookup.
/// </summary>
public sealed class PosErpGateway : IPosErpGateway, IDisposable
{
    private readonly ErpPosClient _client;
    private readonly bool _ownsClient;

    public PosErpGateway(Uri baseAddress, string deviceBearerToken, HttpClient? httpClient = null)
    {
        _client = new ErpPosClient(baseAddress, deviceBearerToken, httpClient);
        _ownsClient = httpClient is null;
    }

    public bool IsConfigured => true;

    public async Task<QueueTicketClaimResult> ClaimQueueTicketAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = QueueTicketCodes.Normalize(code);
        if (!QueueTicketCodes.IsQueueCode(normalized))
        {
            return new QueueTicketClaimResult(
                false,
                null,
                "كود القسيمة غير صالح (المتوقع مثل Q-1001)",
                400);
        }

        var result = await _client.GetQueueTicketAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (!result.ResponseCompleted)
            return new QueueTicketClaimResult(false, null, result.Error ?? "تعذر الاتصال بالمنظومة.", null);
        if (!result.Accepted)
        {
            return new QueueTicketClaimResult(
                false,
                null,
                result.Error ?? "تعذر سحب القسيمة.",
                result.StatusCode);
        }

        if (!TryParseTicket(result.Body, out var ticket, out var parseError))
            return new QueueTicketClaimResult(false, null, parseError, result.StatusCode);

        return new QueueTicketClaimResult(true, ticket, null, result.StatusCode);
    }

    public async Task<bool> CompleteQueueTicketAsync(string code, string? saleId, CancellationToken cancellationToken)
    {
        var normalized = QueueTicketCodes.Normalize(code);
        if (!QueueTicketCodes.IsQueueCode(normalized))
            return false;

        var json = JsonSerializer.Serialize(new { status = "COMPLETED", saleId });
        var result = await _client.PatchQueueTicketAsync(normalized, json, cancellationToken).ConfigureAwait(false);
        return result.Accepted;
    }

    public async Task<(LoyaltyCustomer? Customer, string? Error)> LookupLoyaltyPhoneAsync(
        string phone,
        CancellationToken cancellationToken)
    {
        var normalized = LoyaltyScan.NormalizePhone(phone);
        if (!LoyaltyScan.IsLoyaltyPhone(normalized))
            return (null, "رقم الولاء غير صالح.");

        var result = await _client.GetLoyaltyCustomerAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (!result.ResponseCompleted)
            return (null, result.Error ?? "تعذر الاتصال بالمنظومة.");
        if (result.StatusCode == 404)
            return (null, "عميل غير موجود");
        if (!result.Accepted)
            return (null, result.Error ?? "تعذر البحث عن العميل.");

        try
        {
            using var doc = JsonDocument.Parse(result.Body ?? "{}");
            if (!doc.RootElement.TryGetProperty("customer", out var node) || node.ValueKind != JsonValueKind.Object)
                return (null, "عميل غير موجود");

            var id = node.GetProperty("id").GetString() ?? "";
            var name = node.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : null;
            var phoneValue = node.TryGetProperty("phone", out var phoneNode) && phoneNode.ValueKind == JsonValueKind.String
                ? phoneNode.GetString() ?? normalized
                : normalized;
            var points = node.TryGetProperty("pointsBalance", out var pts) && pts.TryGetInt32(out var balance)
                ? balance
                : 0;
            if (string.IsNullOrWhiteSpace(id))
                return (null, "عميل غير موجود");
            return (new LoyaltyCustomer(id, name, phoneValue, points), null);
        }
        catch (JsonException)
        {
            return (null, "رد غير صالح من المنظومة.");
        }
    }

    public async Task<(IReadOnlyList<OnlineOrderSummary> Orders, string? Error)> ListOnlineOrdersAsync(
        CancellationToken cancellationToken)
    {
        var result = await _client.GetOnlineOrdersAsync(cancellationToken).ConfigureAwait(false);
        if (!result.ResponseCompleted)
            return (Array.Empty<OnlineOrderSummary>(), result.Error ?? "تعذر الاتصال بالمنظومة.");
        if (!result.Accepted)
            return (Array.Empty<OnlineOrderSummary>(), result.Error ?? "تعذر تحميل طلبات الأونلاين.");

        try
        {
            using var doc = JsonDocument.Parse(result.Body ?? "{}");
            var root = doc.RootElement;
            if (!root.TryGetProperty("orders", out var ordersNode) || ordersNode.ValueKind != JsonValueKind.Array)
                return (Array.Empty<OnlineOrderSummary>(), null);

            var list = new List<OnlineOrderSummary>();
            foreach (var row in ordersNode.EnumerateArray())
            {
                if (TryReadOnlineOrder(row, out var order))
                    list.Add(order);
            }

            return (list, null);
        }
        catch (JsonException)
        {
            return (Array.Empty<OnlineOrderSummary>(), "رد غير صالح من المنظومة.");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private static bool TryReadOnlineOrder(JsonElement row, out OnlineOrderSummary order)
    {
        order = null!;
        if (row.ValueKind != JsonValueKind.Object)
            return false;

        var id = row.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var number = row.TryGetProperty("orderNumber", out var num) ? num.GetString() ?? id : id;
        var name = row.TryGetProperty("customerName", out var cn) ? cn.GetString() ?? "" : "";
        var phone = row.TryGetProperty("phone", out var ph) ? ph.GetString() ?? "" : "";
        var address = row.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.String
            ? ad.GetString()
            : null;
        var notes = row.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.String
            ? nt.GetString()
            : null;
        var status = row.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        var created = row.TryGetProperty("createdAt", out var ca) ? ca.GetString() ?? "" : "";
        var driver = row.TryGetProperty("assignedDriverName", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString()
            : null;
        decimal total = 0;
        if (row.TryGetProperty("totalAmount", out var tot))
        {
            if (!tot.TryGetDecimal(out total)
                && tot.ValueKind == JsonValueKind.String
                && !decimal.TryParse(tot.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out total))
            {
                total = 0;
            }
        }

        var items = new List<OnlineOrderItem>();
        if (row.TryGetProperty("items", out var itemsNode) && itemsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsNode.EnumerateArray())
            {
                var itemId = item.TryGetProperty("id", out var iid) ? iid.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                var itemName = item.TryGetProperty("name", out var inm) ? inm.GetString() ?? "صنف" : "صنف";
                var qty = 1m;
                if (item.TryGetProperty("quantity", out var qn))
                    _ = qn.TryGetDecimal(out qty);
                var price = 0m;
                if (item.TryGetProperty("price", out var pn))
                    _ = pn.TryGetDecimal(out price);
                items.Add(new OnlineOrderItem(itemId, itemName, qty, price));
            }
        }

        order = new OnlineOrderSummary(
            id,
            number,
            name,
            phone,
            address,
            notes,
            total,
            status,
            created,
            driver,
            items);
        return true;
    }

    public static IPosErpGateway FromOptions(SyncOptions options)
    {
        if (options.BaseAddress is null || string.IsNullOrWhiteSpace(options.DeviceToken))
            return NullPosErpGateway.Instance;
        return new PosErpGateway(options.BaseAddress, options.DeviceToken);
    }

    private static bool TryParseTicket(string? body, out QueueTicketSnapshot ticket, out string error)
    {
        ticket = null!;
        error = "رد غير صالح من المنظومة.";
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ticket", out var node) || node.ValueKind != JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    error = err.GetString() ?? error;
                else if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    error = msg.GetString() ?? error;
                return false;
            }

            var code = node.GetProperty("code").GetString() ?? "";
            var status = node.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            var total = node.TryGetProperty("total", out var tot) && tot.TryGetDecimal(out var totalDec)
                ? totalDec
                : 0m;
            string? customerId = null;
            string? customerPhone = null;
            string? customerName = null;
            if (node.TryGetProperty("customerId", out var cid) && cid.ValueKind == JsonValueKind.String)
                customerId = cid.GetString();
            if (node.TryGetProperty("customerPhone", out var cph) && cph.ValueKind == JsonValueKind.String)
                customerPhone = cph.GetString();
            if (node.TryGetProperty("customerName", out var cnm) && cnm.ValueKind == JsonValueKind.String)
                customerName = cnm.GetString();

            var items = new List<QueueTicketItem>();
            if (node.TryGetProperty("items", out var itemsNode) && itemsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in itemsNode.EnumerateArray())
                {
                    if (TryReadItem(row, out var item))
                        items.Add(item);
                }
            }

            ticket = new QueueTicketSnapshot(
                code,
                status,
                total,
                customerId,
                customerPhone,
                customerName,
                items);
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadItem(JsonElement row, out QueueTicketItem item)
    {
        item = null!;
        if (row.ValueKind != JsonValueKind.Object)
            return false;

        var productId = row.TryGetProperty("productId", out var pid) ? pid.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(productId) && row.TryGetProperty("id", out var idNode))
            productId = idNode.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(productId))
            return false;

        var name = row.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
            ? nameNode.GetString() ?? "صنف"
            : "صنف";
        var nameZh = row.TryGetProperty("nameZh", out var zh) && zh.ValueKind == JsonValueKind.String
            ? zh.GetString()
            : null;
        var barcode = row.TryGetProperty("barcode", out var bc) && bc.ValueKind == JsonValueKind.String
            ? bc.GetString()
            : null;
        var packageId = row.TryGetProperty("packageId", out var pkg) && pkg.ValueKind == JsonValueKind.String
            ? pkg.GetString()
            : null;
        var qty = ReadDecimal(row, "quantity", "qty");
        var unit = ReadDecimal(row, "unitPrice", "salePrice", "price");
        var weighted = row.TryGetProperty("isWeighted", out var w) && w.ValueKind == JsonValueKind.True;
        if (qty <= 0)
            return false;

        item = new QueueTicketItem(
            productId.Trim(),
            string.IsNullOrWhiteSpace(packageId) ? null : packageId.Trim(),
            name.Trim(),
            nameZh,
            string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(),
            qty,
            unit,
            weighted);
        return true;
    }

    private static decimal ReadDecimal(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var node))
                continue;
            if (node.TryGetDecimal(out var value))
                return value;
            if (node.ValueKind == JsonValueKind.String
                && decimal.TryParse(node.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
        }

        return 0m;
    }
}

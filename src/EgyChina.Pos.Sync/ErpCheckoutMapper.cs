using System.Globalization;
using System.Text.Json;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Builds the JSON body the production browser client posts to <c>/api/checkout</c>.
/// Field names come from that client. Missing server facts stay as a block, not a guess.
/// </summary>
public static class ErpCheckoutMapper
{
    public static bool TryMap(
        string payloadJson,
        ErpMoneyScale scale,
        string? paymentMethod,
        Func<string, long?> resolveNumericId,
        out string json,
        out string? blockReason,
        Func<string, string?>? resolveShiftServerId = null)
    {
        json = string.Empty;
        blockReason = null;
        if (scale == ErpMoneyScale.Unverified)
        {
            blockReason = "money-scale-unverified";
            return false;
        }

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;

        var resolvedPayment = ResolvePaymentMethod(root, paymentMethod);
        if (string.IsNullOrWhiteSpace(resolvedPayment))
        {
            blockReason = "payment-method-unverified";
            return false;
        }

        if (!root.TryGetProperty("saleId", out var saleIdNode) || saleIdNode.ValueKind != JsonValueKind.String)
        {
            blockReason = "payload-missing-sale-id";
            return false;
        }

        if (!root.TryGetProperty("totalMinor", out var totalNode) || !totalNode.TryGetInt64(out var totalMinor))
        {
            blockReason = "payload-missing-total";
            return false;
        }

        if (!root.TryGetProperty("lines", out var linesNode) || linesNode.ValueKind != JsonValueKind.Array || linesNode.GetArrayLength() == 0)
        {
            blockReason = "payload-missing-lines";
            return false;
        }

        var lines = new List<MappedLine>();
        foreach (var line in linesNode.EnumerateArray())
        {
            if (!TryMapLine(line, scale, resolveNumericId, out var mapped, out blockReason))
                return false;
            lines.Add(mapped);
        }

        var total = Scale(totalMinor, scale);
        var isReturn = root.TryGetProperty("isReturn", out var returnNode)
            && returnNode.ValueKind == JsonValueKind.True;
        if (isReturn && total > 0)
            total = -total;
        var createdAt = root.TryGetProperty("completedAt", out _)
            ? null
            : DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        // The stored payload has no completedAt. Callers pass the sale timestamp separately when they have it.
        // createdAt stays in the body because the live client always sends it; the worker overwrites this
        // placeholder before posting when the sale row has completed_at.
        string? customerId = null;
        if (root.TryGetProperty("customerId", out var customerNode)
            && customerNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(customerNode.GetString()))
        {
            customerId = customerNode.GetString();
        }

        var pointsEarned = 0;
        if (root.TryGetProperty("pointsEarned", out var earnedNode) && earnedNode.TryGetInt32(out var earned))
            pointsEarned = Math.Max(0, earned);
        var pointsRedeemed = 0;
        if (root.TryGetProperty("pointsRedeemed", out var redeemedNode) && redeemedNode.TryGetInt32(out var redeemed))
            pointsRedeemed = Math.Max(0, redeemed);

        long? shiftId = ReadPositiveLong(root, "shiftId");
        if (shiftId is null
            && resolveShiftServerId is not null
            && root.TryGetProperty("localShiftId", out var localShiftNode)
            && localShiftNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(localShiftNode.GetString()))
        {
            var bound = resolveShiftServerId(localShiftNode.GetString()!);
            if (!string.IsNullOrWhiteSpace(bound)
                && long.TryParse(bound, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedShift)
                && parsedShift > 0)
            {
                shiftId = parsedShift;
            }
        }

        string? cashierUserId = null;
        if (root.TryGetProperty("cashierUserId", out var cashierUserNode)
            && cashierUserNode.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cashierUserNode.GetString()))
        {
            cashierUserId = cashierUserNode.GetString();
        }
        else if (root.TryGetProperty("cashierId", out var cashierNode)
                 && cashierNode.ValueKind == JsonValueKind.String
                 && !string.IsNullOrWhiteSpace(cashierNode.GetString()))
        {
            cashierUserId = cashierNode.GetString();
        }

        json = Write(
            saleIdNode.GetString()!,
            resolvedPayment.Trim(),
            total,
            createdAt ?? "",
            lines,
            isReturn,
            customerId,
            pointsEarned,
            pointsRedeemed,
            shiftId,
            cashierUserId);
        return true;
    }

    public static string Write(
        string localId,
        string paymentMethod,
        decimal total,
        string createdAt,
        IReadOnlyList<MappedLine> lines,
        bool isReturn = false,
        string? customerId = null,
        int pointsEarned = 0,
        int pointsRedeemed = 0,
        long? shiftId = null,
        string? cashierUserId = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("paymentMethod", paymentMethod);
            WriteMoney(writer, "amount", decimal.Abs(total));
            WriteMoney(writer, "total", total);
            writer.WriteBoolean("isReturn", isReturn);
            if (shiftId is > 0)
                writer.WriteNumber("shiftId", shiftId.Value);
            else
                writer.WriteNull("shiftId");
            writer.WriteString("localId", localId);
            writer.WriteString("createdAt", createdAt);
            writer.WriteNull("customerName");
            writer.WriteNull("employeeId");
            writer.WriteNull("wooOrderId");
            if (string.IsNullOrWhiteSpace(customerId))
                writer.WriteNull("customerId");
            else
                writer.WriteString("customerId", customerId);
            if (string.IsNullOrWhiteSpace(cashierUserId))
                writer.WriteNull("cashierUserId");
            else
                writer.WriteString("cashierUserId", cashierUserId.Trim());
            writer.WriteNumber("pointsEarned", Math.Max(0, pointsEarned));
            writer.WriteNumber("pointsRedeemed", Math.Max(0, pointsRedeemed));
            writer.WritePropertyName("lines");
            writer.WriteStartArray();
            foreach (var line in lines)
            {
                writer.WriteStartObject();
                writer.WriteNumber("productId", line.ProductId);
                writer.WriteString("name", line.Name);
                writer.WriteNumber("qty", line.Qty);
                WriteMoney(writer, "unitPrice", line.UnitPrice);
                WriteMoney(writer, "lineTotal", line.LineTotal);
                writer.WriteNumber("stockMultiplier", 1);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static long? ReadPositiveLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind == JsonValueKind.Null)
            return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var n) && n > 0)
            return n;
        if (node.ValueKind == JsonValueKind.String
            && long.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
            return parsed;
        return null;
    }

    public static decimal Scale(long minor, ErpMoneyScale scale) => scale switch
    {
        ErpMoneyScale.MinorUnits => minor,
        ErpMoneyScale.MajorUnits => minor / 100m,
        _ => throw new InvalidOperationException("Money scale is not verified."),
    };

    /// <summary>
    /// Prefer payload <c>paymentMethod</c>, then first <c>payments[].method</c> (legacy local ids),
    /// then the optional env override. Never invent a method when nothing is present.
    /// </summary>
    internal static string? ResolvePaymentMethod(JsonElement root, string? envFallback)
    {
        if (root.TryGetProperty("paymentMethod", out var paymentNode)
            && paymentNode.ValueKind == JsonValueKind.String)
        {
            var normalized = PosPaymentMethods.TryNormalizeErpId(paymentNode.GetString());
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        if (root.TryGetProperty("payments", out var paymentsNode)
            && paymentsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var pay in paymentsNode.EnumerateArray())
            {
                if (!pay.TryGetProperty("method", out var methodNode)
                    || methodNode.ValueKind != JsonValueKind.String)
                    continue;
                var normalized = PosPaymentMethods.TryNormalizeErpId(methodNode.GetString());
                if (!string.IsNullOrWhiteSpace(normalized))
                    return normalized;
            }
        }

        return PosPaymentMethods.TryNormalizeErpId(envFallback);
    }

    private static bool TryMapLine(
        JsonElement line,
        ErpMoneyScale scale,
        Func<string, long?> resolveNumericId,
        out MappedLine mapped,
        out string? blockReason)
    {
        mapped = default;
        blockReason = null;
        if (!line.TryGetProperty("productId", out var productNode) || productNode.ValueKind != JsonValueKind.String)
        {
            blockReason = "line-missing-product-id";
            return false;
        }

        var rawId = productNode.GetString() ?? string.Empty;
        long numericId;
        if (long.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            numericId = parsed;
        else if (resolveNumericId(rawId) is long linked && linked > 0)
            numericId = linked;
        else if (rawId.StartsWith("demo-", StringComparison.OrdinalIgnoreCase))
        {
            // Seed catalog never exists on production ERP — do not retry forever.
            blockReason = "demo-product-not-on-erp";
            return false;
        }
        else
        {
            blockReason = "product-id-not-numeric";
            return false;
        }

        if (!line.TryGetProperty("nameAr", out var nameNode) || nameNode.ValueKind != JsonValueKind.String)
        {
            blockReason = "line-missing-name";
            return false;
        }

        if (!line.TryGetProperty("qtyMilli", out var qtyNode) || !qtyNode.TryGetInt64(out var qtyMilli) || qtyMilli <= 0 || qtyMilli % 1000 != 0)
        {
            blockReason = "qty-not-whole";
            return false;
        }

        if (!line.TryGetProperty("unitPriceMinor", out var priceNode) || !priceNode.TryGetInt64(out var unitMinor))
        {
            blockReason = "line-missing-price";
            return false;
        }

        if (!line.TryGetProperty("lineTotalMinor", out var lineTotalNode) || !lineTotalNode.TryGetInt64(out var lineMinor))
        {
            blockReason = "line-missing-line-total";
            return false;
        }

        var qty = (int)(qtyMilli / 1000);
        var unit = Scale(unitMinor, scale);
        var lineTotal = Scale(lineMinor, scale);
        if (unit * qty != lineTotal)
        {
            blockReason = "line-total-mismatch";
            return false;
        }

        mapped = new MappedLine(numericId, nameNode.GetString()!, qty, unit, lineTotal);
        return true;
    }

    private static void WriteMoney(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(value.ToString("0.00", CultureInfo.InvariantCulture));
    }
}

public readonly record struct MappedLine(long ProductId, string Name, int Qty, decimal UnitPrice, decimal LineTotal);

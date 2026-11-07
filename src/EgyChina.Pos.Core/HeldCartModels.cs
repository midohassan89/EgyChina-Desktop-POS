using System.Text.Json.Serialization;

namespace EgyChina.Pos.Core;

public sealed record HeldCartSummary(
    string Id,
    string Label,
    string HeldAt,
    int ItemCount,
    long TotalMinor)
{
    public string TotalText => Money.FormatEgp(TotalMinor);

    public string Caption =>
        $"{Label} · {ItemCount} · {TotalText}";
}

public sealed record HeldCartLinePayload(
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("barcode")] string? Barcode,
    [property: JsonPropertyName("nameAr")] string NameAr,
    [property: JsonPropertyName("nameZh")] string? NameZh,
    [property: JsonPropertyName("qty")] int Qty,
    [property: JsonPropertyName("unitPriceMinor")] long UnitPriceMinor);

public sealed record HeldCartPayload(
    [property: JsonPropertyName("lines")] IReadOnlyList<HeldCartLinePayload> Lines);

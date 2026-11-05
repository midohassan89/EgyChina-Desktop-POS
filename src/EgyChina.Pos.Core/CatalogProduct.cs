namespace EgyChina.Pos.Core;

public sealed record CatalogProduct(
    string Id,
    string Sku,
    string? Barcode,
    string NameAr,
    string? NameEn,
    string? NameZh,
    long PriceEgpMinor)
{
    public string PriceEgpText => Money.FormatEgp(PriceEgpMinor);

    public bool HasNameZh => !string.IsNullOrWhiteSpace(NameZh);
}

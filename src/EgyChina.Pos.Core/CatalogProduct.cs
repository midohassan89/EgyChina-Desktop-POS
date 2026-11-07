namespace EgyChina.Pos.Core;

public sealed record CatalogProduct(
    string Id,
    string Sku,
    string? Barcode,
    string NameAr,
    string? NameEn,
    string? NameZh,
    long PriceEgpMinor,
    bool IsFavorite = false,
    string? ImageUrl = null)
{
    public string PriceEgpText => Money.FormatEgp(PriceEgpMinor);

    public bool HasNameZh => !string.IsNullOrWhiteSpace(NameZh);

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public bool HasImageUrl => !string.IsNullOrWhiteSpace(ImageUrl);
}

public enum CatalogTab
{
    All = 0,
    Favorites = 1,
}

public sealed record PriceCheckResult(
    string Barcode,
    string NameAr,
    string? NameZh,
    string Sku,
    long PriceEgpMinor)
{
    public string PriceEgpText => Money.FormatEgp(PriceEgpMinor);

    public bool HasNameZh => !string.IsNullOrWhiteSpace(NameZh);
}

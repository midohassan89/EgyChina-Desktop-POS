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
    string? ImageUrl = null,
    bool HasLocalImage = false,
    string? CategoryId = null,
    string? CategoryNameAr = null)
{
    public string PriceEgpText => Money.FormatEgp(PriceEgpMinor);

    public string DisplayNameAr => ProductDisplayNames.ForShelf(NameAr, NameZh, Barcode ?? Sku);

    public bool HasNameZh => ProductDisplayNames.ShowSecondaryZh(NameAr, NameZh);

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public bool HasImageUrl => !string.IsNullOrWhiteSpace(ImageUrl);

    public bool HasCategory => !string.IsNullOrWhiteSpace(CategoryId);
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

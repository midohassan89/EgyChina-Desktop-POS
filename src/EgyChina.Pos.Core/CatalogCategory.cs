namespace EgyChina.Pos.Core;

public sealed record CatalogCategory(
    string Id,
    string NameAr,
    string? NameEn,
    string? NameZh,
    int ProductCount)
{
    public string ChipText =>
        ProductCount > 0
            ? $"{NameAr} ({ProductCount.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : NameAr;
}

public sealed record CatalogCategoryDraft(
    string Id,
    string NameAr,
    string? NameEn,
    string? NameZh,
    string? Slug,
    string? ImageUrl,
    int ProductCount);

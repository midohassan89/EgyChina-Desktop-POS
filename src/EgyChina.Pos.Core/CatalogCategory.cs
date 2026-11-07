namespace EgyChina.Pos.Core;

public sealed record CatalogCategory(
    string Id,
    string NameAr,
    string? NameEn,
    string? NameZh,
    int ProductCount)
{
    public string DisplayName => ProductDisplayNames.PreferLocalized(NameAr, NameEn, NameZh);

    public string ChipText
    {
        get
        {
            var label = DisplayName;
            return ProductCount > 0
                ? $"{label} ({ProductCount.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                : label;
        }
    }
}

public sealed record CatalogCategoryDraft(
    string Id,
    string NameAr,
    string? NameEn,
    string? NameZh,
    string? Slug,
    string? ImageUrl,
    int ProductCount);

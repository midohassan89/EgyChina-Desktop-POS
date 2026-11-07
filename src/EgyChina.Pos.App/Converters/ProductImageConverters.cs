using System;
using System.Globalization;
using Avalonia.Data.Converters;
using EgyChina.Pos.App.Services;
using EgyChina.Pos.Core;

namespace EgyChina.Pos.App.Converters;

public sealed class ProductImageConverter : IValueConverter
{
    public static readonly ProductImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var productId = value switch
        {
            CatalogProduct product => product.Id,
            string id => id,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(productId))
            return null;

        return ProductImageBitmapCache.Get(productId);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HasProductImageConverter : IValueConverter
{
    public static readonly HasProductImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is CatalogProduct product)
            return product.HasLocalImage || ProductImageBitmapCache.HasFile(product.Id);

        var productId = value as string;
        return !string.IsNullOrWhiteSpace(productId) && ProductImageBitmapCache.HasFile(productId);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MissingProductImageConverter : IValueConverter
{
    public static readonly MissingProductImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = HasProductImageConverter.Instance.Convert(value, targetType, parameter, culture);
        return has is false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

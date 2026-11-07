using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using EgyChina.Pos.Core;
using EgyChina.Pos.Data;

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

        var path = ProductImageCache.TryLocalPath(productId);
        if (path is null)
            return null;

        try
        {
            return new Bitmap(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HasProductImageConverter : IValueConverter
{
    public static readonly HasProductImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var productId = value switch
        {
            CatalogProduct product => product.Id,
            string id => id,
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(productId) && ProductImageCache.TryLocalPath(productId) is not null;
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

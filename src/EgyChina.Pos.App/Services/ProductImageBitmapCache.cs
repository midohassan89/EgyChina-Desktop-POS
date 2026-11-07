using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.App.Services;

/// <summary>
/// Decodes small product thumbnails and keeps a bounded in-memory cache.
/// Prevents the non-virtualized catalog grid from decoding thousands of full-size images.
/// </summary>
public static class ProductImageBitmapCache
{
    private const int MaxEntries = 128;
    /// <summary>In-memory shelf thumbs (CDN full files stay on disk; decode is bounded).</summary>
    private const int MaxEdge = 220;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Lru = new();

    public static bool HasFile(string productId) =>
        !string.IsNullOrWhiteSpace(productId) && ProductImageCache.TryLocalPath(productId) is not null;

    public static Bitmap? Get(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(productId, out var hit))
            {
                Touch(productId);
                return hit;
            }
        }

        var path = ProductImageCache.TryLocalPath(productId);
        if (path is null)
            return null;

        Bitmap? decoded = null;
        try
        {
            using var original = new Bitmap(path);
            var size = original.PixelSize;
            if (size.Width <= 0 || size.Height <= 0)
                return null;

            if (size.Width <= MaxEdge && size.Height <= MaxEdge)
            {
                decoded = new Bitmap(path);
            }
            else
            {
                var scale = Math.Min((double)MaxEdge / size.Width, (double)MaxEdge / size.Height);
                var w = Math.Max(1, (int)Math.Round(size.Width * scale));
                var h = Math.Max(1, (int)Math.Round(size.Height * scale));
                decoded = original.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.MediumQuality);
            }
        }
        catch (Exception)
        {
            decoded?.Dispose();
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(productId, out var race))
            {
                decoded.Dispose();
                Touch(productId);
                return race;
            }

            while (Cache.Count >= MaxEntries && Lru.Last is not null)
            {
                var oldKey = Lru.Last.Value;
                Lru.RemoveLast();
                if (Cache.Remove(oldKey, out var oldBitmap))
                    oldBitmap.Dispose();
            }

            Cache[productId] = decoded;
            Lru.AddFirst(productId);
            return decoded;
        }
    }

    private static void Touch(string productId)
    {
        var node = Lru.Find(productId);
        if (node is null)
            return;
        Lru.Remove(node);
        Lru.AddFirst(node);
    }
}

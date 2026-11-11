using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.App.Services;

/// <summary>
/// Decodes small product thumbnails and keeps a bounded in-memory cache.
/// Never disposes bitmaps that may still be bound to virtualized <c>Image</c> controls —
/// disposing while ItemsRepeater measures them crashes with ObjectDisposedException.
/// </summary>
public static class ProductImageBitmapCache
{
    private const int MaxEntries = 768;
    /// <summary>In-memory shelf thumbs (CDN full files stay on disk; decode is bounded).</summary>
    private const int MaxEdge = 220;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Lru = new();

    public static bool HasFile(string productId) =>
        !string.IsNullOrWhiteSpace(productId) && ProductImageCache.TryLocalPath(productId) is not null;

    public static void Invalidate(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        lock (Gate)
        {
            if (!Cache.Remove(productId, out _))
                return;
            var node = Lru.Find(productId);
            if (node is not null)
                Lru.Remove(node);
            // Intentionally do not Dispose — UI may still reference the instance.
        }
    }

    public static Bitmap? Get(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(productId, out var hit))
            {
                if (IsUsable(hit))
                {
                    Touch(productId);
                    return hit;
                }

                Cache.Remove(productId);
                var dead = Lru.Find(productId);
                if (dead is not null)
                    Lru.Remove(dead);
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
            if (Cache.TryGetValue(productId, out var race) && IsUsable(race))
            {
                decoded.Dispose();
                Touch(productId);
                return race;
            }

            while (Cache.Count >= MaxEntries && Lru.Last is not null)
            {
                var oldKey = Lru.Last.Value;
                Lru.RemoveLast();
                // Drop from cache only — do not Dispose (virtualized Image may still hold it).
                Cache.Remove(oldKey);
            }

            Cache[productId] = decoded;
            Lru.AddFirst(productId);
            return decoded;
        }
    }

    private static bool IsUsable(Bitmap bitmap)
    {
        try
        {
            var size = bitmap.PixelSize;
            return size.Width > 0 && size.Height > 0;
        }
        catch (ObjectDisposedException)
        {
            return false;
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

using SkiaSharp;

namespace EgyChina.Pos.Printing;

/// <summary>
/// Converts a 576-dot-wide Skia bitmap to ESC/POS <c>GS v 0</c> raster chunks
/// matching the mobile XP-P323B path.
/// </summary>
public static class EscPosRasterEncoder
{
    public const int PageWidthDots = 576;
    public const int WidthBytes = PageWidthDots / 8;
    public const int ChunkRows = 24;

    public static byte[] Encode(SKBitmap bitmap, bool kickDrawer = false)
    {
        if (bitmap.Width != PageWidthDots)
            throw new ArgumentException($"Receipt bitmap width must be {PageWidthDots} dots.", nameof(bitmap));

        var mono = ToMonoRows(bitmap);
        using var stream = new MemoryStream();

        // ESC @ initialize
        stream.WriteByte(0x1B);
        stream.WriteByte(0x40);
        // ESC a 0 left align
        stream.WriteByte(0x1B);
        stream.WriteByte(0x61);
        stream.WriteByte(0x00);

        var row = 0;
        while (row < mono.Count)
        {
            var rowsInChunk = Math.Min(ChunkRows, mono.Count - row);
            // GS v 0 m xL xH yL yH [data]
            stream.WriteByte(0x1D);
            stream.WriteByte(0x76);
            stream.WriteByte(0x30);
            stream.WriteByte(0x00);
            stream.WriteByte((byte)(WidthBytes & 0xFF));
            stream.WriteByte((byte)((WidthBytes >> 8) & 0xFF));
            stream.WriteByte((byte)(rowsInChunk & 0xFF));
            stream.WriteByte((byte)((rowsInChunk >> 8) & 0xFF));
            for (var r = 0; r < rowsInChunk; r++)
                stream.Write(mono[row + r], 0, WidthBytes);
            row += rowsInChunk;
        }

        // ESC d 4 feed
        stream.WriteByte(0x1B);
        stream.WriteByte(0x64);
        stream.WriteByte(0x04);
        // GS V B 0 partial cut
        stream.WriteByte(0x1D);
        stream.WriteByte(0x56);
        stream.WriteByte(0x42);
        stream.WriteByte(0x00);

        if (kickDrawer)
            WriteDrawerKick(stream);

        return stream.ToArray();
    }

    public static byte[] DrawerKickOnly()
    {
        using var stream = new MemoryStream(8);
        WriteDrawerKick(stream);
        return stream.ToArray();
    }

    private static void WriteDrawerKick(Stream stream)
    {
        // ESC p m t1 t2 — pin 2 pulse for cash drawer on printer RJ11
        stream.WriteByte(0x1B);
        stream.WriteByte(0x70);
        stream.WriteByte(0x00);
        stream.WriteByte(0x19);
        stream.WriteByte(0xFA);
    }

    private static List<byte[]> ToMonoRows(SKBitmap bitmap)
    {
        var rows = new List<byte[]>(bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = new byte[WidthBytes];
            for (var x = 0; x < PageWidthDots; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var luminance = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
                if (luminance < 160)
                {
                    var byteIndex = x / 8;
                    var bit = 7 - (x % 8);
                    row[byteIndex] |= (byte)(1 << bit);
                }
            }

            rows.Add(row);
        }

        return rows;
    }
}

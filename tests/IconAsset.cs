using System.Buffers.Binary;
using System.IO.Compression;

// Decode our non-interlaced RGBA PNG resources without invoking Unity's renderer.
internal sealed record IconAsset(int Width, int Height, byte[] Pixels)
{
    internal byte Alpha(int x, int y) => Pixels[(y * Width + x) * 4 + 3];
    internal static IconAsset Read(byte[] png)
    {
        if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || png[24] != 8 || png[25] != 6 || png[28] != 0)
            throw new Exception("Expected non-interlaced RGBA PNG");
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        using var compressed = new MemoryStream();
        for (int at = 8; at < png.Length;)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            if (System.Text.Encoding.ASCII.GetString(png, at + 4, 4) == "IDAT")
                compressed.Write(png, at + 8, size);
            at += size + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] filtered = raw.ToArray();
        int stride = width * 4;
        var pixels = new byte[stride * height];
        int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? pixels[y * stride + x - 4] : 0, up = y > 0 ? pixels[(y - 1) * stride + x] : 0, corner = y > 0 && x >= 4 ? pixels[(y - 1) * stride + x - 4] : 0;
                int prediction = filtered[y * (stride + 1)] switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => throw new Exception("Invalid PNG filter")
                };
                pixels[y * stride + x] = unchecked((byte)(filtered[y * (stride + 1) + x + 1] + prediction));
            }
        return new(width, height, pixels);
    }
}

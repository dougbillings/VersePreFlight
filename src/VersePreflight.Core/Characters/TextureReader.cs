using System.Numerics;

namespace VersePreflight.Core.Characters;

/// <summary>A decoded single-channel texture (e.g. hair opacity), 0-255 per texel.</summary>
public sealed record GrayTexture(int Width, int Height, byte[] Texels)
{
    /// <summary>Wrapped bilinear sample in 0..1.</summary>
    public float Sample(float u, float v)
    {
        u = u - MathF.Floor(u); v = v - MathF.Floor(v);
        var x = u * Width - 0.5f; var y = v * Height - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float T(int tx, int ty) => Texels[((ty % Height + Height) % Height) * Width + (tx % Width + Width) % Width];
        var top = T(x0, y0) * (1 - fx) + T(x0 + 1, y0) * fx;
        var bottom = T(x0, y0 + 1) * (1 - fx) + T(x0 + 1, y0 + 1) * fx;
        return (top * (1 - fy) + bottom * fy) / 255f;
    }
}

/// <summary>A decoded RGB colour texture (e.g. skin diffuse), sRGB bytes, 3 per texel.</summary>
public sealed record ColorTexture(int Width, int Height, byte[] Rgb)
{
    /// <summary>Wrapped bilinear sample, sRGB 0..1.</summary>
    public Vector3 Sample(float u, float v)
    {
        u = u - MathF.Floor(u); v = v - MathF.Floor(v);
        var x = u * Width - 0.5f; var y = v * Height - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector3 T(int tx, int ty)
        {
            var i = (((ty % Height + Height) % Height) * Width + (tx % Width + Width) % Width) * 3;
            return new Vector3(Rgb[i], Rgb[i + 1], Rgb[i + 2]);
        }
        var top = Vector3.Lerp(T(x0, y0), T(x0 + 1, y0), fx);
        var bottom = Vector3.Lerp(T(x0, y0 + 1), T(x0 + 1, y0 + 1), fx);
        return Vector3.Lerp(top, bottom, fy) / 255f;
    }

    /// <summary>Average colour, sRGB 0..1.</summary>
    public Vector3 Mean()
    {
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < Rgb.Length; i += 3) { r += Rgb[i]; g += Rgb[i + 1]; b += Rgb[i + 2]; }
        var n = Rgb.Length / 3.0 * 255;
        return new Vector3((float)(r / n), (float)(g / n), (float)(b / n));
    }
}

/// <summary>
/// Reads Star Citizen's split DDS textures (<c>name.dds</c> holds the header and smallest mips,
/// <c>name.dds.N</c> the larger ones, highest N = full size) and decodes BC1, BC3, BC4 and BC5.
/// </summary>
public static class TextureReader
{
    private enum Format { Bc1, Bc3, Bc4, Bc5 }

    private sealed record Level(Format Format, int Width, int Height, byte[] Data, int Offset);

    /// <summary>
    /// Reads the first channel (BC4/BC5 red, BC3 alpha) of the largest mip no bigger than <paramref name="maxSize"/>.
    /// Files are read through <paramref name="readFile"/> by lower-case path (e.g. <c>data\...\x_opac.dds</c>).
    /// </summary>
    public static GrayTexture ReadChannel(Func<string, byte[]?> readFile, string ddsPath, int maxSize = 1024)
    {
        var l = ReadLevel(readFile, ddsPath, maxSize);
        if (l.Format == Format.Bc1) throw new NotSupportedException("BC1 has no single channel to read.");
        return new GrayTexture(l.Width, l.Height, DecodeFirstChannel(l.Data.AsSpan(l.Offset), l.Width, l.Height, 0, l.Format == Format.Bc4 ? 8 : 16));
    }

    /// <summary>Reads the colour (BC1, or the colour part of BC3) of the largest mip no bigger than <paramref name="maxSize"/>.</summary>
    public static ColorTexture ReadColor(Func<string, byte[]?> readFile, string ddsPath, int maxSize = 1024)
    {
        var l = ReadLevel(readFile, ddsPath, maxSize);
        if (l.Format is not (Format.Bc1 or Format.Bc3)) throw new NotSupportedException("Only BC1/BC3 colour textures are supported.");
        return new ColorTexture(l.Width, l.Height, DecodeColor(l.Data.AsSpan(l.Offset), l.Width, l.Height, l.Format == Format.Bc1 ? 8 : 16));
    }

    private static Level ReadLevel(Func<string, byte[]?> readFile, string ddsPath, int maxSize)
    {
        var header = readFile(ddsPath) ?? throw new FileNotFoundException(ddsPath);
        if (header.Length < 128 || BitConverter.ToUInt32(header, 0) != 0x20534444) throw new InvalidDataException("Not a DDS texture.");
        var height = BitConverter.ToInt32(header, 12);
        var width = BitConverter.ToInt32(header, 16);
        var mips = Math.Max(1, BitConverter.ToInt32(header, 28));

        // Legacy FourCC header (128 bytes) or DX10 extended header (148 bytes)
        var fourCc = System.Text.Encoding.ASCII.GetString(header, 84, 4);
        Format format; int dataStart;
        if (fourCc == "DX10")
        {
            dataStart = 148;
            format = BitConverter.ToUInt32(header, 128) switch
            {
                71 or 72 => Format.Bc1,
                77 or 78 => Format.Bc3,
                80 or 81 => Format.Bc4,
                83 or 84 => Format.Bc5,
                var f => throw new NotSupportedException($"Texture format {f} isn't supported."),
            };
        }
        else
        {
            dataStart = 128;
            format = fourCc switch
            {
                "DXT1" => Format.Bc1,
                "DXT5" => Format.Bc3,
                "ATI1" or "BC4U" => Format.Bc4,
                "ATI2" or "BC5U" => Format.Bc5,
                _ => throw new NotSupportedException($"Texture format '{fourCc}' isn't supported."),
            };
        }
        var blockSize = format is Format.Bc1 or Format.Bc4 ? 8 : 16;

        // Count the split parts (.dds.1 ... .dds.N); part N holds mip 0, part N-1 mip 1, and so on
        var parts = 0;
        while (parts < 16 && readFile($"{ddsPath}.{parts + 1}") is not null) parts++;

        var mip = 0;
        while (mip < mips - 1 && (Math.Max(1, width >> mip) > maxSize || Math.Max(1, height >> mip) > maxSize)) mip++;
        int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);

        if (mip < parts) return new Level(format, w, h, readFile($"{ddsPath}.{parts - mip}")!, 0);

        // Small mips live in the header file, largest first, right after the header
        var offset = dataStart;
        for (var m = parts; m < mip; m++) offset += MipBytes(width >> m, height >> m, blockSize);
        return new Level(format, w, h, header, offset);
    }

    private static int MipBytes(int w, int h, int blockSize) => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockSize;

    /// <summary>Decodes channel R of BC4/BC5, or alpha of BC3 (all use the same 8-byte "alpha" block first).</summary>
    internal static byte[] DecodeFirstChannel(ReadOnlySpan<byte> data, int w, int h, uint format, int blockSize)
    {
        var texels = new byte[w * h];
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        Span<byte> values = stackalloc byte[8];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                var block = data.Slice((by * bw + bx) * blockSize, 8);
                DecodeAlphaBlock(block, values);
                ulong bits = 0;
                for (var i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
                for (var py = 0; py < 4; py++)
                    for (var px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        texels[y * w + x] = values[(int)(bits >> (3 * (py * 4 + px))) & 7];
                    }
            }
        _ = format;
        return texels;
    }

    /// <summary>Decodes BC1 colour blocks (or the colour half of BC3 blocks) to RGB.</summary>
    internal static byte[] DecodeColor(ReadOnlySpan<byte> data, int w, int h, int blockSize)
    {
        var rgb = new byte[w * h * 3];
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        Span<int> palette = stackalloc int[12]; // 4 colours x RGB
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                var block = data.Slice((by * bw + bx) * blockSize + (blockSize - 8), 8); // BC3: colour after alpha
                int c0 = block[0] | block[1] << 8, c1 = block[2] | block[3] << 8;
                Unpack565(c0, palette[..3]); Unpack565(c1, palette[3..6]);
                for (var k = 0; k < 3; k++)
                {
                    // BC3 colour always uses 4-colour mode; BC1 uses 3-colour mode when c0 <= c1
                    if (c0 > c1 || blockSize == 16)
                    {
                        palette[6 + k] = (2 * palette[k] + palette[3 + k]) / 3;
                        palette[9 + k] = (palette[k] + 2 * palette[3 + k]) / 3;
                    }
                    else
                    {
                        palette[6 + k] = (palette[k] + palette[3 + k]) / 2;
                        palette[9 + k] = 0;
                    }
                }
                var bits = BitConverter.ToUInt32(block[4..]);
                for (var py = 0; py < 4; py++)
                    for (var px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        var index = (int)(bits >> (2 * (py * 4 + px))) & 3;
                        var o = (y * w + x) * 3;
                        rgb[o] = (byte)palette[index * 3]; rgb[o + 1] = (byte)palette[index * 3 + 1]; rgb[o + 2] = (byte)palette[index * 3 + 2];
                    }
            }
        return rgb;
    }

    private static void Unpack565(int c, Span<int> rgb)
    {
        rgb[0] = (c >> 11 & 31) * 255 / 31;
        rgb[1] = (c >> 5 & 63) * 255 / 63;
        rgb[2] = (c & 31) * 255 / 31;
    }

    private static void DecodeAlphaBlock(ReadOnlySpan<byte> block, Span<byte> values)
    {
        int a0 = block[0], a1 = block[1];
        values[0] = (byte)a0; values[1] = (byte)a1;
        if (a0 > a1)
            for (var i = 1; i <= 6; i++) values[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
        {
            for (var i = 1; i <= 4; i++) values[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            values[6] = 0; values[7] = 255;
        }
    }
}

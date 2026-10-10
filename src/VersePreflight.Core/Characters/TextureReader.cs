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
/// A decoded four-channel texture, 4 bytes per texel. Channels follow GPU conventions: BC4 fills red only,
/// BC5 red and green (signed BC5 is stored offset, so 128 means 0).
/// </summary>
public sealed record RgbaTexture(int Width, int Height, byte[] Rgba)
{
    /// <summary>Wrapped bilinear sample, 0..1 per channel.</summary>
    public Vector4 Sample(float u, float v)
    {
        u = u - MathF.Floor(u); v = v - MathF.Floor(v);
        var x = u * Width - 0.5f; var y = v * Height - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int X(int tx) => (tx % Width + Width) % Width;
        int Y(int ty) => (ty % Height + Height) % Height;
        Vector4 T(int tx, int ty)
        {
            var i = (Y(ty) * Width + X(tx)) * 4;
            return new Vector4(Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
        }
        var top = Vector4.Lerp(T(x0, y0), T(x0 + 1, y0), fx);
        var bottom = Vector4.Lerp(T(x0, y0 + 1), T(x0 + 1, y0 + 1), fx);
        return Vector4.Lerp(top, bottom, fy) / 255f;
    }

    /// <summary>One channel as a gray texture (0 red .. 3 alpha).</summary>
    public GrayTexture Channel(int channel)
    {
        var texels = new byte[Width * Height];
        for (var i = 0; i < texels.Length; i++) texels[i] = Rgba[i * 4 + channel];
        return new GrayTexture(Width, Height, texels);
    }
}

/// <summary>
/// Reads Star Citizen's split DDS textures (<c>name.dds</c> holds the header and smallest mips,
/// <c>name.dds.N</c> the larger ones, highest N = full size) and decodes BC1, BC3, BC4, BC5, BC7 and
/// uncompressed 32-bit textures. Gloss maps are stored beside normal maps as <c>name.dds.a</c> (a header
/// without the "DDS " magic) with parts <c>name.dds.Na</c>.
/// </summary>
public static class TextureReader
{
    private enum Format { Bc1, Bc3, Bc4, Bc5, Bc5Signed, Bc7, Rgba8 }

    private sealed record Level(Format Format, int Width, int Height, byte[] Data, int Offset, uint[] Masks);

    /// <summary>
    /// Reads the first channel (BC4/BC5 red, BC3 alpha, otherwise red) of the largest mip no bigger than
    /// <paramref name="maxSize"/>. Files are read through <paramref name="readFile"/> by path (e.g. <c>data\...\x_opac.dds</c>).
    /// </summary>
    public static GrayTexture ReadChannel(Func<string, byte[]?> readFile, string ddsPath, int maxSize = 1024)
    {
        var l = ReadLevel(readFile, ddsPath, maxSize);
        return l.Format switch
        {
            Format.Bc4 or Format.Bc5 or Format.Bc3 => new GrayTexture(l.Width, l.Height, DecodeFirstChannel(l.Data.AsSpan(l.Offset), l.Width, l.Height, 0, l.Format == Format.Bc4 ? 8 : 16)),
            Format.Bc1 => throw new NotSupportedException("BC1 has no single channel to read."),
            _ => new RgbaTexture(l.Width, l.Height, Decode(l)).Channel(0),
        };
    }

    /// <summary>Reads the colour channels of the largest mip no bigger than <paramref name="maxSize"/>.</summary>
    public static ColorTexture ReadColor(Func<string, byte[]?> readFile, string ddsPath, int maxSize = 1024)
    {
        var l = ReadLevel(readFile, ddsPath, maxSize);
        var rgba = Decode(l);
        var rgb = new byte[l.Width * l.Height * 3];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4) { rgb[i] = rgba[j]; rgb[i + 1] = rgba[j + 1]; rgb[i + 2] = rgba[j + 2]; }
        return new ColorTexture(l.Width, l.Height, rgb);
    }

    /// <summary>Reads all four channels of the largest mip no bigger than <paramref name="maxSize"/>.</summary>
    public static RgbaTexture ReadRgba(Func<string, byte[]?> readFile, string ddsPath, int maxSize = 1024)
    {
        var l = ReadLevel(readFile, ddsPath, maxSize);
        return new RgbaTexture(l.Width, l.Height, Decode(l));
    }

    /// <summary>Full-size dimensions of a texture, from its header.</summary>
    public static (int Width, int Height) ReadSize(Func<string, byte[]?> readFile, string ddsPath)
    {
        var header = readFile(ddsPath) ?? throw new FileNotFoundException(ddsPath);
        var h = HeaderStart(header);
        return (BitConverter.ToInt32(header, h + 12), BitConverter.ToInt32(header, h + 8));
    }

    // Fields are read relative to the DDS_HEADER; ".a" gloss headers leave out the 4-byte magic
    private static int HeaderStart(byte[] header) =>
        header.Length >= 128 && BitConverter.ToUInt32(header, 0) == 0x20534444 ? 4
        : header.Length >= 124 && BitConverter.ToUInt32(header, 0) == 124 ? 0
        : throw new InvalidDataException("Not a DDS texture.");

    private static Level ReadLevel(Func<string, byte[]?> readFile, string ddsPath, int maxSize)
    {
        var header = readFile(ddsPath) ?? throw new FileNotFoundException(ddsPath);
        var h = HeaderStart(header);
        var height = BitConverter.ToInt32(header, h + 8);
        var width = BitConverter.ToInt32(header, h + 12);
        var mips = Math.Max(1, BitConverter.ToInt32(header, h + 24));
        var pixelFlags = BitConverter.ToUInt32(header, h + 76);
        var fourCc = System.Text.Encoding.ASCII.GetString(header, h + 80, 4);
        var masks = new uint[4];

        Format format; int dataStart;
        if (fourCc == "DX10")
        {
            dataStart = h + 124 + 20;
            format = BitConverter.ToUInt32(header, h + 124) switch
            {
                71 or 72 => Format.Bc1,
                77 or 78 => Format.Bc3,
                80 or 81 => Format.Bc4,
                83 => Format.Bc5,
                84 => Format.Bc5Signed,
                98 or 99 => Format.Bc7,
                var f => throw new NotSupportedException($"Texture format {f} isn't supported."),
            };
        }
        else if ((pixelFlags & 0x4) == 0 && BitConverter.ToInt32(header, h + 84) == 32)
        {
            // Uncompressed 32-bit, channels given by bit masks
            dataStart = h + 124;
            format = Format.Rgba8;
            for (var c = 0; c < 4; c++) masks[c] = BitConverter.ToUInt32(header, h + 88 + c * 4);
            if ((pixelFlags & 0x1) == 0) masks[3] = 0; // no alpha
        }
        else
        {
            dataStart = h + 124;
            format = fourCc switch
            {
                "DXT1" => Format.Bc1,
                "DXT5" => Format.Bc3,
                "ATI1" or "BC4U" => Format.Bc4,
                "ATI2" or "BC5U" => Format.Bc5,
                "BC5S" => Format.Bc5Signed,
                _ => throw new NotSupportedException($"Texture format '{fourCc}' isn't supported."),
            };
        }

        // Count the split parts; part N holds mip 0, part N-1 mip 1, and so on
        var gloss = ddsPath.EndsWith(".a", StringComparison.OrdinalIgnoreCase);
        string Part(int n) => gloss ? $"{ddsPath[..^2]}.{n}a" : $"{ddsPath}.{n}";
        var parts = 0;
        while (parts < 16 && readFile(Part(parts + 1)) is not null) parts++;

        var mip = 0;
        while (mip < mips - 1 && (Math.Max(1, width >> mip) > maxSize || Math.Max(1, height >> mip) > maxSize)) mip++;
        int w = Math.Max(1, width >> mip), hh = Math.Max(1, height >> mip);

        if (mip < parts) return new Level(format, w, hh, readFile(Part(parts - mip))!, 0, masks);

        // Small mips live in the header file, largest first, right after the header
        var offset = dataStart;
        for (var m = parts; m < mip; m++) offset += MipBytes(format, Math.Max(1, width >> m), Math.Max(1, height >> m));
        return new Level(format, w, hh, header, offset, masks);
    }

    private static int MipBytes(Format format, int w, int h) => format switch
    {
        Format.Rgba8 => w * h * 4,
        Format.Bc1 or Format.Bc4 => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        _ => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
    };

    private static byte[] Decode(Level l)
    {
        var data = l.Data.AsSpan(l.Offset);
        int w = l.Width, h = l.Height;
        var rgba = new byte[w * h * 4];
        if (l.Format == Format.Rgba8)
        {
            for (var i = 0; i < w * h; i++)
            {
                var px = BitConverter.ToUInt32(data[(i * 4)..]);
                for (var c = 0; c < 4; c++) rgba[i * 4 + c] = l.Masks[c] == 0 ? (byte)255 : Extract(px, l.Masks[c]);
            }
            return rgba;
        }

        var blockSize = l.Format is Format.Bc1 or Format.Bc4 ? 8 : 16;
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        Span<byte> texels = stackalloc byte[64];
        Span<byte> channel = stackalloc byte[16];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                var block = data.Slice((by * bw + bx) * blockSize, blockSize);
                switch (l.Format)
                {
                    case Format.Bc7:
                        Bc7.DecodeBlock(block, texels);
                        break;
                    case Format.Bc1:
                    case Format.Bc3:
                        DecodeColorBlock(block[(blockSize - 8)..], texels, l.Format == Format.Bc1);
                        if (l.Format == Format.Bc3) { DecodeAlphaChannel(block, channel, false); Put(channel, texels, 3); }
                        break;
                    default:
                        texels.Clear();
                        for (var i = 0; i < 16; i++) texels[i * 4 + 3] = 255;
                        var signed = l.Format == Format.Bc5Signed;
                        DecodeAlphaChannel(block, channel, signed); Put(channel, texels, 0);
                        if (l.Format != Format.Bc4) { DecodeAlphaChannel(block[8..], channel, signed); Put(channel, texels, 1); }
                        break;
                }
                for (var py = 0; py < 4; py++)
                    for (var px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        texels.Slice((py * 4 + px) * 4, 4).CopyTo(rgba.AsSpan((y * w + x) * 4, 4));
                    }
            }
        return rgba;
    }

    private static byte Extract(uint pixel, uint mask)
    {
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        return (byte)(((pixel & mask) >> shift) * 255 / max);
    }

    private static void Put(ReadOnlySpan<byte> channel, Span<byte> texels, int c)
    {
        for (var i = 0; i < 16; i++) texels[i * 4 + c] = channel[i];
    }

    /// <summary>Decodes channel R of BC4/BC5, or alpha of BC3 (all use the same 8-byte "alpha" block first).</summary>
    internal static byte[] DecodeFirstChannel(ReadOnlySpan<byte> data, int w, int h, uint format, int blockSize)
    {
        var texels = new byte[w * h];
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        Span<byte> values = stackalloc byte[16];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                DecodeAlphaChannel(data.Slice((by * bw + bx) * blockSize, 8), values, false);
                for (var py = 0; py < 4; py++)
                    for (var px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        texels[y * w + x] = values[py * 4 + px];
                    }
            }
        _ = format;
        return texels;
    }

    /// <summary>Decodes an 8-byte BC4-style block to 16 values; signed blocks are offset so 128 means 0.</summary>
    private static void DecodeAlphaChannel(ReadOnlySpan<byte> block, Span<byte> output, bool signed)
    {
        Span<int> values = stackalloc int[8];
        int a0, a1, min, max;
        if (signed) { a0 = Math.Max(-127, (int)(sbyte)block[0]); a1 = Math.Max(-127, (int)(sbyte)block[1]); (min, max) = (-127, 127); }
        else { a0 = block[0]; a1 = block[1]; (min, max) = (0, 255); }
        values[0] = a0; values[1] = a1;
        if (a0 > a1)
            for (var i = 1; i <= 6; i++) values[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (var i = 1; i <= 4; i++) values[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            values[6] = min; values[7] = max;
        }
        ulong bits = 0;
        for (var i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
        for (var i = 0; i < 16; i++)
        {
            var v = values[(int)(bits >> (3 * i)) & 7];
            output[i] = signed ? (byte)((v + 127) * 255 / 254) : (byte)v;
        }
    }

    /// <summary>Decodes a BC1 colour block (or the colour half of a BC3 block) to 16 RGBA texels.</summary>
    private static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<byte> texels, bool bc1)
    {
        Span<int> palette = stackalloc int[16]; // 4 colours x RGBA
        int c0 = block[0] | block[1] << 8, c1 = block[2] | block[3] << 8;
        Unpack565(c0, palette[..3]); Unpack565(c1, palette[4..7]);
        palette[3] = palette[7] = palette[11] = palette[15] = 255;
        for (var k = 0; k < 3; k++)
        {
            // BC3 colour always uses 4-colour mode; BC1 uses 3-colour mode (plus transparent) when c0 <= c1
            if (c0 > c1 || !bc1)
            {
                palette[8 + k] = (2 * palette[k] + palette[4 + k]) / 3;
                palette[12 + k] = (palette[k] + 2 * palette[4 + k]) / 3;
            }
            else
            {
                palette[8 + k] = (palette[k] + palette[4 + k]) / 2;
                palette[12 + k] = 0;
            }
        }
        if (bc1 && c0 <= c1) palette[15] = 0;
        var bits = BitConverter.ToUInt32(block[4..]);
        for (var i = 0; i < 16; i++)
        {
            var index = (int)(bits >> (2 * i)) & 3;
            for (var c = 0; c < 4; c++) texels[i * 4 + c] = (byte)palette[index * 4 + c];
        }
    }

    private static void Unpack565(int c, Span<int> rgb)
    {
        rgb[0] = (c >> 11 & 31) * 255 / 31;
        rgb[1] = (c >> 5 & 63) * 255 / 63;
        rgb[2] = (c & 31) * 255 / 31;
    }
}

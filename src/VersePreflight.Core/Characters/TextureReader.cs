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

/// <summary>
/// Reads Star Citizen's split DDS textures (<c>name.dds</c> holds the header and smallest mips,
/// <c>name.dds.N</c> the larger ones, highest N = full size) and decodes the first channel of
/// BC4 / BC5 (and the alpha of BC3) block compression.
/// </summary>
public static class TextureReader
{
    private const uint DxgiBc3 = 77, DxgiBc3Srgb = 78, DxgiBc4 = 80, DxgiBc5 = 83;

    /// <summary>
    /// Reads the largest mip level no bigger than <paramref name="maxSize"/>, from archive entries keyed
    /// by lower-case path (e.g. <c>data\objects\...\hair_texture_04_opac.dds</c>).
    /// </summary>
    public static GrayTexture ReadChannel(Func<string, byte[]?> readFile, string ddsPath, int maxSize = 1024)
    {
        var header = readFile(ddsPath) ?? throw new FileNotFoundException(ddsPath);
        if (header.Length < 148 || BitConverter.ToUInt32(header, 0) != 0x20534444) throw new InvalidDataException("Not a DDS texture.");
        var height = BitConverter.ToInt32(header, 12);
        var width = BitConverter.ToInt32(header, 16);
        var mips = Math.Max(1, BitConverter.ToInt32(header, 28));
        if (BitConverter.ToUInt32(header, 84) != 0x30315844) throw new NotSupportedException("Only DX10 DDS textures are supported."); // "DX10"
        var format = BitConverter.ToUInt32(header, 128);
        var blockSize = format switch { DxgiBc4 => 8, DxgiBc5 or DxgiBc3 or DxgiBc3Srgb => 16, _ => throw new NotSupportedException($"Texture format {format} isn't supported.") };

        // Count the split parts (.dds.1 ... .dds.N); part N holds mip 0, part N-1 mip 1, and so on
        var parts = 0;
        while (readFile($"{ddsPath}.{parts + 1}") is not null && parts < 16) parts++;

        var mip = 0;
        while (mip < mips - 1 && (Math.Max(1, width >> mip) > maxSize || Math.Max(1, height >> mip) > maxSize)) mip++;
        int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);

        byte[] data;
        int offset;
        if (mip < parts)
        {
            data = readFile($"{ddsPath}.{parts - mip}")!;
            offset = 0;
        }
        else
        {
            // Small mips live in the header file after the 148-byte header, smallest-but-largest first
            data = header;
            offset = 148;
            for (var m = parts; m < mip; m++) offset += MipBytes(width >> m, height >> m, blockSize);
        }

        return new GrayTexture(w, h, DecodeFirstChannel(data.AsSpan(offset), w, h, format, blockSize));
    }

    private static int MipBytes(int w, int h, int blockSize) => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockSize;

    /// <summary>Decodes channel R of BC4/BC5, or alpha of BC3 (all use the same 8-byte "alpha" block).</summary>
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
                        var index = (int)(bits >> (3 * (py * 4 + px))) & 7;
                        texels[y * w + x] = values[index];
                    }
            }
        _ = format;
        return texels;
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

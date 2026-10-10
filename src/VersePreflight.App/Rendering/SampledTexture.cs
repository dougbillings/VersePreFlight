using System.Numerics;
using System.Runtime.CompilerServices;
using VersePreflight.Core.Characters;

namespace VersePreflight.App.Rendering;

/// <summary>
/// A texture prepared for the CPU renderer: 1, 3 or 4 byte channels with a box-filtered mip chain, sampled
/// bilinearly with wrapping. Prepared copies are shared per source texture.
/// </summary>
public sealed class SampledTexture
{
    private static readonly ConditionalWeakTable<object, SampledTexture> Prepared = new();

    private readonly byte[] _data;
    private readonly bool _powerOfTwo;

    private SampledTexture(int width, int height, int channels, byte[] data)
    {
        Width = width; Height = height; Channels = channels; _data = data;
        _powerOfTwo = (width & (width - 1)) == 0 && (height & (height - 1)) == 0;
        if (width > 1 && height > 1) Next = Half();
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }

    /// <summary>The next smaller mip, or null for the smallest.</summary>
    public SampledTexture? Next { get; }

    public static SampledTexture From(GrayTexture t) => Prepared.GetValue(t, _ => new SampledTexture(t.Width, t.Height, 1, t.Texels));
    public static SampledTexture From(ColorTexture t) => Prepared.GetValue(t, _ => new SampledTexture(t.Width, t.Height, 3, t.Rgb));
    public static SampledTexture From(RgbaTexture t) => Prepared.GetValue(t, _ => new SampledTexture(t.Width, t.Height, 4, t.Rgba));

    /// <summary>The mip for a level of detail (0 = this size, 1 = half, ...).</summary>
    public SampledTexture Level(float lod)
    {
        var t = this;
        for (var i = 0; i < (int)lod && t.Next is not null; i++) t = t.Next;
        return t;
    }

    /// <summary>Wrapped bilinear sample of one channel, 0..1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Sample1(float u, float v, int channel = 0)
    {
        Taps(u, v, out var i00, out var i10, out var i01, out var i11, out var fx, out var fy);
        var d = _data;
        float a = d[i00 + channel], b = d[i10 + channel], c = d[i01 + channel], e = d[i11 + channel];
        return (a + (b - a) * fx + (c + (e - c) * fx - a - (b - a) * fx) * fy) * (1 / 255f);
    }

    /// <summary>Wrapped bilinear sample of the first three channels, 0..1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 Sample3(float u, float v)
    {
        Taps(u, v, out var i00, out var i10, out var i01, out var i11, out var fx, out var fy);
        var d = _data;
        var top = Vector3.Lerp(new Vector3(d[i00], d[i00 + 1], d[i00 + 2]), new Vector3(d[i10], d[i10 + 1], d[i10 + 2]), fx);
        var bottom = Vector3.Lerp(new Vector3(d[i01], d[i01 + 1], d[i01 + 2]), new Vector3(d[i11], d[i11 + 1], d[i11 + 2]), fx);
        return Vector3.Lerp(top, bottom, fy) * (1 / 255f);
    }

    /// <summary>Wrapped bilinear sample of all four channels, 0..1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 Sample4(float u, float v)
    {
        Taps(u, v, out var i00, out var i10, out var i01, out var i11, out var fx, out var fy);
        var d = _data;
        var top = Vector4.Lerp(new Vector4(d[i00], d[i00 + 1], d[i00 + 2], d[i00 + 3]), new Vector4(d[i10], d[i10 + 1], d[i10 + 2], d[i10 + 3]), fx);
        var bottom = Vector4.Lerp(new Vector4(d[i01], d[i01 + 1], d[i01 + 2], d[i01 + 3]), new Vector4(d[i11], d[i11 + 1], d[i11 + 2], d[i11 + 3]), fx);
        return Vector4.Lerp(top, bottom, fy) * (1 / 255f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Taps(float u, float v, out int i00, out int i10, out int i01, out int i11, out float fx, out float fy)
    {
        var x = u * Width - 0.5f; var y = v * Height - 0.5f;
        var xf = MathF.Floor(x); var yf = MathF.Floor(y);
        fx = x - xf; fy = y - yf;
        int x0, x1, y0, y1;
        if (_powerOfTwo)
        {
            x0 = (int)xf & (Width - 1); x1 = (x0 + 1) & (Width - 1);
            y0 = (int)yf & (Height - 1); y1 = (y0 + 1) & (Height - 1);
        }
        else
        {
            x0 = ((int)xf % Width + Width) % Width; x1 = (x0 + 1) % Width;
            y0 = ((int)yf % Height + Height) % Height; y1 = (y0 + 1) % Height;
        }
        var c = Channels;
        i00 = (y0 * Width + x0) * c; i10 = (y0 * Width + x1) * c;
        i01 = (y1 * Width + x0) * c; i11 = (y1 * Width + x1) * c;
    }

    private SampledTexture Half()
    {
        int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2), c = Channels;
        var data = new byte[w * h * c];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var k = 0; k < c; k++)
                {
                    int x0 = Math.Min(2 * x, Width - 1), x1 = Math.Min(2 * x + 1, Width - 1);
                    int y0 = Math.Min(2 * y, Height - 1), y1 = Math.Min(2 * y + 1, Height - 1);
                    var sum = _data[(y0 * Width + x0) * c + k] + _data[(y0 * Width + x1) * c + k] + _data[(y1 * Width + x0) * c + k] + _data[(y1 * Width + x1) * c + k];
                    data[(y * w + x) * c + k] = (byte)((sum + 2) / 4);
                }
        return new SampledTexture(w, h, c, data);
    }
}

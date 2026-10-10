using System.Numerics;
using System.Runtime.CompilerServices;

namespace VersePreflight.App.Rendering;

/// <summary>
/// A CPU renderer for a character head. Opaque parts (skin, eyes) are rasterized into a visibility buffer
/// and shaded per pixel; hair caps and strands are then blended back to front. Lighting is a fixed studio
/// rig around the camera (warm key, cool fill, blue rim, soft sky), tone mapped with a filmic curve.
/// Runs on all cores and works the same on every platform, without GPU drivers.
/// </summary>
public sealed class HeadRenderer
{
    private const float NearZ = 0.01f;
    private const int NoHit = -1;
    private const int BandRows = 8;

    private static readonly float[] SrgbToLinearLut = Enumerable.Range(0, 1024).Select(i => MathF.Pow(i / 1023f, 2.2f)).ToArray();
    // Indexed by the square root of the linear value, so dark tones (hair, deep skin) keep their precision
    private static readonly byte[] LinearToSrgbLut = Enumerable.Range(0, 4096).Select(i => (byte)MathF.Round(MathF.Pow(i / 4095f * (i / 4095f), 1 / 2.2f) * 255)).ToArray();

    private sealed class Prepared
    {
        public Prepared(MeshPart part)
        {
            Part = part;
            Surface = part.Surface;
            (Normals, Winding) = ComputeNormals(part.Positions, part.Indices);
            Linear = part.Colors.Select(SrgbToLinear).ToArray();
            if (part.Uvs is { } uvs) (TangentU, TangentV) = ComputeTangents(part.Positions, part.Indices, uvs, Normals);
            Screen = new Vector3[part.Positions.Length];
            Transparent = Surface is HairSurface or CapSurface;
            if (Transparent)
            {
                Used = part.Indices.Distinct().ToArray();
                Shaded = new Vector3[part.Positions.Length];
            }
        }

        public MeshPart Part { get; }
        public Surface Surface { get; }
        public Vector3[] Normals { get; }
        public float Winding { get; }
        public Vector3[] Linear { get; }
        public Vector3[]? TangentU { get; }  // dP/du
        public Vector3[]? TangentV { get; }  // dP/dv: down the texture; along hair strands, root to tip
        public Vector3[] Screen { get; }     // x, y in pixels, z = view depth
        public bool Transparent { get; }
        public int[] Used { get; } = [];       // vertices the part's triangles use (blended parts)
        public Vector3[] Shaded { get; } = []; // per frame: lit colour per vertex (blended parts)
        public float TextureLod { get; set; } // per frame: log2 of texels per pixel for a 1-texel-per-UV texture
    }

    /// <summary>Camera and lights for one frame; light directions point towards the light.</summary>
    private sealed record Frame(Vector3 Camera, Vector3 Up, Vector3[] LightDirs, Vector3[] LightColors, Vector3 Sky, Vector3 Ground, HeadLighting Lighting)
    {
        public float Diffuse => Lighting.Diffuse;
        public float Specular => Lighting.Specular;
        public float Reflection => Lighting.Reflection;
    }

    private readonly record struct Fragment(int Part, int Triangle, float Lod, float Boost);

    private Prepared[] _parts = [];
    private Vector3 _focus;
    private float _radius = 0.2f;
    private float[] _depth = [];
    private int[] _visible = [];
    private float[] _color = [];
    private float[] _hair = [];
    private Fragment[] _blended = [];
    private List<int>[] _bands = [];
    private float[] _blendedKeys = [];

    public bool HasMesh => _parts.Length > 0;

    /// <summary>The lights and output settings.</summary>
    public HeadLighting Lighting { get; set; } = new();

    /// <summary>
    /// Sets what to draw. The first part frames the camera (Z-up, face looking towards +Y, as in the game files).
    /// </summary>
    public void SetParts(IReadOnlyList<MeshPart> parts)
    {
        _parts = parts.Where(p => p.Positions.Length > 0 && p.Indices.Length > 0).Select(p => new Prepared(p)).ToArray();
        if (_parts.Length == 0) return;

        // Frame the head rather than the shoulders: centre on the top 25 cm of the main mesh
        var positions = _parts[0].Part.Positions;
        var top = positions.Max(p => p.Z);
        var head = positions.Where(p => p.Z > top - 0.25f).ToArray();
        var min = new Vector3(head.Min(p => p.X), head.Min(p => p.Y), head.Min(p => p.Z));
        var max = new Vector3(head.Max(p => p.X), head.Max(p => p.Y), head.Max(p => p.Z));
        _focus = (min + max) / 2;
        _radius = (max - min).Length() / 2;
        _blended = new Fragment[_parts.Where(p => p.Transparent).Sum(p => p.Part.Indices.Length / 3)];
        _blendedKeys = new float[_blended.Length];
    }

    /// <summary>
    /// Renders into a premultiplied BGRA buffer; empty pixels stay transparent. <paramref name="supersample"/>
    /// is how many buffer pixels make one screen pixel across, so hair density looks the same at any quality.
    /// </summary>
    public void Render(uint[] pixels, int width, int height, float yaw, float pitch, float zoom, float supersample = 1)
    {
        var count = width * height;
        if (_depth.Length < count) { _depth = new float[count]; _visible = new int[count]; _color = new float[count * 4]; _hair = new float[count * 4]; }
        Array.Fill(_depth, float.MaxValue, 0, count);
        Array.Fill(_visible, NoHit, 0, count);
        Array.Clear(_color, 0, count * 4);
        Array.Clear(_hair, 0, count * 4);
        if (!HasMesh) { Array.Clear(pixels, 0, count); return; }

        // Orbit camera around the focus point; yaw turns around Z (up), pitch tilts
        var fromFocus = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));
        const float fov = 28f * MathF.PI / 180f;
        var distance = _radius / MathF.Sin(fov / 2) * 0.9f / zoom;
        var camera = _focus + fromFocus * distance;
        var f = Vector3.Normalize(_focus - camera);
        var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        var up = Vector3.Cross(right, f);
        var focal = height / (2 * MathF.Tan(fov / 2));

        // Studio lights fixed around the camera: a key from the right side, a fill from the left, a rim from
        // behind on the left, and soft light from all around (see HeadLighting)
        var l = Lighting;
        var frame = new Frame(camera, up,
            l.Lights.Select(light => Direction(light.Angles)).ToArray(), l.Lights.Select(light => light.Color).ToArray(), l.Sky, l.Ground, l);

        foreach (var part in _parts) Project(part, camera, f, right, up, focal, width, height);

        // Towards a light at an azimuth (degrees, + to the camera's right, 0 = from the camera) and elevation
        Vector3 Direction(Vector2 angles)
        {
            float az = angles.X * MathF.PI / 180, el = angles.Y * MathF.PI / 180;
            return Vector3.Normalize(-f * (MathF.Cos(el) * MathF.Cos(az)) + right * (MathF.Cos(el) * MathF.Sin(az)) + up * MathF.Sin(el));
        }

        // Opaque: find the nearest triangle per pixel, then shade every pixel once
        var opaque = Enumerable.Range(0, _parts.Length).Where(i => !_parts[i].Transparent).ToArray();
        Bands(height, (y0, y1) =>
        {
            foreach (var p in opaque) RasterVisibility(p, width, y0, y1);
        });
        Parallel.For(0, height, y => ShadeOpaqueRow(y, width, frame));

        // Hair caps and strands, sorted front to back and blended into their own layer over the skin
        // Hair and caps are lit per vertex (their cards are finely divided), so each pixel only blends
        foreach (var part in _parts.Where(p => p.Transparent))
        {
            Parallel.For(0, (part.Used.Length + 2047) / 2048, chunk =>
            {
                var used = part.Used;
                for (var j = chunk * 2048; j < Math.Min(used.Length, (chunk + 1) * 2048); j++)
                {
                    var i = used[j];
                    part.Shaded[i] = part.Surface is HairSurface hair ? ShadeHair(part, hair, i, frame) : ShadeCap(part, i, frame);
                }
            });
        }
        var blended = CollectBlended(camera, f, supersample);
        // Sort the triangles into narrow bands of rows (keeping front-to-back order), so the work spreads
        // evenly over the cores and each band only visits its own triangles
        var bandCount = (height + BandRows - 1) / BandRows;
        if (_bands.Length < bandCount) _bands = Enumerable.Range(0, bandCount).Select(_ => new List<int>()).ToArray();
        for (var b = 0; b < bandCount; b++) _bands[b].Clear();
        for (var i = 0; i < blended; i++)
        {
            var fragment = _blended[i];
            var part = _parts[fragment.Part];
            var idx = part.Part.Indices;
            float y0 = part.Screen[idx[fragment.Triangle]].Y, y1 = part.Screen[idx[fragment.Triangle + 1]].Y, y2 = part.Screen[idx[fragment.Triangle + 2]].Y;
            var first = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))) / BandRows);
            var last = Math.Min(bandCount - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))) / BandRows);
            for (var b = first; b <= last; b++) _bands[b].Add(i);
        }
        Parallel.For(0, bandCount, b =>
        {
            int y0 = b * BandRows, y1 = Math.Min(height, y0 + BandRows);
            foreach (var i in _bands[b]) RasterBlended(_blended[i], width, y0, y1, frame);
        });

        Parallel.For(0, height, y => Resolve(pixels, y, width));
    }


    private static void Bands(int height, Action<int, int> body)
    {
        var bands = Math.Max(1, Math.Min(height, Environment.ProcessorCount * 3));
        Parallel.For(0, bands, b => body(b * height / bands, (b + 1) * height / bands));
    }

    private static void Project(Prepared part, Vector3 camera, Vector3 f, Vector3 right, Vector3 up, float focal, int width, int height)
    {
        var pos = part.Part.Positions;
        for (var i = 0; i < pos.Length; i++)
        {
            var rel = pos[i] - camera;
            var z = Vector3.Dot(rel, f);
            part.Screen[i] = new Vector3(width / 2f + Vector3.Dot(rel, right) / z * focal, height / 2f - Vector3.Dot(rel, up) / z * focal, z);
        }

        // Texture detail level: average texels per pixel over (a sample of) the triangles, for a 1-texel-per-UV texture
        if (part.Part.Uvs is not { } uv) return;
        var idx = part.Part.Indices;
        double screenArea = 0, uvArea = 0;
        for (var t = 0; t + 2 < idx.Length; t += 3 * 7)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if (part.Screen[a].Z < NearZ || part.Screen[b].Z < NearZ || part.Screen[c].Z < NearZ) continue;
            var s = Math.Abs(Cross(part.Screen[a], part.Screen[b], part.Screen[c]));
            if (s <= 0) continue;
            screenArea += s;
            uvArea += Math.Abs((uv[b].X - uv[a].X) * (uv[c].Y - uv[a].Y) - (uv[c].X - uv[a].X) * (uv[b].Y - uv[a].Y));
        }
        part.TextureLod = screenArea > 0 && uvArea > 0 ? 0.5f * MathF.Log2((float)(uvArea / screenArea)) : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Cross(Vector3 p0, Vector3 p1, Vector3 p2) => (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);

    // ---------- Opaque parts ----------

    private void RasterVisibility(int partIndex, int width, int y0, int y1)
    {
        var part = _parts[partIndex];
        var idx = part.Part.Indices;
        var s = part.Screen;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            Vector3 p0 = s[idx[t]], p1 = s[idx[t + 1]], p2 = s[idx[t + 2]];
            if (p0.Z < NearZ || p1.Z < NearZ || p2.Z < NearZ) continue;
            int minY = Math.Max(y0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y)))), maxY = Math.Min(y1 - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
            if (minY > maxY) continue;
            var area = Cross(p0, p1, p2);
            // Screen y points down, so front faces wind negatively when the mesh's normals point outward
            if (area * part.Winding >= 0) continue;
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X)))), maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
            var inv = 1 / area;
            float iz0 = 1 / p0.Z, iz1 = 1 / p1.Z, iz2 = 1 / p2.Z;
            var id = partIndex << 24 | t / 3;
            for (var y = minY; y <= maxY; y++)
            {
                var py = y + 0.5f;
                var (x0, x1) = Span(p0, p1, p2, inv, py, minX, maxX);
                for (var x = x0; x <= x1; x++)
                {
                    var px = x + 0.5f;
                    var w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) * inv;
                    var w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) * inv;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    var z = 1 / (w0 * iz0 + w1 * iz1 + w2 * iz2);
                    var i = y * width + x;
                    if (z >= _depth[i]) continue;
                    _depth[i] = z;
                    _visible[i] = id;
                }
            }
        }
    }

    /// <summary>
    /// The pixel columns of a row whose centres may lie inside a triangle (one pixel of margin either side;
    /// the exact test is still done per pixel). Each barycentric weight is linear in x along the row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int From, int To) Span(Vector3 p0, Vector3 p1, Vector3 p2, float inv, float py, int minX, int maxX)
    {
        float lo = minX, hi = maxX + 1;
        // w0 = (b0 - x * (p2.Y - p1.Y)) * inv, w1 = (b1 - x * (p0.Y - p2.Y)) * inv, w2 = 1 - w0 - w1
        float a0 = -(p2.Y - p1.Y) * inv, b0 = (p1.X * (p2.Y - py) - p2.X * (p1.Y - py)) * inv;
        float a1 = -(p0.Y - p2.Y) * inv, b1 = (p2.X * (p0.Y - py) - p0.X * (p2.Y - py)) * inv;
        Limit(a0, b0, ref lo, ref hi);
        Limit(a1, b1, ref lo, ref hi);
        Limit(-a0 - a1, 1 - b0 - b1, ref lo, ref hi);
        if (lo > hi) return (1, 0);
        return (Math.Max(minX, (int)MathF.Floor(lo) - 1), Math.Min(maxX, (int)MathF.Ceiling(hi) + 1));

        // Narrow [lo, hi] to where a * x + b >= 0
        static void Limit(float a, float b, ref float lo, ref float hi)
        {
            if (a > 1e-12f) lo = MathF.Max(lo, -b / a);
            else if (a < -1e-12f) hi = MathF.Min(hi, -b / a);
            else if (b < 0) { lo = 1; hi = 0; }
        }
    }

    /// <summary>Perspective-correct barycentric weights of a pixel centre in a triangle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Weights(Vector3 p0, Vector3 p1, Vector3 p2, float px, float py)
    {
        var inv = 1 / Cross(p0, p1, p2);
        var w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) * inv;
        var w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) * inv;
        var w = new Vector3(w0 / p0.Z, w1 / p1.Z, (1 - w0 - w1) / p2.Z);
        return w / (w.X + w.Y + w.Z);
    }

    private void ShadeOpaqueRow(int y, int width, Frame frame)
    {
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            var id = _visible[i];
            if (id == NoHit) continue;
            var part = _parts[id >> 24];
            var t = (id & 0xFFFFFF) * 3;
            var idx = part.Part.Indices;
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            var w = Weights(part.Screen[a], part.Screen[b], part.Screen[c], x + 0.5f, y + 0.5f);
            var color = part.Surface switch
            {
                SkinSurface skin => ShadeSkin(part, skin, a, b, c, w, frame),
                PlainSurface plain => ShadePlain(part, plain, a, b, c, w, frame),
                _ => Vector3.Zero,
            };
            _color[i * 4] = color.X; _color[i * 4 + 1] = color.Y; _color[i * 4 + 2] = color.Z; _color[i * 4 + 3] = 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Mix(Vector3[] v, int a, int b, int c, Vector3 w) => v[a] * w.X + v[b] * w.Y + v[c] * w.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 Mix(Vector2[] v, int a, int b, int c, Vector3 w) => v[a] * w.X + v[b] * w.Y + v[c] * w.Z;

    private static Vector3 ShadeSkin(Prepared part, SkinSurface skin, int a, int b, int c, Vector3 w, Frame f)
    {
        var m = skin.Material;
        var uv = Mix(part.Part.Uvs!, a, b, c, w);
        var position = Mix(part.Part.Positions, a, b, c, w);
        var view = Vector3.Normalize(f.Camera - position);
        var ng = Vector3.Normalize(Mix(part.Normals, a, b, c, w));
        var lod = part.TextureLod - 0.5f; // slightly sharp

        var albedo = SrgbToLinear(Level(skin.Diffuse, lod).Sample3(uv.X, uv.Y));
        if (skin.Contrast != 1) albedo = new Vector3(MathF.Pow(albedo.X, skin.Contrast), MathF.Pow(albedo.Y, skin.Contrast), MathF.Pow(albedo.Z, skin.Contrast));
        albedo *= skin.Tone;

        // Tangent-space normal: the head's normal map plus tiled pore detail (green points up the texture)
        float nx = 0, ny = 0, gloss = 0.45f;
        if (skin.Normal is { } normalMap)
        {
            var n = Level(normalMap, lod).Sample4(uv.X, uv.Y);
            nx = n.X * 2 - 1; ny = n.Y * 2 - 1;
        }
        if (skin.Gloss is { } glossMap) gloss = Level(glossMap, lod).Sample1(uv.X, uv.Y);
        if (skin.Detail is { } detailMap)
        {
            var tiling = m.DetailTiling;
            var detailLod = lod + MathF.Log2(tiling);
            var d = Level(detailMap, detailLod).Sample4(uv.X * tiling, uv.Y * tiling);
            // Fade the detail out as its features get smaller than a pixel
            var fade = Math.Clamp(1.5f - (detailLod + MathF.Log2(detailMap.Width)) / 4f, 0, 1);
            nx += (d.W * 2 - 1) * m.DetailBumpScale * 0.6f * fade;
            ny += (d.Y * 2 - 1) * m.DetailBumpScale * 0.6f * fade;
            albedo *= 1 - m.DetailCavityScale * (1 - d.X) * fade;
            gloss += (d.Z - 0.53f) * m.DetailGlossScale * 0.5f * fade;
        }
        var normal = ng;
        if (part.TangentU is { } tu && part.TangentV is { } tv)
        {
            var t = Mix(tu, a, b, c, w); t = Vector3.Normalize(t - ng * Vector3.Dot(ng, t));
            var bt = -Mix(tv, a, b, c, w); bt = Vector3.Normalize(bt - ng * Vector3.Dot(ng, bt) - t * Vector3.Dot(t, bt));
            var nz = MathF.Sqrt(MathF.Max(0.05f, 1 - nx * nx - ny * ny));
            normal = Vector3.Normalize(t * nx + bt * ny + ng * nz);
            if (float.IsNaN(normal.X)) normal = ng;
        }
        var reflectance = m.SpecularLevel * (skin.Specular is { } spec ? Level(spec, lod).Sample1(uv.X, uv.Y) : 0.85f);
        // Hair and beards shade the skin underneath
        var occlusion = part.Part.Occlusion is { } occ ? occ[a] * w.X + occ[b] * w.Y + occ[c] * w.Z : 1;
        var roughness = Math.Clamp(1 - gloss, 0.45f, 0.95f);

        // Light scattering under the skin: red light wraps further and sees a softer surface
        var soft = Vector3.Normalize(normal * 0.4f + ng * 0.6f);
        var ndv = MathF.Max(1e-3f, Vector3.Dot(normal, view));
        var diffuse = Hemisphere(normal, f) * 0.9f;
        var specular = Vector3.Zero;
        for (var k = 0; k < f.LightDirs.Length; k++)
        {
            var dir = f.LightDirs[k]; var color = f.LightColors[k];
            var ndl = Vector3.Dot(normal, dir);
            var ndlSoft = Vector3.Dot(soft, dir);
            diffuse += color * new Vector3(Wrap(ndlSoft, 0.3f), Wrap(ndl, 0.12f), Wrap(ndl, 0.08f));
            // Two specular lobes, as skin has an oily layer over a rougher one (the faint fill adds none)
            if (ndl > 0) specular += color * (f.Specular * f.Lighting.SkinHighlights * Ggx2(normal, view, dir, ndl, ndv, roughness, MathF.Min(1, roughness * 1.5f), reflectance));
        }

        // Reflection of the surroundings, strongest at grazing angles
        var fresnel = reflectance + (1 - reflectance) * Pow5(1 - ndv) * (1 - roughness) * 0.25f;
        specular += Surroundings(Vector3.Reflect(-view, normal), f) * (fresnel * f.Reflection);

        // Thin areas (ears, nostrils) glow when lit from behind
        var transmitted = Vector3.Zero;
        if (skin.Translucency is { } trans && m.TranslucencyMultiplier > 0)
        {
            var thin = Level(trans, lod).Sample1(uv.X, uv.Y) * m.TranslucencyMultiplier;
            for (var k = 0; k < f.LightDirs.Length; k++)
            {
                var back = MathF.Max(0, Vector3.Dot(-view, f.LightDirs[k]));
                transmitted += new Vector3(1f, 0.3f, 0.15f) * f.LightColors[k] * (thin * back * back * 0.35f) * Vector3.SquareRoot(albedo) * 2;
            }
        }
        return (albedo * diffuse * f.Diffuse + specular) * occlusion + transmitted * f.Diffuse;
    }

    private static Vector3 ShadePlain(Prepared part, PlainSurface plain, int a, int b, int c, Vector3 w, Frame f)
    {
        var albedo = Mix(part.Linear, a, b, c, w);
        var position = Mix(part.Part.Positions, a, b, c, w);
        var view = Vector3.Normalize(f.Camera - position);
        var normal = Vector3.Normalize(Mix(part.Normals, a, b, c, w));
        var ndv = MathF.Max(1e-3f, Vector3.Dot(normal, view));
        var diffuse = Hemisphere(normal, f) * plain.AmbientOcclusion;
        var specular = Vector3.Zero;
        for (var k = 0; k < f.LightDirs.Length; k++)
        {
            var dir = f.LightDirs[k]; var color = f.LightColors[k];
            var ndl = Vector3.Dot(normal, dir);
            if (ndl <= 0) continue;
            diffuse += color * ndl * plain.AmbientOcclusion;
            specular += color * (f.Specular * Ggx(normal, view, dir, ndl, ndv, plain.Roughness, plain.Reflectance));
        }
        var fresnel = plain.Reflectance + (1 - plain.Reflectance) * Pow5(1 - ndv) * (1 - plain.Roughness);
        specular += Surroundings(Vector3.Reflect(-view, normal), f) * (fresnel * plain.AmbientOcclusion * f.Reflection);
        return albedo * diffuse * f.Diffuse + specular;
    }

    // ---------- Blended parts (hair caps and strands) ----------

    private int CollectBlended(Vector3 camera, Vector3 f, float supersample)
    {
        var n = 0;
        var screenLod = MathF.Log2(supersample);
        for (var p = 0; p < _parts.Length; p++)
        {
            var part = _parts[p];
            if (!part.Transparent) continue;
            var pos = part.Part.Positions; var idx = part.Part.Indices; var uv = part.Part.Uvs!;
            var hair = part.Surface as HairSurface;
            var fullSize = hair is null ? 0 : Math.Max(hair.Layer.OpacityFullSize, hair.Opacity.Width);
            var loadedLod = hair is null ? 0 : MathF.Log2((float)fullSize / hair.Opacity.Width);
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                Vector3 s0 = part.Screen[a], s1 = part.Screen[b], s2 = part.Screen[c];
                if (s0.Z < NearZ || s1.Z < NearZ || s2.Z < NearZ) continue;
                var depth = Vector3.Dot((pos[a] + pos[b] + pos[c]) / 3 - camera, f);
                if (hair is null)
                {
                    // Caps are drawn over the skin even where the shared scalp mesh sinks below this head's shape,
                    // so only their front faces are kept
                    if (Cross(s0, s1, s2) * part.Winding >= 0) continue;
                    _blendedKeys[n] = depth + 0.004f; // and they sort under the strands
                    _blended[n++] = new Fragment(p, t, 0, 1.6f);
                    continue;
                }

                // Opacity mip from the card's texel density on screen; fine strands thicken at smaller mips,
                // as the game does, so hair stays full instead of breaking up
                var screen = MathF.Abs(Cross(s0, s1, s2));
                var texels = MathF.Abs((uv[b].X - uv[a].X) * (uv[c].Y - uv[a].Y) - (uv[c].X - uv[a].X) * (uv[b].Y - uv[a].Y)) * fullSize * fullSize;
                var lod = screen > 1e-6f && texels > 0 ? MathF.Max(0, 0.5f * MathF.Log2(texels / screen)) : 8;
                var displayLod = Math.Clamp(lod + screenLod, 0, 5);
                var boost = 1 + hair.Layer.Shading.OpacityMipScale * displayLod * 0.4f;
                _blendedKeys[n] = depth;
                _blended[n++] = new Fragment(p, t, MathF.Max(0, lod - loadedLod), boost);
            }
        }
        Array.Sort(_blendedKeys, _blended, 0, n); // near to far
        return n;
    }

    private void RasterBlended(in Fragment fragment, int width, int y0, int y1, Frame frame)
    {
        var part = _parts[fragment.Part];
        var idx = part.Part.Indices;
        int a = idx[fragment.Triangle], b = idx[fragment.Triangle + 1], c = idx[fragment.Triangle + 2];
        Vector3 p0 = part.Screen[a], p1 = part.Screen[b], p2 = part.Screen[c];
        int minY = Math.Max(y0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y)))), maxY = Math.Min(y1 - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        if (minY > maxY) return;
        var area = Cross(p0, p1, p2);
        if (MathF.Abs(area) < 1e-8f) return;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X)))), maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        var inv = 1 / area;
        float iz0 = 1 / p0.Z, iz1 = 1 / p1.Z, iz2 = 1 / p2.Z;
        var cap = part.Surface as CapSurface;
        var hair = part.Surface as HairSurface;
        var opacity = cap?.Texture ?? hair!.Opacity.Level(fragment.Lod);
        var uvs = part.Part.Uvs!;
        var bias = cap is null ? 0.0005f : 0.015f; // caps lie on (or just under) the skin
        // Per-strand colour variation from the id map
        var variation = hair is { Id: not null } ? (hair.Layer.Shading.MelaninVariation + hair.Layer.Shading.PigmentVariation) * 0.8f : 0;

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            var (x0, x1) = Span(p0, p1, p2, inv, py, minX, maxX);
            for (var x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                var w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) * inv;
                var w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) * inv;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var iz = w0 * iz0 + w1 * iz1 + w2 * iz2;
                var i = y * width + x;
                var o = i * 4;
                var cover = _hair[o + 3];
                if (cover > 0.996f || 1 / iz > _depth[i] + bias) continue; // already covered, or behind the skin
                var w = new Vector3(w0 * iz0, w1 * iz1, w2 * iz2) / iz;
                var uv = Mix(uvs, a, b, c, w);
                var alpha = MathF.Min(1, opacity.Sample1(uv.X, uv.Y) * fragment.Boost);
                if (alpha < 0.01f) continue;

                var color = Mix(part.Shaded, a, b, c, w);
                if (variation > 0) color *= 1 + (hair!.Id!.Sample1(uv.X, uv.Y) - 0.5f) * variation;
                // Front to back: each layer only shows through what's in front of it
                var weight = alpha * (1 - cover);
                _hair[o] += color.X * weight;
                _hair[o + 1] += color.Y * weight;
                _hair[o + 2] += color.Z * weight;
                _hair[o + 3] = cover + weight;
            }
        }
    }

    private static Vector3 ShadeHair(Prepared part, HairSurface hair, int i, Frame f)
    {
        var s = hair.Layer.Shading;
        var baseColor = part.Linear[i];
        var view = Vector3.Normalize(f.Camera - part.Part.Positions[i]);
        var normal = part.Normals[i];
        if (Vector3.Dot(normal, view) < 0) normal = -normal;
        var tangent = part.TangentV?[i] ?? Vector3.UnitZ;
        var occlusion = part.Part.Occlusion?[i] ?? 1;

        // Kajiya-Kay style strands: diffuse depends on the angle to the strand, and two highlights run
        // across it: a white one shifted towards the root and a tinted, broader one towards the tip
        var exponent = 8 + 260 * s.Smoothness * s.Smoothness;
        var t1 = Vector3.Normalize(tangent - normal * 0.10f);
        var t2 = Vector3.Normalize(tangent + normal * 0.15f);
        var tint = Vector3.SquareRoot(baseColor);
        var diffuse = Hemisphere(normal, f) * 0.8f;
        var specular = Vector3.Zero;
        for (var k = 0; k < f.LightDirs.Length; k++)
        {
            var dir = f.LightDirs[k]; var color = f.LightColors[k];
            var tl = Vector3.Dot(tangent, dir);
            var facing = Math.Clamp(Vector3.Dot(normal, dir) * 0.5f + 0.5f, 0, 1);
            diffuse += color * (MathF.Sqrt(MathF.Max(0, 1 - tl * tl)) * facing);
            var half = Vector3.Normalize(dir + view);
            var h1 = Vector3.Dot(t1, half); var h2 = Vector3.Dot(t2, half);
            var primary = MathF.Pow(MathF.Sqrt(MathF.Max(0, 1 - h1 * h1)), exponent);
            var secondary = MathF.Pow(MathF.Sqrt(MathF.Max(0, 1 - h2 * h2)), exponent * 0.3f);
            specular += color * (facing * s.SpecularMultiplier) * (new Vector3(0.05f * primary) + 0.10f * secondary * tint);
            // Light shining through strands from behind
            var through = MathF.Max(0, Vector3.Dot(-view, dir));
            through *= through; through *= through * through;
            specular += color * tint * (through * 0.15f);
        }
        return (baseColor * diffuse * f.Diffuse + specular * f.Specular) * occlusion;
    }

    private static Vector3 ShadeCap(Prepared part, int i, Frame f)
    {
        var normal = part.Normals[i];
        var light = Hemisphere(normal, f) * 0.6f;
        for (var k = 0; k < f.LightDirs.Length; k++) light += f.LightColors[k] * Wrap(Vector3.Dot(normal, f.LightDirs[k]), 0.2f) * 0.7f;
        return part.Linear[i] * light * f.Diffuse;
    }

    // ---------- Lighting helpers ----------

    /// <summary>Soft light from all around: sky above, ground bounce below.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Hemisphere(Vector3 direction, Frame f) => Vector3.Lerp(f.Ground, f.Sky, Vector3.Dot(direction, f.Up) * 0.5f + 0.5f);

    /// <summary>The surroundings seen in a reflection.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Surroundings(Vector3 direction, Frame f) =>
        Vector3.Lerp(f.Lighting.ReflectionGround, f.Lighting.ReflectionSky, Vector3.Dot(direction, f.Up) * 0.5f + 0.5f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Wrap(float ndl, float wrap) => MathF.Max(0, (ndl + wrap) / (1 + wrap));

    /// <summary>The average of two GGX lobes with different roughness, sharing the half-vector work.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Ggx2(Vector3 n, Vector3 v, Vector3 l, float ndl, float ndv, float roughness1, float roughness2, float f0)
    {
        var h = Vector3.Normalize(v + l);
        var ndh = MathF.Max(0, Vector3.Dot(n, h));
        var fresnel = f0 + (1 - f0) * Pow5(1 - MathF.Max(0, Vector3.Dot(v, h)));
        return 0.5f * fresnel * ndl * (Lobe(ndh, ndl, ndv, roughness1) + Lobe(ndh, ndl, ndv, roughness2));

        static float Lobe(float ndh, float ndl, float ndv, float roughness)
        {
            var alpha = roughness * roughness;
            var a2 = alpha * alpha;
            var d = ndh * ndh * (a2 - 1) + 1;
            return a2 / (MathF.PI * d * d) * 0.5f / (ndl * (ndv * (1 - alpha) + alpha) + ndv * (ndl * (1 - alpha) + alpha) + 1e-5f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Pow5(float x) { var x2 = x * x; return x2 * x2 * x; }

    /// <summary>GGX microfacet specular with Schlick Fresnel, times N·L.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Ggx(Vector3 n, Vector3 v, Vector3 l, float ndl, float ndv, float roughness, float f0)
    {
        var h = Vector3.Normalize(v + l);
        var ndh = MathF.Max(0, Vector3.Dot(n, h));
        var vdh = MathF.Max(0, Vector3.Dot(v, h));
        var alpha = roughness * roughness;
        var a2 = alpha * alpha;
        var d = ndh * ndh * (a2 - 1) + 1;
        var distribution = a2 / (MathF.PI * d * d);
        var visibility = 0.5f / (ndl * (ndv * (1 - alpha) + alpha) + ndv * (ndl * (1 - alpha) + alpha) + 1e-5f);
        var fresnel = f0 + (1 - f0) * Pow5(1 - vdh);
        return distribution * visibility * fresnel * ndl;
    }

    /// <summary>The mip of a UV-mapped texture for a part's per-frame texture level.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SampledTexture Level(SampledTexture texture, float lod) => texture.Level(lod + MathF.Log2(texture.Width));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 SrgbToLinear(Vector3 c) => new(
        SrgbToLinearLut[(int)(Math.Clamp(c.X, 0, 1) * 1023)], SrgbToLinearLut[(int)(Math.Clamp(c.Y, 0, 1) * 1023)], SrgbToLinearLut[(int)(Math.Clamp(c.Z, 0, 1) * 1023)]);

    // ---------- Output ----------

    /// <summary>Filmic tone mapping (ACES fit by K. Narkowicz), then sRGB, premultiplied by coverage.</summary>
    private void Resolve(uint[] pixels, int y, int width)
    {
        var toneMap = Lighting.ToneMap;
        var balance = Lighting.WhiteBalance * Lighting.Exposure;
        for (var x = 0; x < width; x++)
        {
            var o = (y * width + x) * 4;
            var i = o / 4;
            // Hair over skin (both premultiplied)
            var under = 1 - _hair[o + 3];
            var alpha = Math.Clamp(_hair[o + 3] + _color[o + 3] * under, 0, 1);
            if (alpha <= 0) { pixels[i] = 0; continue; }
            var inv = 1 / alpha;
            float Out(float x, float scale) => toneMap ? Tone(x * scale) : Math.Clamp(x * scale, 0, 1);
            var r = Out((_hair[o] + _color[o] * under) * inv, balance.X) * alpha;
            var g = Out((_hair[o + 1] + _color[o + 1] * under) * inv, balance.Y) * alpha;
            var b = Out((_hair[o + 2] + _color[o + 2] * under) * inv, balance.Z) * alpha;
            pixels[i] = (uint)(alpha * 255) << 24 | (uint)ToSrgb(r) << 16 | (uint)ToSrgb(g) << 8 | ToSrgb(b);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToSrgb(float linear) => LinearToSrgbLut[(int)(MathF.Sqrt(linear) * 4095 + 0.5f)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Tone(float x)
    {
        x = MathF.Max(0, x);
        return Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0, 1);
    }

    // ---------- Mesh setup ----------

    internal static (Vector3[] Normals, float Winding) ComputeNormals(Vector3[] pos, int[] idx)
    {
        var n = new Vector3[pos.Length];
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var fn = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]); // area-weighted
            n[idx[t]] += fn; n[idx[t + 1]] += fn; n[idx[t + 2]] += fn;
        }
        // If the mesh winding points normals inwards, flip them (centre-to-vertex test)
        var centre = pos.Aggregate(Vector3.Zero, (a, b) => a + b) / pos.Length;
        var outward = 0f;
        for (var i = 0; i < pos.Length; i++) outward += Vector3.Dot(n[i], pos[i] - centre);
        var winding = outward < 0 ? -1f : 1f;
        for (var i = 0; i < n.Length; i++) n[i] = n[i] == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(n[i] * winding);
        return (n, winding);
    }

    /// <summary>Per-vertex directions of increasing U and V across the surface.</summary>
    private static (Vector3[] U, Vector3[] V) ComputeTangents(Vector3[] pos, int[] idx, Vector2[] uv, Vector3[] normals)
    {
        var tu = new Vector3[pos.Length];
        var tv = new Vector3[pos.Length];
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            Vector3 e1 = pos[b] - pos[a], e2 = pos[c] - pos[a];
            Vector2 d1 = uv[b] - uv[a], d2 = uv[c] - uv[a];
            var det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;
            var r = 1 / det;
            var du = Vector3.Normalize((e1 * d2.Y - e2 * d1.Y) * r);
            var dv = Vector3.Normalize((e2 * d1.X - e1 * d2.X) * r);
            if (float.IsNaN(du.X) || float.IsNaN(dv.X)) continue;
            var weight = Vector3.Cross(e1, e2).Length(); // larger triangles count more
            tu[a] += du * weight; tu[b] += du * weight; tu[c] += du * weight;
            tv[a] += dv * weight; tv[b] += dv * weight; tv[c] += dv * weight;
        }
        for (var i = 0; i < pos.Length; i++)
        {
            if (tu[i] == Vector3.Zero) tu[i] = Vector3.Cross(normals[i], Vector3.UnitZ) + new Vector3(1e-4f, 0, 0);
            tu[i] = Vector3.Normalize(tu[i]);
            if (tv[i] == Vector3.Zero) tv[i] = Vector3.Cross(normals[i], tu[i]) + new Vector3(0, 0, 1e-4f);
            tv[i] = Vector3.Normalize(tv[i]);
        }
        return (tu, tv);
    }
}

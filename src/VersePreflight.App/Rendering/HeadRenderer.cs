using System.Numerics;
using VersePreflight.Core.Characters;

namespace VersePreflight.App.Rendering;

/// <summary>A mesh to draw, with an sRGB colour per vertex and how shiny it is.</summary>
public sealed record MeshPart(Vector3[] Positions, int[] Indices, Vector3[] Colors, float Specular = 0.12f, float Gloss = 30f)
{
    /// <summary>Texture coordinates for <see cref="Opacity"/>.</summary>
    public Vector2[]? Uvs { get; init; }

    /// <summary>Cut-out mask (e.g. hair strands); pixels below <see cref="AlphaCutoff"/> aren't drawn.</summary>
    public GrayTexture? Opacity { get; init; }
    public float AlphaCutoff { get; init; } = 0.35f;

    /// <summary>Drawn from both sides (hair cards), lit from whichever side faces the camera.</summary>
    public bool TwoSided { get; init; }
}

/// <summary>
/// A small CPU rasterizer for a head and its parts: perspective camera orbiting the face, z-buffer,
/// gamma-correct smooth shading with a warm key light, a cool fill, a soft sheen and a cyan HUD rim light.
/// Running on the CPU avoids GPU driver issues and works the same on every platform.
/// </summary>
public sealed class HeadRenderer
{
    private sealed class Prepared
    {
        public Prepared(MeshPart part)
        {
            Part = part;
            (Normals, Winding) = ComputeNormals(part.Positions, part.Indices);
            LinearColors = part.Colors.Select(ToLinear).ToArray();
            Shade = new Vector3[part.Positions.Length];
            Screen = new Vector3[part.Positions.Length];
        }

        public MeshPart Part { get; }
        public Vector3[] Normals { get; }
        public float Winding { get; }
        public Vector3[] LinearColors { get; }
        public Vector3[] Shade { get; }
        public Vector3[] Screen { get; }
    }

    private List<Prepared> _parts = [];
    private Vector3 _focus;
    private float _radius = 0.2f;

    public bool HasMesh => _parts.Count > 0;

    /// <summary>
    /// Sets what to draw. The first part frames the camera (Z-up, face looking towards +Y, as in the game files).
    /// </summary>
    public void SetParts(IReadOnlyList<MeshPart> parts)
    {
        _parts = parts.Where(p => p.Positions.Length > 0 && p.Indices.Length > 0).Select(p => new Prepared(p)).ToList();
        if (_parts.Count == 0) return;

        // Frame the head rather than the shoulders: centre on the top 25 cm of the main mesh
        var positions = _parts[0].Part.Positions;
        var top = positions.Max(p => p.Z);
        var head = positions.Where(p => p.Z > top - 0.25f).ToArray();
        var min = new Vector3(head.Min(p => p.X), head.Min(p => p.Y), head.Min(p => p.Z));
        var max = new Vector3(head.Max(p => p.X), head.Max(p => p.Y), head.Max(p => p.Z));
        _focus = (min + max) / 2;
        _radius = (max - min).Length() / 2;
    }

    /// <summary>Renders into a premultiplied BGRA buffer; empty pixels stay transparent.</summary>
    public void Render(uint[] pixels, float[] depth, int width, int height, float yaw, float pitch, float zoom)
    {
        Array.Clear(pixels, 0, width * height);
        Array.Fill(depth, float.MaxValue, 0, width * height);
        if (!HasMesh) return;

        // Orbit camera around the focus point; yaw turns around Z (up), pitch tilts
        var forwardFromFocus = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));
        const float fov = 28f * MathF.PI / 180f;
        var distance = _radius / MathF.Sin(fov / 2) * 0.9f / zoom;
        var camera = _focus + forwardFromFocus * distance;
        var f = Vector3.Normalize(_focus - camera);
        var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        var up = Vector3.Cross(right, f);
        var focal = height / (2 * MathF.Tan(fov / 2));

        // Lights are fixed relative to the camera so the face always reads well (linear light values)
        var key = Vector3.Normalize(-f * 0.8f + up * 0.55f - right * 0.45f);
        var fill = Vector3.Normalize(-f * 0.6f - up * 0.1f + right * 0.8f);
        var keyColor = new Vector3(1.25f, 1.18f, 1.08f);
        var fillColor = new Vector3(0.22f, 0.27f, 0.34f);
        var ambient = new Vector3(0.10f, 0.11f, 0.13f);
        var rimColor = new Vector3(0.10f, 0.55f, 0.70f);

        foreach (var part in _parts)
        {
            var pos = part.Part.Positions;
            for (var i = 0; i < pos.Length; i++)
            {
                var rel = pos[i] - camera;
                var z = Vector3.Dot(rel, f);
                part.Screen[i] = new Vector3(width / 2f + Vector3.Dot(rel, right) / z * focal, height / 2f - Vector3.Dot(rel, up) / z * focal, z);

                var n = part.Normals[i];
                var view = Vector3.Normalize(-rel);
                if (part.Part.TwoSided && Vector3.Dot(n, view) < 0) n = -n;
                var albedo = part.LinearColors[i];
                var diffuse = ambient + keyColor * MathF.Max(0, Vector3.Dot(n, key)) + fillColor * MathF.Max(0, Vector3.Dot(n, fill));
                var half = Vector3.Normalize(key + view);
                var spec = part.Part.Specular * MathF.Pow(MathF.Max(0, Vector3.Dot(n, half)), part.Part.Gloss);
                var rim = rimColor * MathF.Pow(1 - MathF.Max(0, Vector3.Dot(n, view)), 3);
                part.Shade[i] = ToSrgb(albedo * diffuse + new Vector3(spec) + rim);
            }
        }

        foreach (var part in _parts)
        {
            var pos = part.Part.Positions; var idx = part.Part.Indices;
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                var faceNormal = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                if (!part.Part.TwoSided && part.Winding * Vector3.Dot(faceNormal, camera - pos[a]) <= 0) continue; // back face
                if (part.Part.Opacity is { } mask && part.Part.Uvs is { } uv)
                    RasterCutout(pixels, depth, width, height, part.Screen[a], part.Screen[b], part.Screen[c], part.Shade[a], part.Shade[b], part.Shade[c],
                        uv[a], uv[b], uv[c], mask, part.Part.AlphaCutoff);
                else
                    Raster(pixels, depth, width, height, part.Screen[a], part.Screen[b], part.Screen[c], part.Shade[a], part.Shade[b], part.Shade[c]);
            }
        }
    }

    private static Vector3 ToLinear(Vector3 srgb) => new(MathF.Pow(srgb.X, 2.2f), MathF.Pow(srgb.Y, 2.2f), MathF.Pow(srgb.Z, 2.2f));

    private static Vector3 ToSrgb(Vector3 linear)
    {
        linear = Vector3.Clamp(linear, Vector3.Zero, Vector3.One);
        return new Vector3(MathF.Pow(linear.X, 1 / 2.2f), MathF.Pow(linear.Y, 1 / 2.2f), MathF.Pow(linear.Z, 1 / 2.2f));
    }

    private static void Raster(uint[] px, float[] depth, int w, int h, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 c0, Vector3 c1, Vector3 c2)
    {
        if (p0.Z <= 0 || p1.Z <= 0 || p2.Z <= 0) return;
        var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(area) < 1e-8f) return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        var inv = 1 / area;

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            var row = y * w;
            for (var x = minX; x <= maxX; x++)
            {
                var pxf = x + 0.5f;
                var w0 = ((p1.X - pxf) * (p2.Y - py) - (p2.X - pxf) * (p1.Y - py)) * inv;
                var w1 = ((p2.X - pxf) * (p0.Y - py) - (p0.X - pxf) * (p2.Y - py)) * inv;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var z = w0 * p0.Z + w1 * p1.Z + w2 * p2.Z;
                if (z >= depth[row + x]) continue;
                depth[row + x] = z;
                var col = w0 * c0 + w1 * c1 + w2 * c2;
                px[row + x] = 0xFF000000u | (uint)(col.X * 255) << 16 | (uint)(col.Y * 255) << 8 | (uint)(col.Z * 255);
            }
        }
    }

    // Like Raster, but samples an opacity mask at the interpolated UV and skips mostly-transparent pixels
    private static void RasterCutout(uint[] px, float[] depth, int w, int h, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 c0, Vector3 c1, Vector3 c2,
        Vector2 t0, Vector2 t1, Vector2 t2, GrayTexture mask, float cutoff)
    {
        if (p0.Z <= 0 || p1.Z <= 0 || p2.Z <= 0) return;
        var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(area) < 1e-8f) return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        var inv = 1 / area;

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            var row = y * w;
            for (var x = minX; x <= maxX; x++)
            {
                var pxf = x + 0.5f;
                var w0 = ((p1.X - pxf) * (p2.Y - py) - (p2.X - pxf) * (p1.Y - py)) * inv;
                var w1 = ((p2.X - pxf) * (p0.Y - py) - (p0.X - pxf) * (p2.Y - py)) * inv;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var z = w0 * p0.Z + w1 * p1.Z + w2 * p2.Z;
                if (z >= depth[row + x]) continue;
                var uv = w0 * t0 + w1 * t1 + w2 * t2;
                var alpha = mask.Sample(uv.X, uv.Y);
                if (alpha < cutoff) continue;
                depth[row + x] = z;
                var col = (w0 * c0 + w1 * c1 + w2 * c2) * (0.75f + 0.25f * alpha); // denser strands read slightly brighter
                px[row + x] = 0xFF000000u | (uint)(col.X * 255) << 16 | (uint)(col.Y * 255) << 8 | (uint)(col.Z * 255);
            }
        }
    }

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
}

/// <summary>Builds per-vertex colours for the parts of a character.</summary>
public static class CharacterColoring
{
    /// <summary>Neutral clay when the character file has no skin colour.</summary>
    public static readonly Vector3 DefaultSkin = new(0.80f, 0.76f, 0.72f);

    public static Vector3[] Uniform(int count, Vector3 color) => Enumerable.Repeat(color, count).ToArray();

    /// <summary>
    /// Colours each eyeball by the angle from its centre: pupil, iris (with a darker limbal ring) facing
    /// forward (+Y), white sclera elsewhere. Each eye is told apart by which side of the face it's on.
    /// </summary>
    public static Vector3[] Eyes(Vector3[] positions, Vector3 irisColor)
    {
        var midX = positions.Average(p => p.X);
        Vector3 Centre(bool left)
        {
            var side = positions.Where(p => p.X < midX == left).ToArray();
            return side.Aggregate(Vector3.Zero, (a, b) => a + b) / side.Length;
        }
        var leftCentre = Centre(true); var rightCentre = Centre(false);
        var sclera = new Vector3(0.88f, 0.86f, 0.83f);
        var pupil = new Vector3(0.02f, 0.02f, 0.02f);
        var ring = irisColor * 0.45f;

        var colors = new Vector3[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var centre = positions[i].X < midX ? leftCentre : rightCentre;
            var dir = Vector3.Normalize(positions[i] - centre);
            var facing = dir.Y; // cosine of the angle from straight ahead
            colors[i] = facing switch
            {
                > 0.985f => pupil,
                > 0.90f => irisColor,
                > 0.86f => ring,
                _ => sclera,
            };
        }
        return colors;
    }
}

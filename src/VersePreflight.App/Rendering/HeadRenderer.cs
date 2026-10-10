using System.Numerics;
using VersePreflight.Core.Characters;

namespace VersePreflight.App.Rendering;

/// <summary>A mesh to draw, with an sRGB colour per vertex and how shiny it is.</summary>
public sealed record MeshPart(Vector3[] Positions, int[] Indices, Vector3[] Colors, float Specular = 0.12f, float Gloss = 30f)
{
    /// <summary>Texture coordinates for <see cref="Albedo"/> and <see cref="Opacity"/>.</summary>
    public Vector2[]? Uvs { get; init; }

    /// <summary>Colour texture (e.g. skin); when set it replaces <see cref="Colors"/>, multiplied by <see cref="AlbedoTint"/>.</summary>
    public ColorTexture? Albedo { get; init; }

    /// <summary>Linear-space multiplier applied to <see cref="Albedo"/> (e.g. to match a chosen skin tone).</summary>
    public Vector3 AlbedoTint { get; init; } = Vector3.One;

    /// <summary>Cut-out mask (e.g. hair strands); pixels below <see cref="AlphaCutoff"/> aren't drawn.</summary>
    public GrayTexture? Opacity { get; init; }
    public float AlphaCutoff { get; init; } = 0.35f;

    /// <summary>Drawn from both sides (hair cards), lit from whichever side faces the camera.</summary>
    public bool TwoSided { get; init; }

    /// <summary>Strength of the cyan HUD rim light (lower for fine strands, which it would wash out).</summary>
    public float Rim { get; init; } = 1f;
}

/// <summary>
/// A small CPU rasterizer for a head and its parts: perspective camera orbiting the face, z-buffer,
/// gamma-correct smooth shading with a warm key light, a cool fill, a soft sheen and a cyan HUD rim light.
/// Running on the CPU avoids GPU driver issues and works the same on every platform.
/// </summary>
public sealed class HeadRenderer
{
    private static readonly float[] ToLinearLut = Enumerable.Range(0, 256).Select(i => MathF.Pow(i / 255f, 2.2f)).ToArray();
    private static readonly byte[] ToSrgbLut = Enumerable.Range(0, 4096).Select(i => (byte)MathF.Round(MathF.Pow(i / 4095f, 1 / 2.2f) * 255)).ToArray();

    private sealed class Prepared
    {
        public Prepared(MeshPart part)
        {
            Part = part;
            (Normals, Winding) = ComputeNormals(part.Positions, part.Indices);
            LinearColors = part.Colors.Select(ToLinear).ToArray();
            Diffuse = new Vector3[part.Positions.Length];
            Additive = new Vector3[part.Positions.Length];
            Shade = new Vector3[part.Positions.Length];
            Screen = new Vector3[part.Positions.Length];
            if (part.Albedo is { } tex)
            {
                // Pre-convert the texture to linear light once, with the tint applied
                AlbedoLinear = new float[tex.Width * tex.Height * 3];
                for (var i = 0; i < AlbedoLinear.Length; i++)
                    AlbedoLinear[i] = ToLinearLut[tex.Rgb[i]] * (i % 3 == 0 ? part.AlbedoTint.X : i % 3 == 1 ? part.AlbedoTint.Y : part.AlbedoTint.Z);
            }
        }

        public MeshPart Part { get; }
        public Vector3[] Normals { get; }
        public float Winding { get; }
        public Vector3[] LinearColors { get; }
        public float[]? AlbedoLinear { get; }
        public Vector3[] Diffuse { get; }   // incoming light per vertex (linear)
        public Vector3[] Additive { get; }  // specular + rim per vertex (linear)
        public Vector3[] Shade { get; }     // final sRGB colour per vertex, for untextured parts
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
        var keyColor = new Vector3(1.05f, 1.0f, 0.93f);
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
                var diffuse = ambient + keyColor * MathF.Max(0, Vector3.Dot(n, key)) + fillColor * MathF.Max(0, Vector3.Dot(n, fill));
                var half = Vector3.Normalize(key + view);
                var spec = part.Part.Specular * MathF.Pow(MathF.Max(0, Vector3.Dot(n, half)), part.Part.Gloss);
                var add = new Vector3(spec) + rimColor * (part.Part.Rim * MathF.Pow(1 - MathF.Max(0, Vector3.Dot(n, view)), 3));
                part.Diffuse[i] = diffuse;
                part.Additive[i] = add;
                part.Shade[i] = ToSrgb(part.LinearColors[i] * diffuse + add);
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
                var tri = new Triangle(part.Screen[a], part.Screen[b], part.Screen[c]);
                if (part.Part.Opacity is { } mask && part.Part.Uvs is { } uv)
                    RasterCutout(pixels, depth, width, height, tri, part.Shade[a], part.Shade[b], part.Shade[c], uv[a], uv[b], uv[c], mask, part.Part.AlphaCutoff);
                else if (part.AlbedoLinear is { } albedo && part.Part.Uvs is { } tuv)
                    RasterTextured(pixels, depth, width, height, tri, part, albedo, a, b, c, tuv);
                else
                    Raster(pixels, depth, width, height, tri, part.Shade[a], part.Shade[b], part.Shade[c]);
            }
        }
    }

    private static Vector3 ToLinear(Vector3 srgb) => new(MathF.Pow(srgb.X, 2.2f), MathF.Pow(srgb.Y, 2.2f), MathF.Pow(srgb.Z, 2.2f));

    private static Vector3 ToSrgb(Vector3 linear)
    {
        linear = Vector3.Clamp(linear, Vector3.Zero, Vector3.One);
        return new Vector3(MathF.Pow(linear.X, 1 / 2.2f), MathF.Pow(linear.Y, 1 / 2.2f), MathF.Pow(linear.Z, 1 / 2.2f));
    }

    private static uint Pack(Vector3 srgb) =>
        0xFF000000u | (uint)(Math.Clamp(srgb.X, 0, 1) * 255) << 16 | (uint)(Math.Clamp(srgb.Y, 0, 1) * 255) << 8 | (uint)(Math.Clamp(srgb.Z, 0, 1) * 255);

    private static byte LinearToByte(float v) => ToSrgbLut[(int)(Math.Clamp(v, 0, 1) * 4095)];

    /// <summary>Screen-space triangle with its bounding box and edge setup.</summary>
    private readonly struct Triangle
    {
        public Triangle(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            P0 = p0; P1 = p1; P2 = p2;
            var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
            Valid = p0.Z > 0 && p1.Z > 0 && p2.Z > 0 && MathF.Abs(area) >= 1e-8f;
            Inv = Valid ? 1 / area : 0;
        }

        public Vector3 P0 { get; }
        public Vector3 P1 { get; }
        public Vector3 P2 { get; }
        public bool Valid { get; }
        public float Inv { get; }

        public (int MinX, int MaxX, int MinY, int MaxY) Bounds(int w, int h) => (
            Math.Max(0, (int)MathF.Floor(MathF.Min(P0.X, MathF.Min(P1.X, P2.X)))),
            Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(P0.X, MathF.Max(P1.X, P2.X)))),
            Math.Max(0, (int)MathF.Floor(MathF.Min(P0.Y, MathF.Min(P1.Y, P2.Y)))),
            Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(P0.Y, MathF.Max(P1.Y, P2.Y)))));

        /// <summary>Barycentric weights of a pixel centre; false when outside.</summary>
        public bool Weights(float px, float py, out float w0, out float w1, out float w2)
        {
            w0 = ((P1.X - px) * (P2.Y - py) - (P2.X - px) * (P1.Y - py)) * Inv;
            w1 = ((P2.X - px) * (P0.Y - py) - (P0.X - px) * (P2.Y - py)) * Inv;
            w2 = 1 - w0 - w1;
            return w0 >= 0 && w1 >= 0 && w2 >= 0;
        }
    }

    private static void Raster(uint[] px, float[] depth, int w, int h, Triangle t, Vector3 c0, Vector3 c1, Vector3 c2)
    {
        if (!t.Valid) return;
        var (minX, maxX, minY, maxY) = t.Bounds(w, h);
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (!t.Weights(x + 0.5f, y + 0.5f, out var w0, out var w1, out var w2)) continue;
                var z = w0 * t.P0.Z + w1 * t.P1.Z + w2 * t.P2.Z;
                var i = y * w + x;
                if (z >= depth[i]) continue;
                depth[i] = z;
                px[i] = Pack(w0 * c0 + w1 * c1 + w2 * c2);
            }
    }

    // Per-pixel colour from the albedo texture, lit with the interpolated per-vertex light
    private static void RasterTextured(uint[] px, float[] depth, int w, int h, Triangle t, Prepared part, float[] albedo, int a, int b, int c, Vector2[] uv)
    {
        if (!t.Valid) return;
        var tex = part.Part.Albedo!;
        var (minX, maxX, minY, maxY) = t.Bounds(w, h);
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (!t.Weights(x + 0.5f, y + 0.5f, out var w0, out var w1, out var w2)) continue;
                var z = w0 * t.P0.Z + w1 * t.P1.Z + w2 * t.P2.Z;
                var i = y * w + x;
                if (z >= depth[i]) continue;
                depth[i] = z;

                var u = w0 * uv[a] + w1 * uv[b] + w2 * uv[c];
                var tx = (int)((u.X - MathF.Floor(u.X)) * tex.Width) % tex.Width;
                var ty = (int)((u.Y - MathF.Floor(u.Y)) * tex.Height) % tex.Height;
                var ti = (ty * tex.Width + tx) * 3;
                var light = w0 * part.Diffuse[a] + w1 * part.Diffuse[b] + w2 * part.Diffuse[c];
                var add = w0 * part.Additive[a] + w1 * part.Additive[b] + w2 * part.Additive[c];
                px[i] = 0xFF000000u
                        | (uint)LinearToByte(albedo[ti] * light.X + add.X) << 16
                        | (uint)LinearToByte(albedo[ti + 1] * light.Y + add.Y) << 8
                        | LinearToByte(albedo[ti + 2] * light.Z + add.Z);
            }
    }

    // Like Raster, but samples an opacity mask at the interpolated UV and skips mostly-transparent pixels
    private static void RasterCutout(uint[] px, float[] depth, int w, int h, Triangle t, Vector3 c0, Vector3 c1, Vector3 c2,
        Vector2 t0, Vector2 t1, Vector2 t2, GrayTexture mask, float cutoff)
    {
        if (!t.Valid) return;
        var (minX, maxX, minY, maxY) = t.Bounds(w, h);
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (!t.Weights(x + 0.5f, y + 0.5f, out var w0, out var w1, out var w2)) continue;
                var z = w0 * t.P0.Z + w1 * t.P1.Z + w2 * t.P2.Z;
                var i = y * w + x;
                if (z >= depth[i]) continue;
                var uv = w0 * t0 + w1 * t1 + w2 * t2;
                var alpha = mask.Sample(uv.X, uv.Y);
                if (alpha < cutoff) continue;
                depth[i] = z;
                px[i] = Pack((w0 * c0 + w1 * c1 + w2 * c2) * (0.75f + 0.25f * alpha)); // denser strands read slightly brighter
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
    /// Linear multiplier that makes a skin texture's average colour match the chosen skin tone, keeping
    /// the texture's own variation (pores, lips, shading).
    /// </summary>
    public static Vector3 SkinTint(ColorTexture texture, Vector3 skinSrgb)
    {
        static float Lin(float v) => MathF.Pow(Math.Clamp(v, 0.001f, 1), 2.2f);
        var mean = texture.Mean();
        return new Vector3(Lin(skinSrgb.X) / Lin(mean.X), Lin(skinSrgb.Y) / Lin(mean.Y), Lin(skinSrgb.Z) / Lin(mean.Z));
    }

    /// <summary>
    /// Hair colour per vertex. The dye sits towards the tip of each strand, fading in from the natural
    /// colour; the dyed share grows with the dye amount (√amount, which matched in-game shots better than
    /// the raw amount). Along the dyed part the colour goes from the first dye colour to the second.
    /// A black dye colour means "no colour chosen" when the other one is set.
    /// The position along the strand comes from the card's V texture coordinate (0 = root).
    /// </summary>
    public static Vector3[] Hair(Vector2[] uvs, HairChoice? choice, float defaultMelanin)
    {
        var melanin = choice is { Melanin: > 0 } ? choice.Melanin : defaultMelanin; // 0 in the file means "style default"
        var natural = NaturalHair(melanin, choice?.Redness ?? 0);
        if (choice is null || choice.DyeAmount <= 0) return Uniform(uvs.Length, natural);

        var dye1 = choice.DyeColor.ToVector();
        var dye2 = choice.DyeColor2.ToVector();
        if (choice.DyeColor2 == default) dye2 = dye1;
        else if (choice.DyeColor == default) dye1 = dye2;

        var reach = MathF.Sqrt(Math.Clamp(choice.DyeAmount, 0, 1)); // share of the strand (from the tip) that's dyed
        var dyeStart = 1 - reach;
        var colors = new Vector3[uvs.Length];
        for (var i = 0; i < uvs.Length; i++)
        {
            var t = Math.Clamp(uvs[i].Y - MathF.Floor(uvs[i].Y), 0, 1);
            if (t <= dyeStart) { colors[i] = natural; continue; }
            var along = (t - dyeStart) / reach;                          // 0 where the dye starts .. 1 at the tip
            var dye = Vector3.Lerp(dye1, dye2, SmoothStep(along));
            colors[i] = Vector3.Lerp(natural, dye, SmoothStep(along * 2)); // fades in over the first half of the dyed part
        }
        return colors;
    }

    /// <summary>
    /// Natural hair colour (sRGB) from melanin (0 white .. 1 black) and redness, using the usual physically
    /// based hair model: melanin sets eumelanin/pheomelanin concentrations, whose absorption gives the colour.
    /// Real hair darkens quickly, so 0.6 is already brown and 0.97 black (the game's own presets).
    /// </summary>
    public static Vector3 NaturalHair(float melanin, float redness)
    {
        var concentration = -MathF.Log(MathF.Max(1 - Math.Clamp(melanin, 0, 1), 1e-4f));
        var r = Math.Clamp(redness, 0, 1);
        var absorption = concentration * (1 - r) * new Vector3(0.506f, 0.841f, 1.653f) + concentration * r * new Vector3(0.343f, 0.733f, 1.924f);
        // Colour from absorption (d'Eon et al.); the scale is softened to account for light scattering through strands
        const float scale = 4f;
        var linear = new Vector3(MathF.Exp(-MathF.Sqrt(absorption.X) * scale), MathF.Exp(-MathF.Sqrt(absorption.Y) * scale), MathF.Exp(-MathF.Sqrt(absorption.Z) * scale));
        return new Vector3(MathF.Pow(linear.X, 1 / 2.2f), MathF.Pow(linear.Y, 1 / 2.2f), MathF.Pow(linear.Z, 1 / 2.2f));
    }

    private static float SmoothStep(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

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
        var sclera = new Vector3(0.70f, 0.67f, 0.63f);
        var pupil = new Vector3(0.02f, 0.02f, 0.02f);
        var ring = irisColor * 0.45f;

        var colors = new Vector3[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var centre = positions[i].X < midX ? leftCentre : rightCentre;
            var dir = Vector3.Normalize(positions[i] - centre);
            colors[i] = dir.Y switch
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

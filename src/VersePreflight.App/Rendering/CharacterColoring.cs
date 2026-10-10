using System.Numerics;
using VersePreflight.Core.Characters;

namespace VersePreflight.App.Rendering;

/// <summary>Builds colours and shading inputs for the parts of a character.</summary>
public static class CharacterColoring
{
    /// <summary>Neutral clay when the character file has no skin colour.</summary>
    public static readonly Vector3 DefaultSkin = new(0.80f, 0.76f, 0.72f);

    public static Vector3[] Uniform(int count, Vector3 color) => Enumerable.Repeat(color, count).ToArray();

    /// <summary>
    /// Linear multiplier that tones a skin texture so its average is the chosen skin colour, keeping the
    /// texture's own detail. Measured against in-game shots, skin in the character booth shows about 20%
    /// less saturated than the stored colour, so the target is desaturated to match. (The materials'
    /// SourceAverageColor/FinalSkinTone calibration didn't match the shots.)
    /// </summary>
    public static Vector3 SkinTone(SkinMaterial material, Vector3 skinSrgb)
    {
        static float Lin(float v) => MathF.Pow(Math.Clamp(v, 0.001f, 1), 2.2f);
        var target = new Vector3(Lin(skinSrgb.X), Lin(skinSrgb.Y), Lin(skinSrgb.Z));
        var luma = Vector3.Dot(target, new Vector3(0.2126f, 0.7152f, 0.0722f));
        target = Vector3.Lerp(new Vector3(luma), target, 0.8f);
        var mean = material.Diffuse.Mean();
        return target / new Vector3(Lin(mean.X), Lin(mean.Y), Lin(mean.Z));
    }

    /// <summary>
    /// How much hair and beards shade the skin under them, per skin vertex (1 = open), from the density of
    /// strand vertices within a centimetre. This is a large part of what makes short hair read as dense.
    /// </summary>
    public static float[] SkinOcclusion(Vector3[] skin, IReadOnlyList<Vector3> strands, float strength = 1)
    {
        var density = Density(skin, strands, 0.01f);
        var occlusion = new float[skin.Length];
        for (var i = 0; i < skin.Length; i++) occlusion[i] = MathF.Max(0.15f, MathF.Exp(-density[i] * strength / 6f));
        return occlusion;
    }

    /// <summary>The strand vertices of a mesh's strand layers (not caps or other geometry).</summary>
    public static IEnumerable<Vector3> StrandVertices(AccessoryMesh item) =>
        item.Layers.Where(l => l.Kind == HairLayerKind.Strands).SelectMany(l => l.Indices).Distinct().Select(i => item.Mesh.Positions[i]);

    /// <summary>For each point, the sum of (1 - d/r)² over sample points within distance r.</summary>
    internal static float[] Density(Vector3[] points, IReadOnlyList<Vector3> samples, float radius)
    {
        var grid = new Dictionary<(int, int, int), List<Vector3>>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / radius), (int)MathF.Floor(p.Y / radius), (int)MathF.Floor(p.Z / radius));
        foreach (var p in samples)
        {
            var k = Key(p);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = [];
            list.Add(p);
        }
        var density = new float[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var (kx, ky, kz) = Key(points[i]);
            var sum = 0f;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list))
                            foreach (var q in list)
                            {
                                var d = Vector3.Distance(points[i], q);
                                if (d < radius) { var f = 1 - d / radius; sum += f * f; }
                            }
            density[i] = sum;
        }
        return density;
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
        var natural = NaturalHair(choice, defaultMelanin);
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

    /// <summary>The undyed hair colour of a choice (0 melanin in the file means the style's default).</summary>
    public static Vector3 NaturalHair(HairChoice? choice, float defaultMelanin) =>
        NaturalHair(choice is { Melanin: > 0 } ? choice.Melanin : defaultMelanin, choice?.Redness ?? 0);

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

    /// <summary>
    /// Ambient occlusion per hair vertex from its height above the skin: strands close to the scalp sit
    /// under the rest of the hair and get little light. <paramref name="strength"/> is the material's
    /// AmbientOcclusion setting.
    /// </summary>
    public static float[] HairOcclusion(Vector3[] hair, Vector3[] skin, float strength)
    {
        const float cell = 0.01f;   // 1 cm grid over the skin vertices
        const float reach = 0.04f;  // look no further than 4 cm
        var grid = new Dictionary<(int, int, int), List<Vector3>>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        foreach (var p in skin)
        {
            var k = Key(p);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = [];
            list.Add(p);
        }

        var heights = new float[hair.Length];
        var steps = (int)MathF.Ceiling(reach / cell);
        for (var i = 0; i < hair.Length; i++)
        {
            var (kx, ky, kz) = Key(hair[i]);
            var best = reach * reach;
            for (var dx = -steps; dx <= steps; dx++)
                for (var dy = -steps; dy <= steps; dy++)
                    for (var dz = -steps; dz <= steps; dz++)
                        if (grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list))
                            foreach (var p in list) best = MathF.Min(best, Vector3.DistanceSquared(p, hair[i]));
            heights[i] = MathF.Sqrt(best);
        }

        // Normalise by the style's thickness (most strands lie within it)
        var sorted = heights.OrderBy(h => h).ToArray();
        var thickness = MathF.Max(0.004f, sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.9f)] : 0.01f);
        var floor = 1 / (1 + Math.Max(0, strength) * 0.45f);
        var occlusion = new float[hair.Length];
        for (var i = 0; i < hair.Length; i++) occlusion[i] = floor + (1 - floor) * SmoothStep(heights[i] / thickness);
        return occlusion;
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
        var sclera = new Vector3(0.5f, 0.47f, 0.45f);
        var pupil = new Vector3(0.02f, 0.02f, 0.02f);
        irisColor = Vector3.Min(Vector3.One, irisColor * 1.4f); // the game's iris shader shows the chosen colour brighter
        var ring = irisColor * 0.45f;

        var colors = new Vector3[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var centre = positions[i].X < midX ? leftCentre : rightCentre;
            var dir = Vector3.Normalize(positions[i] - centre);
            colors[i] = dir.Y switch
            {
                > 0.98f => pupil,
                > 0.86f => irisColor,
                > 0.82f => ring,
                _ => sclera,
            };
        }
        return colors;
    }
}

using System.Globalization;
using System.Numerics;

namespace VersePreflight.Core.Characters;

/// <summary>The textures and settings of a head's skin material (the game's HumanSkin shader) that the viewer uses.</summary>
public sealed record SkinMaterial(ColorTexture Diffuse)
{
    /// <summary>Tangent-space normal (signed BC5, stored offset: red X, green Y, 128 = 0; green points up the texture).</summary>
    public RgbaTexture? Normal { get; init; }
    public GrayTexture? Gloss { get; init; }
    public GrayTexture? Specular { get; init; }

    /// <summary>Tiled pore detail: red cavity, green normal Y, blue gloss, alpha normal X.</summary>
    public RgbaTexture? Detail { get; init; }
    public float DetailTiling { get; init; } = 1;
    public GrayTexture? Translucency { get; init; }

    /// <summary>
    /// The skin tone the texture was authored for (linear). The game's tone adjustment maps it to the chosen
    /// skin colour, so textures meant to look lighter or darker keep that relationship.
    /// </summary>
    public Vector3 SourceAverageColor { get; init; }

    /// <summary>How strongly the texture's own variation is kept when toning (an exponent; 1 = as authored).</summary>
    public float SourceDetail { get; init; } = 1;
    public float SpecularLevel { get; init; } = 0.032f;
    public float DetailBumpScale { get; init; } = 1;
    public float DetailCavityScale { get; init; }
    public float DetailGlossScale { get; init; }
    public float TranslucencyMultiplier { get; init; }
}

/// <summary>Strand shading settings from a hair material (HairPBR shader).</summary>
public sealed record HairShading
{
    public float Smoothness { get; init; } = 0.6f;
    public float SpecularMultiplier { get; init; } = 1;

    /// <summary>How much strand opacity is boosted at smaller mips, so hair stays full at a distance.</summary>
    public float OpacityMipScale { get; init; } = 3;
    public float AmbientOcclusion { get; init; } = 2.5f;
    public float ShadowDensity { get; init; } = 3;
    public float MelaninVariation { get; init; }
    public float PigmentVariation { get; init; }
}

/// <summary>Helpers for reading values from CryEngine material parameters.</summary>
internal static class MaterialParams
{
    public static float Float(CryXmlElement? element, string name, float fallback) =>
        element?[name] is { } s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static Vector3 Vector(CryXmlElement? element, string name, Vector3 fallback)
    {
        var parts = element?[name]?.Split(',');
        if (parts is not { Length: 3 }) return fallback;
        var values = new float[3];
        for (var i = 0; i < 3; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return fallback;
        return new Vector3(values[0], values[1], values[2]);
    }
}

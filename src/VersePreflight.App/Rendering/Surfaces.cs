using System.Numerics;
using VersePreflight.Core.Characters;

namespace VersePreflight.App.Rendering;

/// <summary>How a mesh part is shaded.</summary>
public abstract record Surface;

/// <summary>A smooth surface lit with its per-vertex colours (e.g. the eyes).</summary>
public sealed record PlainSurface(float Roughness, float Reflectance = 0.04f) : Surface
{
    /// <summary>Darkens ambient light (e.g. eyes sit in the shadow of the lids).</summary>
    public float AmbientOcclusion { get; init; } = 1;
}

/// <summary>The game's skin material, toned to the character's skin colour by a linear multiplier.</summary>
public sealed record SkinSurface(SkinMaterial Material, Vector3 Tone) : Surface
{
    internal SampledTexture Diffuse { get; } = SampledTexture.From(Material.Diffuse);
    internal SampledTexture? Normal { get; } = Material.Normal is { } t ? SampledTexture.From(t) : null;
    internal SampledTexture? Gloss { get; } = Material.Gloss is { } t ? SampledTexture.From(t) : null;
    internal SampledTexture? Specular { get; } = Material.Specular is { } t ? SampledTexture.From(t) : null;
    internal SampledTexture? Detail { get; } = Material.Detail is { } t ? SampledTexture.From(t) : null;
    internal SampledTexture? Translucency { get; } = Material.Translucency is { } t ? SampledTexture.From(t) : null;
}

/// <summary>Hair strand cards: an opacity atlas, blended back to front, with strand-aligned highlights.</summary>
public sealed record HairSurface(HairLayer Layer) : Surface
{
    internal SampledTexture Opacity { get; } = SampledTexture.From(Layer.Opacity);
    internal SampledTexture? Id { get; } = Layer.Id is { } t ? SampledTexture.From(t) : null;
}

/// <summary>A hair cap: hair density painted over the skin under short hair and beards.</summary>
public sealed record CapSurface(GrayTexture Density) : Surface
{
    internal SampledTexture Texture { get; } = SampledTexture.From(Density);
}

/// <summary>A mesh to draw, with a colour per vertex (sRGB) and how it's shaded.</summary>
public sealed record MeshPart(Vector3[] Positions, int[] Indices, Vector3[] Colors, Surface Surface)
{
    /// <summary>Texture coordinates; required by skin, hair and cap surfaces.</summary>
    public Vector2[]? Uvs { get; init; }

    /// <summary>Per-vertex ambient occlusion (1 = open), e.g. for inner hair layers.</summary>
    public float[]? Occlusion { get; init; }
}

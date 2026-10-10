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

/// <summary>
/// The game's skin material, toned to the character's skin colour: linear texel^<see cref="Contrast"/> × <see cref="Tone"/>.
/// </summary>
public sealed record SkinSurface(SkinMaterial Material, Vector3 Tone) : Surface
{
    public float Contrast { get; init; } = 1;

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

/// <summary>A light at a direction relative to the camera: azimuth (degrees, + to the right, 0 = from the camera) and elevation.</summary>
public sealed record StudioLight(Vector2 Angles, Vector3 Color);

/// <summary>
/// Studio lights around the camera (linear intensities) and output settings. The defaults reproduce the
/// in-game character booth, fitted to the user's screenshots at matching face points (see FINDINGS.md):
/// white lights (a large softbox on the right, spread over several lights; a small light above the camera;
/// a faint fill from the left; soft light from below) and a cool colour balance, plus a blue rim from
/// behind on the left for the HUD look.
/// The scales let each kind of light be measured separately when calibrating.
/// </summary>
public sealed record HeadLighting
{
    public static readonly IReadOnlyList<StudioLight> Booth =
    [
        .. new[] { -30f, 0f, 30f }.SelectMany(el => new[] { 60f, 90f }.Select(az => new StudioLight(new(az, el), new Vector3(1.645f / 6)))),
        new(new(0, 30), new(0.09f)),
        new(new(0, 60), new(0.09f)),
        new(new(-60, 0), new(0.05f)),
        new(new(-90, 0), new(0.05f)),
        new(new(-129, 15), new(0.06f, 0.14f, 0.24f)),
    ];

    public IReadOnlyList<StudioLight> Lights { get; init; } = Booth;

    /// <summary>Diffuse light from the surroundings: above and below (vertical surfaces get the average).</summary>
    public Vector3 Sky { get; init; } = new(0.04f);
    public Vector3 Ground { get; init; } = new(0.20f);

    /// <summary>The surroundings as seen in reflections, above and below.</summary>
    public Vector3 ReflectionSky { get; init; } = new(0.04f);
    public Vector3 ReflectionGround { get; init; } = new(0.22f);

    /// <summary>Scales diffuse light, direct highlights and reflections of the surroundings.</summary>
    public float Diffuse { get; init; } = 1;
    public float Specular { get; init; } = 1;
    public float Reflection { get; init; } = 1;

    /// <summary>
    /// Strength of skin highlights relative to a single GGX lobe. In-game skin shows a broad sheen from the
    /// booth's large lights; the fit to the screenshots wants it about this strong.
    /// </summary>
    public float SkinHighlights { get; init; } = 6;

    public float Exposure { get; init; } = 1;

    /// <summary>Colour balance of the final image (the booth renders cool: less red, more blue).</summary>
    public Vector3 WhiteBalance { get; init; } = new(0.75f, 1, 1.14f);

    /// <summary>Filmic tone mapping; off gives linear output (clamped), for measuring.</summary>
    public bool ToneMap { get; init; } = true;
}

using System.Numerics;
using System.Runtime.CompilerServices;
using VersePreflight.Core.Characters;

namespace VersePreflight.App.Rendering;

/// <summary>Settings for assembling a character (exposed for calibrating against in-game shots).</summary>
public sealed record SceneOptions
{
    /// <summary>How much scalp hair shades the skin under it (dense short hair relies on this).</summary>
    public float HairShading { get; init; } = 1;

    /// <summary>How much beards and eyebrows shade the skin under them; in game, skin shows through.</summary>
    public float FacialHairShading { get; init; } = 0.15f;

    /// <summary>Move hair and beards with the character's face shape (they're authored on the base head).</summary>
    public bool FollowFace { get; init; } = true;
}

/// <summary>Builds the parts to draw for a character: head, eyes, hair, beard, eyebrows and scalp.</summary>
public static class CharacterScene
{
    private static readonly ConditionalWeakTable<HeadLibrary, FaceFollower> Followers = new();

    /// <summary>
    /// The head with the character's skin, the eyes, and the equipped hair, beard and eyebrows.
    /// <paramref name="warn"/> hears about accessories that couldn't be loaded (the head is still drawn).
    /// </summary>
    public static List<MeshPart> Build(HeadLibrary library, CharacterFile character, AccessoryLibrary? accessories, SceneOptions? options = null, Action<string>? warn = null)
    {
        options ??= new SceneOptions();
        var head = library.Head.Blend(character);
        var skin = character.SkinColor?.ToVector() ?? CharacterColoring.DefaultSkin;
        // The chosen skin material (texture, normal, gloss and pore detail), toned to the skin colour as the game does
        var material = accessories?.LoadSkinMaterial(character.HeadMaterialId);
        Surface skinSurface = material is null
            ? new PlainSurface(Roughness: 0.55f, Reflectance: 0.03f)
            : new SkinSurface(material, CharacterColoring.SkinTone(material, skin));
        var parts = new List<MeshPart> { new(head, library.Head.Indices, CharacterColoring.Uniform(head.Length, skin), skinSurface) { Uvs = library.Head.Uvs } };
        if (library.Eyes is { } eyesSet)
        {
            var eyes = eyesSet.Blend(character);
            var iris = character.EyeColor?.ToVector() ?? new Vector3(0.35f, 0.25f, 0.15f);
            parts.Add(new MeshPart(eyes, eyesSet.Indices, CharacterColoring.Eyes(eyes, iris), new PlainSurface(Roughness: 0.12f) { AmbientOcclusion = 0.35f }));
        }
        if (accessories is null) return parts;

        try
        {
            var follower = Followers.GetValue(library, l => new FaceFollower(l.BasePositions));
            var equipped = accessories.LoadFor(character, library.IsFemale)
                .Select(e => (e.Kind, e.Mesh, Positions: options.FollowFace ? follower.Follow(e.Mesh.Mesh.Positions, head) : e.Mesh.Mesh.Positions))
                .ToList();

            // Hair and beards shade the skin under them: scalp hair strongly, facial hair lightly
            var scalpRoots = equipped.Where(e => e.Kind == AccessoryKind.Hair).SelectMany(e => CharacterColoring.StrandVertices(e.Mesh, e.Positions)).ToList();
            var faceRoots = equipped.Where(e => e.Kind is AccessoryKind.Beard or AccessoryKind.Eyebrows).SelectMany(e => CharacterColoring.StrandVertices(e.Mesh, e.Positions)).ToList();
            if (scalpRoots.Count + faceRoots.Count > 0)
            {
                var scalp = CharacterColoring.SkinOcclusion(head, scalpRoots, options.HairShading);
                var face = CharacterColoring.SkinOcclusion(head, faceRoots, options.FacialHairShading);
                parts[0] = parts[0] with { Occlusion = scalp.Zip(face, (a, b) => a * b).ToArray() };
            }

            foreach (var (kind, item, positions) in equipped)
            {
                var choice = kind switch { AccessoryKind.Beard => character.Beard, AccessoryKind.Eyebrows => character.Eyebrows, _ => character.Hair };
                var colors = CharacterColoring.Hair(item.Mesh.Uvs, choice, item.DefaultMelanin);
                var natural = CharacterColoring.Uniform(positions.Length, CharacterColoring.NaturalHair(choice, item.DefaultMelanin));
                var strands = item.Layers.FirstOrDefault(l => l.Kind == HairLayerKind.Strands);
                var occlusion = strands is null ? null : CharacterColoring.HairOcclusion(positions, head, strands.Shading.AmbientOcclusion);
                foreach (var layer in item.Layers)
                {
                    parts.Add(layer.Kind == HairLayerKind.Cap
                        ? new MeshPart(positions, layer.Indices, natural, new CapSurface(layer.Opacity)) { Uvs = item.Mesh.Uvs }
                        : new MeshPart(positions, layer.Indices, colors, new HairSurface(layer)) { Uvs = item.Mesh.Uvs, Occlusion = occlusion });
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            warn?.Invoke($"Couldn't load hair for this character: {e.Message}");
        }
        return parts;
    }
}

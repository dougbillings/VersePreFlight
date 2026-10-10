using System.Collections.Concurrent;
using System.Numerics;

namespace VersePreflight.Core.Characters;

/// <summary>A hair, beard or eyebrow mesh, split into layers by material.</summary>
public sealed record AccessoryMesh(string Name, MeshGeometry Mesh, IReadOnlyList<HairLayer> Layers)
{
    /// <summary>The style's own default melanin (0 light .. 1 black), used when the character file stores 0.</summary>
    public float DefaultMelanin { get; init; } = 0.9f;
}

/// <summary>What a hair layer is: strand cards, or a "cap" of hair density painted onto the skin underneath.</summary>
public enum HairLayerKind { Strands, Cap }

/// <summary>
/// Triangles (indices into the mesh) drawn with one material. <see cref="Opacity"/> is the strand atlas for
/// strands, or the density map for a cap.
/// </summary>
public sealed record HairLayer(HairLayerKind Kind, int[] Indices, GrayTexture Opacity)
{
    /// <summary>Full width of the opacity texture in the game files (the loaded copy may be a smaller mip).</summary>
    public int OpacityFullSize { get; init; }

    /// <summary>Per-strand random ids, for colour variation.</summary>
    public GrayTexture? Id { get; init; }
    public HairShading Shading { get; init; } = new();
}

/// <summary>Which loadout slots are drawn, in drawing order, and how each is coloured.</summary>
public enum AccessoryKind { Scalp, Eyebrows, Beard, Hair }

/// <summary>
/// Loads the hair, beard, eyebrows and scalp a character has equipped, and their skin material, from the
/// player's own Data.p4k. Item ids are resolved to names through the game database once per game build
/// (cached); meshes and textures are read on demand and kept in memory while the app runs.
/// </summary>
public sealed class AccessoryLibrary : IDisposable
{
    private const string HeadsFolder = @"Data\Objects\Characters\Human\heads\";
    private const int CatalogVersion = 2;

    private static readonly (AccessoryKind Kind, string Port)[] Ports =
    [
        (AccessoryKind.Scalp, "universal_scalp_itemport"),
        (AccessoryKind.Eyebrows, "eyebrow_itemport"),
        (AccessoryKind.Beard, "beard_itemport"),
        (AccessoryKind.Hair, "hair_itemport"),
    ];

    private readonly GameArchive _archive;
    private readonly Dictionary<string, GameArchiveEntry> _byPath;
    private readonly Dictionary<string, GameArchiveEntry> _meshByName;
    private readonly Dictionary<Guid, string> _itemNames;
    private readonly Dictionary<Guid, string> _skinMaterials;
    private readonly ConcurrentDictionary<Guid, SkinMaterial?> _skins = new();
    private readonly ConcurrentDictionary<string, AccessoryMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object?> _textures = new(StringComparer.OrdinalIgnoreCase);

    private AccessoryLibrary(GameArchive archive, List<GameArchiveEntry> entries, Dictionary<Guid, string> itemNames, Dictionary<Guid, string> skinMaterials)
    {
        _archive = archive;
        _byPath = entries.GroupBy(e => e.Path.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        _meshByName = entries.Where(e => e.Path.EndsWith(".skinm", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => ArchivePath.FileNameWithoutExtension(e.Path).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        _itemNames = itemNames;
        _skinMaterials = skinMaterials;
    }

    public int ItemCount => _itemNames.Count;

    /// <summary>Opens the archive and loads (or builds and caches) the item name catalog for this build.</summary>
    public static AccessoryLibrary Open(string p4kPath, string cacheDirectory, string buildKey)
    {
        var archive = GameArchive.Open(p4kPath);
        try
        {
            var entries = archive.FindEntries(p =>
                p.Equals(@"Data\Game2.dcb", StringComparison.OrdinalIgnoreCase) || p.StartsWith(HeadsFolder, StringComparison.OrdinalIgnoreCase));

            var catalogDir = Path.Combine(cacheDirectory, "items");
            var catalogFile = Path.Combine(catalogDir, string.Concat(buildKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".bin");
            var (names, skins) = TryLoadCatalog(catalogFile);
            if (names is null || skins is null)
            {
                var dcb = entries.FirstOrDefault(e => e.Path.Equals(@"Data\Game2.dcb", StringComparison.OrdinalIgnoreCase))
                          ?? throw new FileNotFoundException("Game database (Game2.dcb) not found in the archive.");
                var database = archive.Read(dcb);
                names = GameDatabase.ReadRecordNames(database, f => f.Contains("/head/shared/", StringComparison.OrdinalIgnoreCase));
                skins = GameDatabase.FindMaterialPaths(database, "_head_material.mtl");
                if (Directory.Exists(catalogDir)) foreach (var old in Directory.GetFiles(catalogDir, "*.bin")) File.Delete(old);
                Directory.CreateDirectory(catalogDir);
                SaveCatalog(catalogFile, names, skins);
            }
            return new AccessoryLibrary(archive, entries, names, skins);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>The accessories this character has equipped, for the given body type.</summary>
    public List<(AccessoryKind Kind, AccessoryMesh Mesh)> LoadFor(CharacterFile character, bool female)
    {
        // Record "hair_24" -> mesh "m_hair_24"; the shared scalp record maps to "m_universal_scalp"
        var equipped = new List<(AccessoryKind Kind, string Mesh)>();
        foreach (var (kind, port) in Ports)
        {
            if (!character.Loadout.TryGetValue(CharacterFile.Hash(port), out var id) || !_itemNames.TryGetValue(id, out var item)) continue;
            equipped.Add((kind, (female ? "f_" : "m_") + (item.Equals("shared_scalp_unified", StringComparison.OrdinalIgnoreCase) ? "universal_scalp" : item)));
        }

        // The scalp has no material of its own: it wears the hairstyle's hair cap (its second sub-material).
        // Characters saved before the scalp slot existed still get one under their hair.
        var hair = equipped.FirstOrDefault(e => e.Kind == AccessoryKind.Hair).Mesh;
        if (hair is not null && equipped.All(e => e.Kind != AccessoryKind.Scalp))
            equipped.Insert(0, (AccessoryKind.Scalp, (female ? "f_" : "m_") + "universal_scalp"));
        var result = new List<(AccessoryKind, AccessoryMesh)>();
        foreach (var (kind, meshName) in equipped)
        {
            var materialFrom = kind == AccessoryKind.Scalp ? hair : meshName;
            if (materialFrom is null) continue;
            if (_meshes.GetOrAdd($"{meshName}|{materialFrom}", _ => LoadMesh(meshName, materialFrom)) is { } mesh) result.Add((kind, mesh));
        }
        return result;
    }

    private AccessoryMesh? LoadMesh(string meshName, string materialFrom)
    {
        if (!_meshByName.TryGetValue(meshName.ToLowerInvariant(), out var entry)
            || !_meshByName.TryGetValue(materialFrom.ToLowerInvariant(), out var materialEntry)) return null;
        var mesh = SkinMeshReader.Read(_archive.Read(entry));
        var (materials, melanin) = ReadHairMaterials(materialEntry.Path);
        var submeshes = mesh.Submeshes.Count > 0 ? mesh.Submeshes : [new Submesh(0, 0, mesh.Indices.Length)];

        var layers = new List<HairLayer>();
        foreach (var s in submeshes)
        {
            if (s.MaterialId >= materials.Count || materials[s.MaterialId] is not { } layer) continue;
            layers.Add(layer with { Indices = mesh.Indices.AsSpan(s.FirstIndex, s.IndexCount).ToArray() });
        }
        return layers.Count == 0 ? null : new AccessoryMesh(meshName, mesh, layers) { DefaultMelanin = melanin ?? 0.9f };
    }

    /// <summary>The character's skin material (their chosen head skin), if it can be read.</summary>
    public SkinMaterial? LoadSkinMaterial(Guid headMaterialId) => _skins.GetOrAdd(headMaterialId, id =>
    {
        if (!_skinMaterials.TryGetValue(id, out var mtlPath)) return null;
        var mtl = ReadFile(GamePath(mtlPath));
        if (mtl is null || !CryXml.IsBinary(mtl)) return null;
        var material = CryXml.Parse(mtl).Descendants()
            .FirstOrDefault(e => e.Tag == "Material" && (e["Shader"]?.StartsWith("HumanSkin", StringComparison.OrdinalIgnoreCase) ?? false));
        if (material is null) return null;

        var textures = Textures(material);
        var p = material.Children.FirstOrDefault(c => c.Tag == "PublicParams");
        try
        {
            if (!textures.TryGetValue("TexSlot1", out var diffuse)) return null;
            var normalPath = textures.GetValueOrDefault("TexSlot2");
            var detail = material.Descendants().FirstOrDefault(e => e.Tag == "Texture" && e["Map"] == "TexSlot6");
            var detailMod = detail?.Children.FirstOrDefault(c => c.Tag == "TexMod");
            return new SkinMaterial(TextureReader.ReadColor(ReadFile, DdsPath(diffuse), 2048))
            {
                Normal = Try(() => normalPath is null ? null : TextureReader.ReadRgba(ReadFile, DdsPath(normalPath), 1024)),
                Gloss = Try(() => normalPath is null ? null : TextureReader.ReadChannel(ReadFile, DdsPath(normalPath) + ".a", 1024)),
                Specular = Try(() => textures.GetValueOrDefault("TexSlot4") is { } spec ? TextureReader.ReadRgba(ReadFile, DdsPath(spec), 1024).Channel(0) : null),
                Detail = Try(() => detail?["File"] is { } file ? Cached(DdsPath(file), path => TextureReader.ReadRgba(ReadFile, path, 512)) : null),
                DetailTiling = MaterialParams.Float(detailMod, "TileU", 1),
                SourceAverageColor = MaterialParams.Vector(p, "SourceAverageColor", Vector3.Zero),
                SourceDetail = MaterialParams.Float(p, "SourceDetail", 1),
                Translucency = Try(() => textures.GetValueOrDefault("TexSlot12") is { } trans ? Cached(DdsPath(trans), path => TextureReader.ReadChannel(ReadFile, path, 512)) : null),
                SpecularLevel = MaterialParams.Vector(material, "Specular", new Vector3(0.032f)).X,
                DetailBumpScale = MaterialParams.Float(p, "DetailBumpScale", 1),
                DetailCavityScale = MaterialParams.Float(p, "DetailCavityScale", 0),
                DetailGlossScale = MaterialParams.Float(p, "DetailGlossScale", 0),
                TranslucencyMultiplier = MaterialParams.Float(p, "TranslucencyMultiplier", 0),
            };
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or FileNotFoundException) { return null; }
    });

    /// <summary>
    /// For each sub-material (in material id order), a layer template (indices filled in later), or null
    /// for materials that aren't drawn; plus the style's default melanin.
    /// </summary>
    private (List<HairLayer?> Layers, float? Melanin) ReadHairMaterials(string meshPath)
    {
        var folder = ArchivePath.Directory(meshPath).ToLowerInvariant();
        var baseName = ArchivePath.FileNameWithoutExtension(meshPath).ToLowerInvariant();
        // Prefer "<mesh>.mtl", otherwise any material in the folder named after the mesh (e.g. colour variants)
        var mtl = _byPath.GetValueOrDefault(ArchivePath.Combine(folder, baseName + ".mtl"))
                  ?? _byPath.Values.FirstOrDefault(e => e.Path.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase)
                      && ArchivePath.Directory(e.Path).Equals(folder, StringComparison.OrdinalIgnoreCase)
                      && ArchivePath.FileName(e.Path).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
        if (mtl is null) return ([], null);

        var bytes = _archive.Read(mtl);
        if (!CryXml.IsBinary(bytes)) return ([], null);
        var root = CryXml.Parse(bytes);
        var subs = root.Children.FirstOrDefault(c => c.Tag.Equals("SubMaterials", StringComparison.OrdinalIgnoreCase))?.Children ?? [root];
        float? melanin = null;
        var layers = new List<HairLayer?>();
        foreach (var sub in subs)
        {
            var textures = Textures(sub);
            var p = sub.Children.FirstOrDefault(c => c.Tag == "PublicParams");
            var cap = sub["StringGenMask"]?.Contains("HAIR_CAP", StringComparison.OrdinalIgnoreCase) ?? false;
            var opacity = textures.GetValueOrDefault("TexSlot1");
            HairLayer? layer = null;
            if (cap && opacity is not null)
            {
                // Density is in alpha (BC7 caps repeat it in every channel; DXT5 caps are white with alpha)
                if (Try(() => Cached(DdsPath(opacity) + "#alpha", _ => TextureReader.ReadRgba(ReadFile, DdsPath(opacity), 512).Channel(3))) is { } density)
                    layer = new HairLayer(HairLayerKind.Cap, [], density);
            }
            else if (opacity is not null && opacity.Contains("_opac", StringComparison.OrdinalIgnoreCase)
                     && Try(() => Cached(DdsPath(opacity), path => TextureReader.ReadChannel(ReadFile, path, 1024))) is { } strands)
            {
                melanin ??= p?["BaseMelanin"] is not null ? MaterialParams.Float(p, "BaseMelanin", 0.9f) : null;
                layer = new HairLayer(HairLayerKind.Strands, [], strands)
                {
                    OpacityFullSize = Try(() => TextureReader.ReadSize(ReadFile, DdsPath(opacity)).Width) ?? strands.Width,
                    Id = textures.GetValueOrDefault("TexSlot4") is { } id ? Try(() => Cached(DdsPath(id), path => TextureReader.ReadChannel(ReadFile, path, 512))) : null,
                    Shading = new HairShading
                    {
                        Smoothness = MaterialParams.Float(p, "Smoothness", 0.6f),
                        SpecularMultiplier = MaterialParams.Float(p, "SpecularMultiplier", 1),
                        OpacityMipScale = MaterialParams.Float(p, "OpacityMipScale", 3),
                        AmbientOcclusion = MaterialParams.Float(p, "AmbientOcclusion", 2.5f),
                        ShadowDensity = MaterialParams.Float(p, "ShadowDensity", 3),
                        MelaninVariation = MaterialParams.Float(p, "BaseMelaninVariation", 0),
                        PigmentVariation = MaterialParams.Float(p, "DyePigmentVariation", 0),
                    },
                };
            }
            layers.Add(layer);
        }
        return (layers, melanin);
    }

    private static Dictionary<string, string> Textures(CryXmlElement material) =>
        material.Children.Where(c => c.Tag == "Textures").SelectMany(t => t.Children)
            .Where(t => t["Map"] is not null && t["File"] is not null)
            .GroupBy(t => t["Map"]!).ToDictionary(g => g.Key, g => g.First()["File"]!);

    private static string GamePath(string path) => ("data\\" + path).Replace('/', '\\');
    private static string DdsPath(string texture) => GamePath(Path.ChangeExtension(texture, ".dds"));

    private T? Cached<T>(string key, Func<string, T?> load) where T : class => (T?)_textures.GetOrAdd(key, k => load(k.Split('#')[0]));

    private static T? Try<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or FileNotFoundException) { return null; }
    }

    private static int? Try(Func<int> read)
    {
        try { return read(); }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or FileNotFoundException) { return null; }
    }

    private byte[]? ReadFile(string path) => _byPath.TryGetValue(path.ToLowerInvariant(), out var e) ? _archive.Read(e) : null;

    // Catalog file: version, then item names, then skin material paths (both id -> string)
    private static (Dictionary<Guid, string>? Names, Dictionary<Guid, string>? Skins) TryLoadCatalog(string file)
    {
        if (!File.Exists(file)) return (null, null);
        try
        {
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadInt32() != CatalogVersion) return (null, null);
            return (ReadMap(r), ReadMap(r));
        }
        catch (Exception e) when (e is IOException or EndOfStreamException) { return (null, null); }

        static Dictionary<Guid, string> ReadMap(BinaryReader r)
        {
            var count = r.ReadInt32();
            var map = new Dictionary<Guid, string>(count);
            for (var i = 0; i < count; i++) map[new Guid(r.ReadBytes(16))] = r.ReadString();
            return map;
        }
    }

    private static void SaveCatalog(string file, Dictionary<Guid, string> names, Dictionary<Guid, string> skins)
    {
        using var w = new BinaryWriter(File.Create(file));
        w.Write(CatalogVersion);
        foreach (var map in new[] { names, skins })
        {
            w.Write(map.Count);
            foreach (var (id, name) in map) { w.Write(id.ToByteArray()); w.Write(name); }
        }
    }

    public void Dispose() => _archive.Dispose();
}

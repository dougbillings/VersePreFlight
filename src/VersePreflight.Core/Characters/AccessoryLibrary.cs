using System.Collections.Concurrent;

namespace VersePreflight.Core.Characters;

/// <summary>
/// A hair, beard or eyebrow mesh. <see cref="Layers"/> are the strand cards, each with its opacity
/// texture; skin-shadow and scalp layers are left out for now.
/// </summary>
public sealed record AccessoryMesh(string Name, MeshGeometry Mesh, IReadOnlyList<StrandLayer> Layers);

/// <summary>Triangles (indices into the mesh) drawn with one strand opacity texture.</summary>
public sealed record StrandLayer(int[] Indices, GrayTexture Opacity);

/// <summary>Which loadout slots are drawn, in drawing order, and how each is coloured.</summary>
public enum AccessoryKind { Scalp, Eyebrows, Beard, Hair }

/// <summary>
/// Loads the hair, beard, eyebrows and scalp a character has equipped, from the player's own Data.p4k.
/// Item ids are resolved to names through the game database once per game build (cached); meshes and
/// textures are read on demand and kept in memory while the app runs.
/// </summary>
public sealed class AccessoryLibrary : IDisposable
{
    private const string SharedFolder = @"Data\Objects\Characters\Human\heads\shared\";
    private const int CatalogVersion = 1;

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
    private readonly ConcurrentDictionary<string, AccessoryMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GrayTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);

    private AccessoryLibrary(GameArchive archive, List<GameArchiveEntry> entries, Dictionary<Guid, string> itemNames)
    {
        _archive = archive;
        _byPath = entries.GroupBy(e => e.Path.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        _meshByName = entries.Where(e => e.Path.EndsWith(".skinm", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => Path.GetFileNameWithoutExtension(e.Path).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        _itemNames = itemNames;
    }

    public int ItemCount => _itemNames.Count;

    /// <summary>Opens the archive and loads (or builds and caches) the item name catalog for this build.</summary>
    public static AccessoryLibrary Open(string p4kPath, string cacheDirectory, string buildKey)
    {
        var archive = GameArchive.Open(p4kPath);
        try
        {
            var entries = archive.FindEntries(p =>
                p.Equals(@"Data\Game2.dcb", StringComparison.OrdinalIgnoreCase) || p.StartsWith(SharedFolder, StringComparison.OrdinalIgnoreCase));

            var catalogDir = Path.Combine(cacheDirectory, "items");
            var catalogFile = Path.Combine(catalogDir, string.Concat(buildKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".bin");
            var names = TryLoadCatalog(catalogFile);
            if (names is null)
            {
                var dcb = entries.FirstOrDefault(e => e.Path.Equals(@"Data\Game2.dcb", StringComparison.OrdinalIgnoreCase))
                          ?? throw new FileNotFoundException("Game database (Game2.dcb) not found in the archive.");
                names = GameDatabase.ReadRecordNames(archive.Read(dcb), f => f.Contains("/head/shared/", StringComparison.OrdinalIgnoreCase));
                if (Directory.Exists(catalogDir)) foreach (var old in Directory.GetFiles(catalogDir, "*.bin")) File.Delete(old);
                Directory.CreateDirectory(catalogDir);
                SaveCatalog(catalogFile, names);
            }
            return new AccessoryLibrary(archive, entries, names);
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
        var result = new List<(AccessoryKind, AccessoryMesh)>();
        foreach (var (kind, port) in Ports)
        {
            if (!character.Loadout.TryGetValue(CharacterFile.Hash(port), out var id) || !_itemNames.TryGetValue(id, out var item)) continue;
            // Record "hair_24" -> mesh "m_hair_24"; the shared scalp record maps to "m_universal_scalp"
            var meshName = (female ? "f_" : "m_") + (item.Equals("shared_scalp_unified", StringComparison.OrdinalIgnoreCase) ? "universal_scalp" : item);
            if (_meshes.GetOrAdd(meshName, LoadMesh) is { } mesh) result.Add((kind, mesh));
        }
        return result;
    }

    private AccessoryMesh? LoadMesh(string meshName)
    {
        if (!_meshByName.TryGetValue(meshName.ToLowerInvariant(), out var entry)) return null;
        var mesh = SkinMeshReader.Read(_archive.Read(entry));
        var materials = ReadSubMaterialOpacity(entry.Path);
        var submeshes = mesh.Submeshes.Count > 0 ? mesh.Submeshes : [new Submesh(0, 0, mesh.Indices.Length)];

        // One layer per submesh whose material has a strand opacity texture; other layers (skin
        // shadow, scalp caps) need skin blending and aren't drawn yet
        var layers = new List<StrandLayer>();
        foreach (var s in submeshes)
        {
            if (s.MaterialId >= materials.Count || materials[s.MaterialId] is not { } opacity) continue;
            layers.Add(new StrandLayer(mesh.Indices.AsSpan(s.FirstIndex, s.IndexCount).ToArray(), opacity));
        }
        return layers.Count == 0 ? null : new AccessoryMesh(meshName, mesh, layers);
    }

    /// <summary>For each sub-material (in material id order), its strand opacity texture, or null.</summary>
    private List<GrayTexture?> ReadSubMaterialOpacity(string meshPath)
    {
        var folder = Path.GetDirectoryName(meshPath)!.ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(meshPath).ToLowerInvariant();
        // Prefer "<mesh>.mtl", otherwise any material in the folder named after the mesh (e.g. colour variants)
        var mtl = _byPath.GetValueOrDefault(Path.Combine(folder, baseName + ".mtl"))
                  ?? _byPath.Values.FirstOrDefault(e => e.Path.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase)
                      && Path.GetDirectoryName(e.Path)!.Equals(folder, StringComparison.OrdinalIgnoreCase)
                      && Path.GetFileName(e.Path).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
        if (mtl is null) return [];

        var bytes = _archive.Read(mtl);
        if (!CryXml.IsBinary(bytes)) return [];
        var root = CryXml.Parse(bytes);
        var subs = root.Children.FirstOrDefault(c => c.Tag.Equals("SubMaterials", StringComparison.OrdinalIgnoreCase))?.Children ?? [root];

        return subs.Select(sub =>
        {
            var file = sub.Descendants()
                .Where(e => e.Tag.Equals("Texture", StringComparison.OrdinalIgnoreCase))
                .Select(e => e["File"])
                .FirstOrDefault(f => f is not null && f.Contains("_opac", StringComparison.OrdinalIgnoreCase));
            if (file is null) return null;
            var dds = ("data\\" + Path.ChangeExtension(file, ".dds")).Replace('/', '\\').ToLowerInvariant();
            return _textures.GetOrAdd(dds, path =>
            {
                try { return TextureReader.ReadChannel(ReadFile, path, 1024); }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException or FileNotFoundException) { return null; }
            });
        }).ToList();
    }

    private byte[]? ReadFile(string path) => _byPath.TryGetValue(path.ToLowerInvariant(), out var e) ? _archive.Read(e) : null;

    private static Dictionary<Guid, string>? TryLoadCatalog(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadInt32() != CatalogVersion) return null;
            var count = r.ReadInt32();
            var names = new Dictionary<Guid, string>(count);
            for (var i = 0; i < count; i++) names[new Guid(r.ReadBytes(16))] = r.ReadString();
            return names;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException) { return null; }
    }

    private static void SaveCatalog(string file, Dictionary<Guid, string> names)
    {
        using var w = new BinaryWriter(File.Create(file));
        w.Write(CatalogVersion);
        w.Write(names.Count);
        foreach (var (id, name) in names) { w.Write(id.ToByteArray()); w.Write(name); }
    }

    public void Dispose() => _archive.Dispose();
}

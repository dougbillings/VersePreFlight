using System.Numerics;
using System.Text;

namespace VersePreflight.Core.Characters;

/// <summary>
/// Everything needed to rebuild faces for one body type: the shared mesh, the preset head shapes
/// (aligned to the base head) and the face region mask. Built from the player's own Data.p4k and
/// cached locally; nothing from the game is shipped with Verse Preflight.
/// </summary>
public sealed class HeadLibrary
{
    private const string CacheMagic = "VPHEAD";
    private const int CacheVersion = 1;
    private const string HeadsFolder = @"Data\Objects\Characters\Human\heads\";

    internal HeadLibrary(string name, uint bodyHash, int[] indices, Vector3[]?[] slots, float[] mask, int regionCount)
    {
        Name = name; BodyHash = bodyHash; Indices = indices; Slots = slots; Mask = mask; RegionCount = regionCount;
    }

    /// <summary>The base head's name, e.g. "protos_human_male_face_t1_pu".</summary>
    public string Name { get; }
    public uint BodyHash { get; }
    public int[] Indices { get; }
    /// <summary>Head shapes by head_id; slot 0 is the base head. Null where the game has no mesh.</summary>
    public Vector3[]?[] Slots { get; }
    public float[] Mask { get; }
    public int RegionCount { get; }
    public int VertexCount => Slots[0]!.Length;
    public Vector3[] BasePositions => Slots[0]!;
    public bool IsFemale => Name.Contains("female", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuilds a character's head: each vertex mixes the preset heads chosen for each face region,
    /// weighted by how much the vertex belongs to that region. Regions without a recipe keep the base shape.
    /// </summary>
    public Vector3[] Blend(CharacterFile character)
    {
        var verts = VertexCount;
        var result = new Vector3[verts];
        for (var v = 0; v < verts; v++)
        {
            var p = Vector3.Zero;
            for (var r = 0; r < RegionCount; r++)
            {
                var m = Mask[r * verts + v];
                if (m == 0) continue;
                var recipe = r < character.FaceParts.Count ? character.FaceParts[r] : [];
                Vector3 regionPos = Vector3.Zero; float total = 0;
                foreach (var b in recipe)
                {
                    var shape = b.HeadId < Slots.Length ? Slots[b.HeadId] ?? BasePositions : BasePositions;
                    regionPos += b.Weight * shape[v];
                    total += b.Weight;
                }
                p += m * (total > 0 ? regionPos / total : BasePositions[v]);
            }
            result[v] = p;
        }
        return result;
    }

    /// <summary>Reads the head data for every body type found in the archive.</summary>
    public static List<HeadLibrary> BuildFromArchive(string p4kPath, IProgress<string>? progress = null)
    {
        using var archive = GameArchive.Open(p4kPath);
        progress?.Report("Indexing game files...");
        var entries = archive.FindEntries(p =>
            p.StartsWith(HeadsFolder, StringComparison.OrdinalIgnoreCase) &&
            (p.EndsWith("_head.skinm", StringComparison.OrdinalIgnoreCase) || p.EndsWith("_face_t1_pu_head.dna", StringComparison.OrdinalIgnoreCase)));

        var meshes = entries.Where(e => e.Path.EndsWith(".skinm", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => Path.GetFileName(e.Path).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var libraries = new List<HeadLibrary>();
        foreach (var dnaEntry in entries.Where(e => e.Path.EndsWith(".dna", StringComparison.OrdinalIgnoreCase)))
        {
            var baseName = Path.GetFileName(dnaEntry.Path)[..^"_head.dna".Length];
            progress?.Report($"Reading {baseName}...");
            var dna = HeadDnaFile.Parse(archive.Read(dnaEntry));
            if (!meshes.TryGetValue((baseName + "_head.skinm").ToLowerInvariant(), out var baseEntry)) continue;

            var baseMesh = SkinMeshReader.Read(archive.Read(baseEntry));
            if (baseMesh.Positions.Length != dna.VertexCount) continue;

            var slots = new Vector3[]?[dna.HeadSlots.Count];
            slots[0] = baseMesh.Positions;
            for (var i = 1; i < slots.Length; i++)
            {
                if (!meshes.TryGetValue((dna.HeadSlots[i] + "_head.skinm").ToLowerInvariant(), out var e)) continue;
                var mesh = SkinMeshReader.Read(archive.Read(e));
                if (mesh.Positions.Length != dna.VertexCount) continue;
                slots[i] = Align(mesh.Positions, baseMesh.Positions);
            }
            libraries.Add(new HeadLibrary(baseName, dna.BodyHash, baseMesh.Indices, slots, dna.Mask, dna.RegionCount));
        }
        return libraries;
    }

    // Some presets are stored with a global offset; remove the mean offset from the base head
    internal static Vector3[] Align(Vector3[] head, Vector3[] baseHead)
    {
        var offset = Vector3.Zero;
        for (var i = 0; i < head.Length; i++) offset += head[i] - baseHead[i];
        offset /= head.Length;
        return head.Select(p => p - offset).ToArray();
    }

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(CacheMagic); w.Write(CacheVersion);
        w.Write(Name); w.Write(BodyHash); w.Write(RegionCount);
        w.Write(Indices.Length); foreach (var i in Indices) w.Write(i);
        w.Write(VertexCount);
        w.Write(Slots.Length);
        foreach (var slot in Slots)
        {
            w.Write(slot is not null);
            if (slot is null) continue;
            foreach (var p in slot) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        }
        foreach (var m in Mask) w.Write(m);
    }

    public static HeadLibrary Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != CacheMagic || r.ReadInt32() != CacheVersion) throw new InvalidDataException("Unsupported head cache.");
        var name = r.ReadString(); var hash = r.ReadUInt32(); var regions = r.ReadInt32();
        var indices = new int[r.ReadInt32()]; for (var i = 0; i < indices.Length; i++) indices[i] = r.ReadInt32();
        var verts = r.ReadInt32();
        var slots = new Vector3[]?[r.ReadInt32()];
        for (var s = 0; s < slots.Length; s++)
        {
            if (!r.ReadBoolean()) continue;
            var pos = new Vector3[verts];
            for (var i = 0; i < verts; i++) pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            slots[s] = pos;
        }
        var mask = new float[regions * verts]; for (var i = 0; i < mask.Length; i++) mask[i] = r.ReadSingle();
        return new HeadLibrary(name, hash, indices, slots, mask, regions);
    }
}

/// <summary>Keeps head libraries cached per game build, so the archive is only read once per patch.</summary>
public sealed class HeadLibraryCache(string cacheDirectory)
{
    /// <summary>Loads the cached libraries for this build, or builds and caches them from the archive.</summary>
    public List<HeadLibrary> GetOrBuild(string p4kPath, string buildKey, IProgress<string>? progress = null)
    {
        var dir = Path.Combine(cacheDirectory, "heads", Sanitize(buildKey));
        if (Directory.Exists(dir))
        {
            try
            {
                var cached = Directory.GetFiles(dir, "*.vphead").Select(f => { using var s = File.OpenRead(f); return HeadLibrary.Load(s); }).ToList();
                if (cached.Count > 0) return cached;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { /* rebuild below */ }
        }

        var libraries = HeadLibrary.BuildFromArchive(p4kPath, progress);
        // Old builds' caches are no longer needed
        var root = Path.Combine(cacheDirectory, "heads");
        if (Directory.Exists(root))
            foreach (var old in Directory.GetDirectories(root)) try { Directory.Delete(old, recursive: true); } catch (IOException) { }
        Directory.CreateDirectory(dir);
        foreach (var lib in libraries)
        {
            using var s = File.Create(Path.Combine(dir, lib.Name + ".vphead"));
            lib.Save(s);
        }
        return libraries;
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

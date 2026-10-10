using System.Numerics;
using System.Text;

namespace VersePreflight.Core.Characters;

/// <summary>
/// A mesh that can be reshaped per character: a base shape, preset shapes by head id (aligned to the
/// base) and a per-vertex face region mask.
/// </summary>
public sealed class BlendSet
{
    internal BlendSet(int[] indices, Vector3[]?[] slots, float[] mask, int regionCount, Vector2[]? uvs = null)
    {
        Indices = indices; Slots = slots; Mask = mask; RegionCount = regionCount;
        Uvs = uvs ?? new Vector2[slots[0]!.Length];
    }

    /// <summary>Texture coordinates of the shared mesh (the same for every preset).</summary>
    public Vector2[] Uvs { get; }

    public int[] Indices { get; }
    /// <summary>Shapes by head_id; slot 0 is the base. Null where the game has no mesh for that preset.</summary>
    public Vector3[]?[] Slots { get; }
    public float[] Mask { get; }
    public int RegionCount { get; }
    public int VertexCount => Slots[0]!.Length;
    public Vector3[] BasePositions => Slots[0]!;

    /// <summary>
    /// Each vertex mixes the preset shapes chosen for each face region, weighted by how much the vertex
    /// belongs to that region. Regions without a recipe keep the base shape.
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

    internal void Save(BinaryWriter w)
    {
        w.Write(RegionCount);
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
        foreach (var uv in Uvs) { w.Write(uv.X); w.Write(uv.Y); }
    }

    internal static BlendSet Load(BinaryReader r)
    {
        var regions = r.ReadInt32();
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
        var uvs = new Vector2[verts]; for (var i = 0; i < verts; i++) uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        return new BlendSet(indices, slots, mask, regions, uvs);
    }
}

/// <summary>
/// Everything needed to rebuild faces for one body type: the head and the eyes as blend sets.
/// Built from the player's own Data.p4k and cached locally; nothing from the game is shipped.
/// </summary>
public sealed class HeadLibrary
{
    private const string CacheMagic = "VPHEAD";
    private const int CacheVersion = 4;
    private const string HeadsFolder = @"Data\Objects\Characters\Human\heads\";

    internal HeadLibrary(string name, uint bodyHash, BlendSet head, BlendSet? eyes)
    {
        Name = name; BodyHash = bodyHash; Head = head; Eyes = eyes;
    }

    /// <summary>The base head's name, e.g. "protos_human_male_face_t1_pu".</summary>
    public string Name { get; }
    public uint BodyHash { get; }
    public BlendSet Head { get; }
    public BlendSet? Eyes { get; }
    public bool IsFemale => Name.Contains("female", StringComparison.OrdinalIgnoreCase);

    // Shorthands for the head mesh
    public int[] Indices => Head.Indices;
    public Vector3[] BasePositions => Head.BasePositions;
    public int VertexCount => Head.VertexCount;
    public Vector3[] Blend(CharacterFile character) => Head.Blend(character);

    /// <summary>Reads the head data for every body type found in the archive.</summary>
    public static List<HeadLibrary> BuildFromArchive(string p4kPath, IProgress<string>? progress = null)
    {
        using var archive = GameArchive.Open(p4kPath);
        progress?.Report("Indexing game files...");
        var entries = archive.FindEntries(p =>
            p.StartsWith(HeadsFolder, StringComparison.OrdinalIgnoreCase) &&
            (p.EndsWith("_head.skinm", StringComparison.OrdinalIgnoreCase) || p.EndsWith("_eyes.skinm", StringComparison.OrdinalIgnoreCase) ||
             p.EndsWith("_face_t1_pu_head.dna", StringComparison.OrdinalIgnoreCase) || p.EndsWith("_face_t1_pu_eyes.dna", StringComparison.OrdinalIgnoreCase)));
        var byName = entries.GroupBy(e => Path.GetFileName(e.Path).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        MeshGeometry? Mesh(string fileName) =>
            byName.TryGetValue(fileName.ToLowerInvariant(), out var e) ? SkinMeshReader.Read(archive.Read(e)) : null;

        var libraries = new List<HeadLibrary>();
        foreach (var dnaEntry in entries.Where(e => e.Path.EndsWith("_head.dna", StringComparison.OrdinalIgnoreCase)))
        {
            var baseName = Path.GetFileName(dnaEntry.Path)[..^"_head.dna".Length];
            progress?.Report($"Reading {baseName}...");
            var dna = HeadDnaFile.Parse(archive.Read(dnaEntry));
            if (Mesh(baseName + "_head.skinm") is not { } baseHead || baseHead.Positions.Length != dna.VertexCount) continue;

            // Head presets, used as stored: they're registered to the base head (their necks line up within
            // about a millimetre), as the game's linear blend needs
            var headSlots = new Vector3[]?[dna.HeadSlots.Count];
            headSlots[0] = baseHead.Positions;
            for (var i = 1; i < headSlots.Length; i++)
            {
                if (Mesh(dna.HeadSlots[i] + "_head.skinm") is not { } mesh || mesh.Positions.Length != dna.VertexCount) continue;
                headSlots[i] = mesh.Positions;
            }
            var head = new BlendSet(baseHead.Indices, headSlots, dna.Mask, dna.RegionCount, baseHead.Uvs);

            // Eyes: each preset has its own eye mesh. The eye DNA file isn't a region mask, but every eye
            // vertex belongs entirely to the left or right eye region, so build that mask from the head's.
            BlendSet? eyes = null;
            if (Mesh(baseName + "_eyes.skinm") is { } baseEyes)
            {
                var eyeSlots = new Vector3[]?[headSlots.Length];
                eyeSlots[0] = baseEyes.Positions;
                for (var i = 1; i < eyeSlots.Length; i++)
                {
                    if (headSlots[i] is null) continue; // head preset missing: eyes fall back to base
                    if (Mesh(dna.HeadSlots[i] + "_eyes.skinm") is not { } m || m.Positions.Length != baseEyes.Positions.Length) continue;
                    eyeSlots[i] = m.Positions;
                }
                eyes = new BlendSet(baseEyes.Indices, eyeSlots, EyeMask(baseEyes.Positions, baseHead.Positions, dna), dna.RegionCount, baseEyes.Uvs);
            }
            libraries.Add(new HeadLibrary(baseName, dna.BodyHash, head, eyes));
        }
        return libraries;
    }

    internal const int RegionEyeLeft = 2, RegionEyeRight = 3;

    /// <summary>Assigns each eye vertex fully to the left or right eye region, by which eye it's nearer.</summary>
    internal static float[] EyeMask(Vector3[] eyes, Vector3[] head, HeadDnaFile dna)
    {
        Vector3 RegionCentre(int region)
        {
            var sum = Vector3.Zero; float total = 0;
            for (var v = 0; v < dna.VertexCount; v++) { var w = dna.Weight(region, v); sum += w * head[v]; total += w; }
            return sum / total;
        }
        var left = RegionCentre(RegionEyeLeft);
        var right = RegionCentre(RegionEyeRight);
        var mask = new float[dna.RegionCount * eyes.Length];
        for (var v = 0; v < eyes.Length; v++)
        {
            var region = Vector3.Distance(eyes[v], left) <= Vector3.Distance(eyes[v], right) ? RegionEyeLeft : RegionEyeRight;
            mask[region * eyes.Length + v] = 1;
        }
        return mask;
    }

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(CacheMagic); w.Write(CacheVersion);
        w.Write(Name); w.Write(BodyHash);
        Head.Save(w);
        w.Write(Eyes is not null);
        Eyes?.Save(w);
    }

    public static HeadLibrary Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != CacheMagic || r.ReadInt32() != CacheVersion) throw new InvalidDataException("Unsupported head cache.");
        var name = r.ReadString(); var hash = r.ReadUInt32();
        var head = BlendSet.Load(r);
        var eyes = r.ReadBoolean() ? BlendSet.Load(r) : null;
        return new HeadLibrary(name, hash, head, eyes);
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
        // Old builds' caches (and older cache formats) are no longer needed
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

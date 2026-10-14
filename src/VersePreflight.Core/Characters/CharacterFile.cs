using System.Numerics;
using System.Text;
using ZstdSharp;

namespace VersePreflight.Core.Characters;

/// <summary>One face part's recipe: up to four preset heads and how much of each.</summary>
public readonly record struct DnaBlend(int HeadId, float Weight);

/// <summary>An sRGB colour from a character file, 0-255 per channel.</summary>
public readonly record struct CharacterColor(byte R, byte G, byte B)
{
    public Vector3 ToVector() => new(R / 255f, G / 255f, B / 255f);
    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>Hair, beard or eyebrow choice: the style (hashed name) and how it's coloured.</summary>
public sealed record HairChoice(uint StyleHash, CharacterColor DyeColor, float DyeAmount, float Melanin, float Redness)
{
    /// <summary>Second dye colour (the dye fades from the first to the second along the strands).</summary>
    public CharacterColor DyeColor2 { get; init; }

    /// <summary>Where along the strand the dye fades from the first colour to the second (0 root .. 1 tip).</summary>
    public float DyeFadeout { get; init; } = 0.5f;
}

/// <summary>
/// The parts of a custom character (<c>.chf</c>) that Verse Preflight reads: body type, face DNA,
/// colours and hair choices. Format knowledge from StarBreaker's starbreaker-chf (MIT, diogotr7).
/// Files are only read, never written.
/// </summary>
public sealed record CharacterFile(uint BodyHash, IReadOnlyList<DnaBlend[]> FaceParts)
{
    public const int FileSize = 4096;
    private const ushort Magic = 0x4242; // "BB"

    // Character files are shared online, so every size and count is checked before it's trusted.
    // Real payloads unpack to about 2 KB and the loadout is a few levels deep.
    internal const int MaxPayloadSize = 64 * 1024;
    internal const int MaxLoadoutDepth = 16;

    public CharacterColor? SkinColor { get; init; }

    /// <summary>The chosen head skin material (the customizer's skin texture), or empty.</summary>
    public Guid HeadMaterialId { get; init; }
    public CharacterColor? EyeColor { get; init; }
    public HairChoice? Hair { get; init; }
    public HairChoice? Beard { get; init; }
    public HairChoice? Eyebrows { get; init; }

    /// <summary>Equipped items by item port name hash (e.g. hair_itemport -> item GUID).</summary>
    public IReadOnlyDictionary<uint, Guid> Loadout { get; init; } = new Dictionary<uint, Guid>();

    public static CharacterFile Read(string path) => Parse(File.ReadAllBytes(path));

    public static CharacterFile Parse(byte[] file)
    {
        if (file.Length != FileSize || BitConverter.ToUInt16(file, 0) != Magic)
            throw new InvalidDataException("Not a Star Citizen custom character file.");

        // Header: magic(2) unknown(2) crc32c(4) compressed size(4) uncompressed size(4); crc covers bytes 16..4096
        var expectedCrc = BitConverter.ToUInt32(file, 4);
        if (Crc32C(file.AsSpan(16)) != expectedCrc) throw new InvalidDataException("Character file is damaged (checksum mismatch).");
        var compressed = BitConverter.ToInt32(file, 8);
        if (compressed is <= 0 or > FileSize - 16) throw new InvalidDataException("Character file is damaged (bad size).");
        byte[] payload;
        try
        {
            using var decompressor = new Decompressor();
            payload = decompressor.Unwrap(file.AsSpan(16, compressed), MaxPayloadSize).ToArray();
        }
        catch (ZstdException e)
        {
            throw new InvalidDataException($"Character file is damaged ({e.Message}).", e);
        }
        return ParsePayload(payload);
    }

    internal static CharacterFile ParsePayload(byte[] d)
    {
        // version(4) version(4) body type guid(16) voice guid(16), then u64 DNA size and the DNA block
        var p = 4 + 4 + 16 + 16;
        var dnaSize = CheckedCount(BitConverter.ToUInt64(d, p), 1, d, p + 8);
        p += 8;
        var (bodyHash, parts) = ParseDna(d.AsSpan(p, dnaSize));
        p += dnaSize;

        var character = new CharacterFile(bodyHash, parts);
        try
        {
            return ParseAppearance(character, d, p);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or ArgumentException or InvalidDataException)
        {
            return character; // the face shape is still usable if the rest has an unexpected layout
        }
    }

    private static (uint BodyHash, DnaBlend[][] Parts) ParseDna(ReadOnlySpan<byte> dna)
    {
        // "dna matrix 1.0" hash(4), body hash(4), variant hash(4), zero(4),
        // part count(2), blends per part(2), unknown(2), max head id(2), then blends
        var bodyHash = BitConverter.ToUInt32(dna[4..]);
        var partCount = BitConverter.ToUInt16(dna[16..]);
        var blendsPerPart = BitConverter.ToUInt16(dna[18..]);
        if (partCount is 0 or > 32 || blendsPerPart != 4) throw new InvalidDataException("Unexpected face DNA layout.");

        var parts = Enumerable.Range(0, partCount).Select(_ => new List<DnaBlend>()).ToArray();
        // Stored interleaved: entry i belongs to part i % partCount
        for (var i = 0; i < partCount * blendsPerPart; i++)
        {
            var value = BitConverter.ToUInt16(dna[(24 + i * 4)..]);
            var headId = BitConverter.ToUInt16(dna[(26 + i * 4)..]);
            if (value > 0) parts[i % partCount].Add(new DnaBlend(headId, value / 65535f));
        }
        return (bodyHash, parts.Select(l => l.ToArray()).ToArray());
    }

    private static CharacterFile ParseAppearance(CharacterFile character, byte[] d, int p)
    {
        // Loadout: u64 total port count, then a tree of (name hash, item guid, child count, tail count, children...)
        p += 8;
        var loadout = new Dictionary<uint, Guid>();
        ReadItemPort(d, ref p, loadout, depth: 0);
        if (BitConverter.ToUInt32(d, p) != 5) throw new InvalidDataException("Unexpected material marker.");
        p += 4;

        var materials = new Dictionary<uint, List<SubMaterial>>();
        var materialIds = new Dictionary<uint, Guid>();
        while (p + 4 <= d.Length && BitConverter.ToUInt32(d, p) != 0)
        {
            var (name, id, subs) = ReadMaterial(d, ref p);
            materials[name] = subs;
            materialIds[name] = id;
        }

        CharacterColor? Color(uint material, uint param) =>
            materials.TryGetValue(material, out var subs) && subs.Count > 0 && subs[0].Colors.TryGetValue(param, out var c) ? c : null;

        HairChoice? Hair(uint material)
        {
            if (!materials.TryGetValue(material, out var subs) || subs.Count == 0) return null;
            var s = subs[0];
            float F(string n, float fallback) => s.Floats.TryGetValue(Hash(n), out var v) ? v : fallback;
            return new HairChoice(s.Name, s.Colors.GetValueOrDefault(Ids.HairDyeColor1), F("DyeAmount", 0), F("BaseMelanin", 0.5f), F("BaseMelaninRedness", 0)) { DyeColor2 = s.Colors.GetValueOrDefault(Ids.HairDyeColor2), DyeFadeout = F("DyeFadeout", 0.5f) };
        }

        return character with
        {
            Loadout = loadout,
            SkinColor = Color(Ids.HeadMaterial, Ids.BodyColor),
            HeadMaterialId = materialIds.GetValueOrDefault(Ids.HeadMaterial),
            EyeColor = Color(Ids.EyeMaterial, Ids.EyeColor),
            Hair = Hair(Ids.HairDyeMaterial),
            Beard = Hair(Ids.BeardDyeMaterial),
            Eyebrows = Hair(Ids.EyebrowDyeMaterial),
        };
    }

    private static void ReadItemPort(byte[] d, ref int p, Dictionary<uint, Guid> loadout, int depth)
    {
        // A recursion limit: too deep a tree would overflow the stack, which can't be caught
        if (depth > MaxLoadoutDepth) throw new InvalidDataException("Unexpected loadout layout.");
        var name = BitConverter.ToUInt32(d, p);
        var id = new Guid(d.AsSpan(p + 4, 16));
        var children = BitConverter.ToUInt32(d, p + 20);
        p += 28; // name, guid, child count, tail count
        if (id != Guid.Empty) loadout[name] = id;
        if (children > 64) throw new InvalidDataException("Unexpected loadout layout.");
        for (var i = 0; i < children; i++) ReadItemPort(d, ref p, loadout, depth + 1);
    }

    private sealed record SubMaterial(uint Name, Dictionary<uint, float> Floats, Dictionary<uint, CharacterColor> Colors);

    private static (uint Name, Guid Id, List<SubMaterial> Subs) ReadMaterial(byte[] d, ref int p)
    {
        // name hash, base material guid, flags, empty guid, sub-material count, marker 5
        var name = BitConverter.ToUInt32(d, p);
        var id = new Guid(d.AsSpan(p + 4, 16));
        var subCount = BitConverter.ToUInt32(d, p + 4 + 16 + 4 + 16);
        p += 4 + 16 + 4 + 16 + 4 + 4;
        subCount = (uint)CheckedCount(subCount, 24, d, p); // each sub-material is at least 24 bytes
        var subs = new List<SubMaterial>();
        for (var i = 0; i < subCount; i++)
        {
            var subName = BitConverter.ToUInt32(d, p);
            var textures = CheckedCount(BitConverter.ToUInt32(d, p + 4), 21, d, p + 8);
            p += 8 + textures * 21; // texture: zero(4) index(1) guid(16)
            var floats = new Dictionary<uint, float>();
            var floatCount = CheckedCount(BitConverter.ToUInt64(d, p), 12, d, p + 8); p += 8;
            for (var f = 0; f < floatCount; f++, p += 12) floats[BitConverter.ToUInt32(d, p)] = BitConverter.ToSingle(d, p + 4);
            var colors = new Dictionary<uint, CharacterColor>();
            var colorCount = CheckedCount(BitConverter.ToUInt64(d, p), 12, d, p + 8); p += 8;
            for (var c = 0; c < colorCount; c++, p += 12) colors[BitConverter.ToUInt32(d, p)] = new CharacterColor(d[p + 4], d[p + 5], d[p + 6]);
            subs.Add(new SubMaterial(subName, floats, colors));
            // Groups are separated by a 5 marker (absent after the very last sub-material in the file)
            if (p + 4 <= d.Length && BitConverter.ToUInt32(d, p) == 5) p += 4;
        }
        return (name, id, subs);
    }

    /// <summary>
    /// A count read from the file, as an int, if that many items of <paramref name="itemSize"/> bytes
    /// fit in what's left after <paramref name="p"/>. Stops wrap-around and endless loops on bad data.
    /// </summary>
    private static int CheckedCount(ulong count, int itemSize, byte[] d, int p)
    {
        var remaining = (ulong)Math.Max(0, d.Length - p);
        if (count > remaining / (ulong)itemSize) throw new InvalidDataException("Unexpected character file layout.");
        return (int)count;
    }

    /// <summary>
    /// Fixed ids the game uses for material and colour names. Unlike parameter and style names,
    /// these aren't CRC-32C of the readable name (mapping from StarBreaker's name_hash.rs, MIT).
    /// </summary>
    internal static class Ids
    {
        public const uint HeadMaterial = 0xA98BEB34;
        public const uint HairDyeMaterial = 0x6C836947;
        public const uint EyebrowDyeMaterial = 0x078AC8BD;
        public const uint EyeMaterial = 0xA047885E;
        public const uint BeardDyeMaterial = 0x9B274D93;
        public const uint BodyColor = 0xBD530797;
        public const uint EyeColor = 0x442A34AC;
        public const uint HairDyeColor1 = 0x15E90814;
        public const uint HairDyeColor2 = 0xA2C7C909;
    }

    /// <summary>The game's name hash for parameter and style names: CRC-32C of the exact name.</summary>
    public static uint Hash(string name) => Crc32C(Encoding.UTF8.GetBytes(name));

    /// <summary>CRC-32C (Castagnoli), as used by the .chf container and name hashes.</summary>
    internal static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = BitOperations.Crc32C(crc, b);
        return ~crc;
    }
}

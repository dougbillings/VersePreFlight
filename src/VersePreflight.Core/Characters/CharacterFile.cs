using System.Numerics;
using ZstdSharp;

namespace VersePreflight.Core.Characters;

/// <summary>One face part's recipe: up to four preset heads and how much of each.</summary>
public readonly record struct DnaBlend(int HeadId, float Weight);

/// <summary>
/// The parts of a custom character (<c>.chf</c>) that Verse Preflight reads: the body type
/// (as the hash the game uses to pick the head data) and the face DNA.
/// Format knowledge from StarBreaker's starbreaker-chf (MIT, diogotr7). Files are only read, never written.
/// </summary>
public sealed record CharacterFile(uint BodyHash, IReadOnlyList<DnaBlend[]> FaceParts)
{
    public const int FileSize = 4096;
    private const ushort Magic = 0x4242; // "BB"

    public static CharacterFile Read(string path) => Parse(File.ReadAllBytes(path));

    public static CharacterFile Parse(byte[] file)
    {
        if (file.Length != FileSize || BitConverter.ToUInt16(file, 0) != Magic)
            throw new InvalidDataException("Not a Star Citizen custom character file.");

        // Header: magic(2) unknown(2) crc32c(4) compressed size(4) uncompressed size(4); crc covers bytes 16..4096
        var expectedCrc = BitConverter.ToUInt32(file, 4);
        if (Crc32C(file.AsSpan(16)) != expectedCrc) throw new InvalidDataException("Character file is damaged (checksum mismatch).");
        var compressed = BitConverter.ToInt32(file, 8);
        var payload = new Decompressor().Unwrap(file.AsSpan(16, compressed)).ToArray();
        return ParsePayload(payload);
    }

    internal static CharacterFile ParsePayload(byte[] d)
    {
        // version(4) version(4) body type guid(16) voice guid(16), then u64 DNA size and the DNA block
        var p = 4 + 4 + 16 + 16;
        var dnaSize = (int)BitConverter.ToUInt64(d, p);
        p += 8;
        var dna = d.AsSpan(p, dnaSize);

        // DNA: "dna matrix 1.0" hash(4), body hash(4), variant hash(4), zero(4),
        //      part count(2), blends per part(2), unknown(2), max head id(2), then blends
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
        return new CharacterFile(bodyHash, parts.Select(l => l.ToArray()).ToList());
    }

    /// <summary>CRC-32C (Castagnoli), as used by the .chf container.</summary>
    internal static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = BitOperations.Crc32C(crc, b);
        return ~crc;
    }
}

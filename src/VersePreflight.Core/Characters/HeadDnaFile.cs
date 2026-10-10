using System.Text;

namespace VersePreflight.Core.Characters;

/// <summary>
/// The parts of CIG's head "DNA V1.6" file that are needed to rebuild a face: the preset head slots
/// a .chf <c>head_id</c> refers to, and the per-vertex face region mask.
/// The format was worked out for this project (see experiments/character-3d/FINDINGS.md).
/// </summary>
public sealed class HeadDnaFile
{
    private const string Magic = "DNA V1.6";
    private const int SlotSize = 40;
    // Indices into the header's table of u64 section offsets (from 0x40)
    private const int SectionHeadSlots = 2;
    private const int SectionRegionMask = 22;

    private HeadDnaFile(uint bodyHash, int regionCount, int vertexCount, string[] slots, float[] mask)
    {
        BodyHash = bodyHash; RegionCount = regionCount; VertexCount = vertexCount; HeadSlots = slots; Mask = mask;
    }

    /// <summary>Matches <see cref="CharacterFile.BodyHash"/> of characters that use this head.</summary>
    public uint BodyHash { get; }
    public int RegionCount { get; }
    public int VertexCount { get; }

    /// <summary>Head names by head_id. Slot 0 is the base head itself.</summary>
    public IReadOnlyList<string> HeadSlots { get; }

    /// <summary>Region weights, region-major: <c>Mask[region * VertexCount + vertex]</c>.</summary>
    public float[] Mask { get; }

    public float Weight(int region, int vertex) => Mask[region * VertexCount + vertex];

    public static HeadDnaFile Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x100 || Encoding.ASCII.GetString(d[..8]) != Magic) throw new InvalidDataException("Not a supported head DNA file.");

        var bodyHash = BitConverter.ToUInt32(d[0x18..]);
        int regions = d[0x28];
        var slotCount = BitConverter.ToUInt16(d[0x2A..]);
        var vertices = BitConverter.ToUInt16(d[0x30..]);

        var slotsAt = (int)BitConverter.ToUInt64(d[(0x40 + SectionHeadSlots * 8)..]);
        var slots = new string[slotCount];
        for (var i = 0; i < slotCount; i++)
            slots[i] = Encoding.ASCII.GetString(d.Slice(slotsAt + i * SlotSize, SlotSize)).Split('\0')[0];

        var maskAt = (int)BitConverter.ToUInt64(d[(0x40 + SectionRegionMask * 8)..]);
        var mask = new float[regions * vertices];
        for (var i = 0; i < mask.Length; i++) mask[i] = BitConverter.ToSingle(d[(maskAt + i * 4)..]);

        // Every vertex's region weights must sum to 1, or the layout isn't what we expect
        for (var v = 0; v < vertices; v++)
        {
            float sum = 0;
            for (var r = 0; r < regions; r++) sum += mask[r * vertices + v];
            if (Math.Abs(sum - 1) > 0.01f) throw new InvalidDataException("Head DNA file has an unexpected layout (region mask check failed).");
        }

        return new HeadDnaFile(bodyHash, regions, vertices, slots, mask);
    }
}

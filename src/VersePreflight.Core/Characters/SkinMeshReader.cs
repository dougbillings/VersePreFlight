using System.Numerics;

namespace VersePreflight.Core.Characters;

/// <summary>Positions and triangle indices of a mesh.</summary>
public sealed record MeshGeometry(Vector3[] Positions, int[] Indices, Vector2[] Uvs)
{
    /// <summary>Index ranges drawn with one material each (material id = index into the .mtl sub-materials).</summary>
    public IReadOnlyList<Submesh> Submeshes { get; init; } = [];
}

/// <summary>A range of triangles using one material.</summary>
public readonly record struct Submesh(int MaterialId, int FirstIndex, int IndexCount);

/// <summary>
/// Reads the geometry of a Star Citizen <c>.skinm</c> file (CryEngine "#ivo" chunk file with an
/// IVO_SKIN2 chunk). Ported from StarBreaker's starbreaker-chunks and starbreaker-3d (MIT, diogotr7).
/// Only positions and indices are read; everything else is skipped.
/// </summary>
public static class SkinMeshReader
{
    private const uint IvoMagic = 0x6F766923; // "#ivo"
    private const uint IvoVersion = 0x900;
    private const uint IvoSkin2 = 0xB8757777;
    private const uint StreamVertsUvs = 0x91329AE9;
    private const uint StreamVertsUvs2 = 0xB3A70D5E;
    private const uint StreamIndices = 0xEECDC168;

    public static MeshGeometry Read(byte[] file)
    {
        if (file.Length < 16 || BitConverter.ToUInt32(file, 0) != IvoMagic) throw new InvalidDataException("Not an #ivo mesh file.");
        if (BitConverter.ToUInt32(file, 4) != IvoVersion) throw new InvalidDataException("Unsupported #ivo version.");

        var chunkCount = BitConverter.ToInt32(file, 8);
        var entries = Enumerable.Range(0, chunkCount)
            .Select(i => (Type: BitConverter.ToUInt32(file, 16 + i * 16), Offset: (long)BitConverter.ToUInt64(file, 16 + i * 16 + 8)))
            .ToList();
        var skin = entries.FirstOrDefault(e => e.Type == IvoSkin2);
        if (skin.Type != IvoSkin2) throw new InvalidDataException("Mesh file has no skin chunk.");
        // Chunk size is the gap to the next chunk (by offset) or the end of the file
        var end = entries.Select(e => e.Offset).Where(o => o > skin.Offset).DefaultIfEmpty(file.Length).Min();
        return ReadSkin(file.AsSpan((int)skin.Offset, (int)(end - skin.Offset)));
    }

    private static MeshGeometry ReadSkin(ReadOnlySpan<byte> d)
    {
        // Header: flags u32, MeshInfo (76 bytes), 88 bytes padding, then submesh descriptors (48 bytes each)
        var numVertices = BitConverter.ToInt32(d[8..]);
        var numIndices = BitConverter.ToInt32(d[12..]);
        var numSubmeshes = BitConverter.ToInt32(d[16..]);
        var minBound = ReadVec3(d[48..]);   // MeshInfo.min_bound (the scaling box for quantized positions)
        var maxBound = ReadVec3(d[60..]);
        var extraCount = BitConverter.ToInt32(d[76..]);
        var submeshes = new Submesh[numSubmeshes];
        for (var s = 0; s < numSubmeshes; s++)
        {
            var sd = d[(4 + 76 + 88 + s * 48)..]; // mat id(2) node(2) first index(4) index count(4) ...
            submeshes[s] = new Submesh(BitConverter.ToUInt16(sd), BitConverter.ToInt32(sd[4..]), BitConverter.ToInt32(sd[8..]));
        }
        var p = 4 + 76 + 88 + numSubmeshes * 48 + extraCount * 4;

        Vector3[]? positions = null;
        Vector2[]? uvs = null;
        int[]? indices = null;
        while (p + 8 <= d.Length && (positions is null || indices is null))
        {
            var type = BitConverter.ToUInt32(d[p..]);
            p += 4;
            if (type == 0) continue; // alignment padding
            var elementSize = BitConverter.ToInt32(d[p..]);
            p += 4;
            var start = p;

            if (type is StreamVertsUvs or StreamVertsUvs2)
            {
                positions = new Vector3[numVertices];
                uvs = new Vector2[numVertices];
                for (var i = 0; i < numVertices; i++)
                {
                    var v = d[(p + i * elementSize)..];
                    // The UV is the last 4 bytes of each vertex (two half floats) in both the 16- and 20-byte layouts
                    uvs[i] = new Vector2((float)BitConverter.ToHalf(v[(elementSize - 4)..]), (float)BitConverter.ToHalf(v[(elementSize - 2)..]));
                    positions[i] = elementSize switch
                    {
                        20 => ReadVec3(v),
                        16 => Dequantize(BitConverter.ToInt16(v), BitConverter.ToInt16(v[2..]), BitConverter.ToInt16(v[4..]), minBound, maxBound),
                        _ => throw new InvalidDataException($"Unexpected vertex size {elementSize}."),
                    };
                }
                p += numVertices * elementSize;
            }
            else if (type == StreamIndices)
            {
                indices = new int[numIndices];
                for (var i = 0; i < numIndices; i++)
                    indices[i] = elementSize == 2 ? BitConverter.ToUInt16(d[(p + i * 2)..]) : BitConverter.ToInt32(d[(p + i * 4)..]);
                p += numIndices * elementSize;
            }
            else
            {
                p += elementSize * numVertices; // other per-vertex streams
            }

            var remainder = (p - start) % 8; // streams are 8-byte aligned
            if (remainder != 0) p += 8 - remainder;
        }

        if (positions is null || indices is null) throw new InvalidDataException("Mesh is missing positions or indices.");
        return new MeshGeometry(positions, indices, uvs ?? new Vector2[positions.Length]) { Submeshes = submeshes };
    }

    private static Vector3 ReadVec3(ReadOnlySpan<byte> s) =>
        new(BitConverter.ToSingle(s), BitConverter.ToSingle(s[4..]), BitConverter.ToSingle(s[8..]));

    // SNorm16 scaled to the bounding box; half extents are clamped to at least 1 (as CryEngine-Converter does)
    internal static Vector3 Dequantize(short x, short y, short z, Vector3 min, Vector3 max)
    {
        static float Axis(short raw, float lo, float hi) =>
            raw / 32767f * Math.Max((hi - lo) / 2f, 1f) + (hi + lo) / 2f;
        return new Vector3(Axis(x, min.X, max.X), Axis(y, min.Y, max.Y), Axis(z, min.Z, max.Z));
    }
}

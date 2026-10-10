using System.Numerics;
using System.Text;
using VersePreflight.Core.Characters;
using ZstdSharp;

namespace VersePreflight.Core.Tests.Characters;

/// <summary>Builds small synthetic game files in memory; no real game data is used.</summary>
public class CharacterReaderTests
{
    // ---------- .chf ----------

    /// <summary>Appends a loadout and materials like the game writes them (see StarBreaker's format notes).</summary>
    private static void WriteAppearance(BinaryWriter w)
    {
        static uint H(string s) => CharacterFile.Hash(s);
        // Loadout: total count, then body -> head -> hair
        w.Write(3UL);
        w.Write(H("body_itemport")); w.Write(Guid.Empty.ToByteArray()); w.Write(1u); w.Write(0u);
        w.Write(H("head_itemport")); w.Write(Guid.Empty.ToByteArray()); w.Write(1u); w.Write(0u);
        w.Write(H("hair_itemport")); w.Write(new Guid("ce9137d2-9c06-412a-ab90-5da4e988902b").ToByteArray()); w.Write(0u); w.Write(0u);
        w.Write(5u);

        void Material(uint name, string sub, (string, float)[] floats, (uint, byte, byte, byte)[] colors, bool last)
        {
            w.Write(name); w.Write(new byte[16]); w.Write(0u); w.Write(new byte[16]); w.Write(1u); w.Write(5u);
            w.Write(H(sub)); w.Write(0u);
            w.Write((ulong)floats.Length); foreach (var (n, v) in floats) { w.Write(H(n)); w.Write(v); w.Write(0u); }
            w.Write((ulong)colors.Length); foreach (var (n, r, g, b) in colors) { w.Write(n); w.Write(r); w.Write(g); w.Write(b); w.Write((byte)255); w.Write(0u); }
            if (!last) w.Write(5u);
        }
        Material(0xA98BEB34, "shader_head", [("FrecklesAmount", 0.5f)], [(0xBD530797, 0x3a, 0x2b, 0x26)], false);
        Material(0x6C836947, "hair_21_m", [("DyeAmount", 1f), ("BaseMelanin", 0f)], [(0x15E90814, 0x44, 0x62, 0xd0)], false);
        Material(0xA047885E, "shader_eyeInner", [], [(0x442A34AC, 0x2d, 0x32, 0x11)], true);
    }

    [Fact]
    public void CharacterFile_ReadsColoursHairAndLoadout()
    {
        var chf = CharacterFile.Parse(BuildChf(1, [[(0, 65535)]], appearance: true));

        Assert.Equal(new CharacterColor(0x3a, 0x2b, 0x26), chf.SkinColor);
        Assert.Equal(new CharacterColor(0x2d, 0x32, 0x11), chf.EyeColor);
        Assert.NotNull(chf.Hair);
        Assert.Equal(CharacterFile.Hash("hair_21_m"), chf.Hair.StyleHash);
        Assert.Equal(new CharacterColor(0x44, 0x62, 0xd0), chf.Hair.DyeColor);
        Assert.Equal(1f, chf.Hair.DyeAmount);
        Assert.Equal(0f, chf.Hair.Melanin); // 0 = use the style's default
        Assert.Equal(new Guid("ce9137d2-9c06-412a-ab90-5da4e988902b"), chf.Loadout[CharacterFile.Hash("hair_itemport")]);
        Assert.Null(chf.Beard);
    }

    [Fact]
    public void CharacterFile_StillReadsFaceWhenAppearanceIsMissing()
    {
        var chf = CharacterFile.Parse(BuildChf(1, [[(3, 65535)]]));
        Assert.Null(chf.SkinColor);
        Assert.Equal(3, chf.FaceParts[0][0].HeadId);
    }

    [Fact]
    public void Hash_IsCrc32COfName() =>
        Assert.Equal(CharacterFile.Crc32C(Encoding.UTF8.GetBytes("EyeMaterial")), CharacterFile.Hash("EyeMaterial"));

    private static byte[] BuildChf(uint bodyHash, (int Head, ushort Value)[][] parts, bool corruptCrc = false, bool appearance = false)
    {
        var dna = new MemoryStream(); var w = new BinaryWriter(dna);
        w.Write(0xFCD09394u); w.Write(bodyHash); w.Write(0x12345678u); w.Write(0u);
        w.Write((ushort)parts.Length); w.Write((ushort)4); w.Write((ushort)4); w.Write((ushort)40);
        for (var blend = 0; blend < 4; blend++)                 // interleaved: all parts' blend 0, then blend 1...
            foreach (var part in parts)
            {
                var (head, value) = blend < part.Length ? part[blend] : (0, (ushort)0);
                w.Write(value); w.Write((ushort)head);
            }

        var payload = new MemoryStream(); var pw = new BinaryWriter(payload);
        pw.Write(2u); pw.Write(8u); pw.Write(new byte[16]); pw.Write(new byte[16]);
        pw.Write((ulong)dna.Length); pw.Write(dna.ToArray());
        if (appearance) WriteAppearance(pw);
        else pw.Write(new byte[32]); // no loadout/materials: only the face is read

        var compressed = new Compressor().Wrap(payload.ToArray()).ToArray();
        var file = new byte[CharacterFile.FileSize];
        BitConverter.GetBytes((ushort)0x4242).CopyTo(file, 0);
        BitConverter.GetBytes(compressed.Length).CopyTo(file, 8);
        BitConverter.GetBytes((int)payload.Length).CopyTo(file, 12);
        compressed.CopyTo(file, 16);
        var crc = CharacterFile.Crc32C(file.AsSpan(16));
        BitConverter.GetBytes(corruptCrc ? crc ^ 1 : crc).CopyTo(file, 4);
        return file;
    }

    [Fact]
    public void CharacterFile_ReadsBodyAndInterleavedDna()
    {
        var file = BuildChf(0xDD6C67F6, [[(2, 32768), (11, 32767)], [(20, 65535)]]);

        var chf = CharacterFile.Parse(file);

        Assert.Equal(0xDD6C67F6u, chf.BodyHash);
        Assert.Equal(2, chf.FaceParts.Count);
        Assert.Equal([2, 11], chf.FaceParts[0].Select(b => b.HeadId));
        Assert.Equal(0.5f, chf.FaceParts[0][0].Weight, 0.001f);
        Assert.Equal(20, Assert.Single(chf.FaceParts[1]).HeadId);
    }

    [Fact]
    public void CharacterFile_RejectsDamagedFiles()
    {
        Assert.Throws<InvalidDataException>(() => CharacterFile.Parse(BuildChf(1, [[(0, 1)]], corruptCrc: true)));
        Assert.Throws<InvalidDataException>(() => CharacterFile.Parse(new byte[100]));
    }

    [Fact]
    public void Crc32C_MatchesKnownValue() =>
        Assert.Equal(0xE3069283u, CharacterFile.Crc32C(Encoding.ASCII.GetBytes("123456789"))); // standard check value

    // ---------- DNA head file ----------

    internal static byte[] BuildDna(uint bodyHash, string[] slots, float[,] mask)
    {
        int regions = mask.GetLength(0), verts = mask.GetLength(1);
        const int slotsAt = 0x200, maskAt = 0x800;
        var d = new byte[maskAt + regions * verts * 4];
        Encoding.ASCII.GetBytes("DNA V1.6").CopyTo(d, 0);
        BitConverter.GetBytes(bodyHash).CopyTo(d, 0x18);
        d[0x28] = (byte)regions; d[0x29] = (byte)regions;
        BitConverter.GetBytes((ushort)slots.Length).CopyTo(d, 0x2A);
        BitConverter.GetBytes((ushort)verts).CopyTo(d, 0x30);
        BitConverter.GetBytes((ulong)slotsAt).CopyTo(d, 0x40 + 2 * 8);
        BitConverter.GetBytes((ulong)maskAt).CopyTo(d, 0x40 + 22 * 8);
        for (var i = 0; i < slots.Length; i++) Encoding.ASCII.GetBytes(slots[i]).CopyTo(d, slotsAt + i * 40);
        for (var r = 0; r < regions; r++)
            for (var v = 0; v < verts; v++) BitConverter.GetBytes(mask[r, v]).CopyTo(d, maskAt + (r * verts + v) * 4);
        return d;
    }

    [Fact]
    public void HeadDnaFile_ReadsSlotsAndMask()
    {
        var dna = HeadDnaFile.Parse(BuildDna(7, ["base", "a", "b"], new float[,] { { 1, 0.25f }, { 0, 0.75f } }));

        Assert.Equal(7u, dna.BodyHash);
        Assert.Equal(["base", "a", "b"], dna.HeadSlots);
        Assert.Equal(2, dna.RegionCount);
        Assert.Equal(0.75f, dna.Weight(1, 1));
    }

    [Fact]
    public void HeadDnaFile_RejectsMaskThatDoesNotSumToOne() =>
        Assert.Throws<InvalidDataException>(() => HeadDnaFile.Parse(BuildDna(7, ["base"], new float[,] { { 0.5f, 1 }, { 0, 0 } })));

    // ---------- .skinm ----------

    internal static byte[] BuildSkin(Vector3[] positions, int[] indices, Vector2[]? uvs = null, (int Material, int First, int Count)[]? submeshes = null)
    {
        submeshes ??= [];
        var skin = new MemoryStream(); var w = new BinaryWriter(skin);
        w.Write(0u);                                          // flags
        w.Write(0u); w.Write(positions.Length); w.Write(indices.Length); w.Write(submeshes.Length); w.Write(0); // MeshInfo start
        for (var i = 0; i < 12; i++) w.Write(0f);             // model and scaling bounds
        w.Write(0u); w.Write(0);                              // vertex format, extra count
        w.Write(new byte[88]);
        foreach (var (material, first, count) in submeshes)  // 48-byte submesh descriptors
        {
            w.Write((ushort)material); w.Write((ushort)0); w.Write(first); w.Write(count);
            w.Write(new byte[36]);
        }
        w.Write(0x91329AE9u); w.Write(20);                     // float vertices: xyz + colour + uv (two halves)
        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i]; var uv = uvs?[i] ?? Vector2.Zero;
            w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(0u); w.Write((Half)uv.X); w.Write((Half)uv.Y);
        }
        while (skin.Length % 8 != 0) w.Write((byte)0);
        w.Write(0xEECDC168u); w.Write(2);                      // u16 indices
        foreach (var i in indices) w.Write((ushort)i);
        while (skin.Length % 8 != 0) w.Write((byte)0);

        var file = new MemoryStream(); var f = new BinaryWriter(file);
        f.Write(0x6F766923u); f.Write(0x900u); f.Write(1); f.Write(0);
        f.Write(0xB8757777u); f.Write(0u); f.Write((ulong)32);  // one chunk table entry; chunk data at offset 32
        f.Write(skin.ToArray());
        return file.ToArray();
    }

    [Fact]
    public void SkinMeshReader_ReadsPositionsAndIndices()
    {
        Vector3[] pos = [new(1, 2, 3), new(4, 5, 6), new(7, 8, 9)];
        var mesh = SkinMeshReader.Read(BuildSkin(pos, [0, 1, 2]));

        Assert.Equal(pos, mesh.Positions);
        Assert.Equal([0, 1, 2], mesh.Indices);
    }

    [Fact]
    public void SkinMeshReader_DequantizesWithMinimumUnitExtent()
    {
        // A small box: half extent is clamped to 1, so the result is the raw SNorm plus the centre
        var p = SkinMeshReader.Dequantize(32767, -32767, 0, new Vector3(-0.1f, -0.1f, 1), new Vector3(0.1f, 0.1f, 2));
        Assert.Equal(new Vector3(1, -1, 1.5f), p);
    }

    [Fact]
    public void SkinMeshReader_RejectsOtherFiles() =>
        Assert.Throws<InvalidDataException>(() => SkinMeshReader.Read(new byte[64]));

    // ---------- Blending ----------

    [Fact]
    public void HeadLibrary_BlendsRegionsAndRoundTripsThroughCache()
    {
        // Two vertices, two regions: vertex 0 fully region 0, vertex 1 split 50/50
        Vector3[] baseHead = [Vector3.Zero, Vector3.Zero];
        Vector3[] headA = [new(1, 0, 0), new(1, 0, 0)];
        Vector3[] headB = [new(0, 2, 0), new(0, 2, 0)];
        var lib = new HeadLibrary("test", 7, new BlendSet([0, 1, 1], [baseHead, headA, headB], [1, 0.5f, 0, 0.5f], 2), null);
        var chf = new CharacterFile(7, [[new DnaBlend(1, 1)], [new DnaBlend(2, 0.5f), new DnaBlend(1, 0.5f)]]);

        var result = lib.Blend(chf);

        Assert.Equal(new Vector3(1, 0, 0), result[0]);
        Assert.Equal(new Vector3(0.75f, 0.5f, 0), result[1]);  // 0.5 x A + 0.5 x (half A + half B)

        using var ms = new MemoryStream();
        lib.Save(ms); ms.Position = 0;
        var loaded = HeadLibrary.Load(ms);
        Assert.Equal(result, loaded.Blend(chf));
    }

    [Fact]
    public void HeadLibrary_UsesBaseForMissingHeadsAndRegions()
    {
        Vector3[] baseHead = [new(5, 5, 5)];
        var lib = new HeadLibrary("test", 7, new BlendSet([0, 0, 0], [baseHead, null], [0.5f, 0.5f], 2), null);
        var chf = new CharacterFile(7, [[new DnaBlend(1, 1)]]); // head 1 has no mesh; region 1 has no recipe

        Assert.Equal(new Vector3(5, 5, 5), lib.Blend(chf)[0]);
    }

    [Fact]
    public void Align_RemovesMeanOffset()
    {
        var aligned = HeadLibrary.Align([new(1, 1, 1), new(3, 1, 1)], [new(0, 0, 0), new(2, 0, 0)]);
        Assert.Equal([new Vector3(0, 0, 0), new Vector3(2, 0, 0)], aligned);
    }
}

using System.Text;
using VersePreflight.Core.Characters;

namespace VersePreflight.Core.Tests.Characters;

/// <summary>Tests for the game-format readers used by hair rendering, with synthetic files.</summary>
public class FormatReaderTests
{
    // ---------- CryXmlB ----------

    private static byte[] BuildCryXml()
    {
        // <Material><SubMaterials><Material Name="hair_m"><Texture File="a_opac.tif"/></Material></SubMaterials></Material>
        var strings = new MemoryStream();
        var offsets = new Dictionary<string, int>();
        int S(string s)
        {
            if (offsets.TryGetValue(s, out var o)) return o;
            o = (int)strings.Length; offsets[s] = o;
            strings.Write(Encoding.UTF8.GetBytes(s)); strings.WriteByte(0);
            return o;
        }
        (int Tag, int Attrs, int FirstAttr, int Children, int FirstChild)[] nodes =
        [
            (S("Material"), 0, 0, 1, 0),
            (S("SubMaterials"), 0, 0, 1, 1),
            (S("Material"), 1, 0, 1, 2),
            (S("Texture"), 1, 1, 0, 0),
        ];
        int[] children = [1, 2, 3];
        (int, int)[] attrs = [(S("Name"), S("hair_m")), (S("File"), S("a_opac.tif"))];

        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("CryXmlB\0"));
        w.Write(0); w.Write(0); w.Write(nodes.Length); w.Write(0); w.Write(attrs.Length); w.Write(0); w.Write(children.Length); w.Write(0); w.Write((int)strings.Length);
        foreach (var n in nodes) { w.Write(n.Tag); w.Write(0); w.Write((ushort)n.Attrs); w.Write((ushort)n.Children); w.Write(-1); w.Write(n.FirstAttr); w.Write(n.FirstChild); w.Write(0); }
        foreach (var c in children) w.Write(c);
        foreach (var (k, v) in attrs) { w.Write(k); w.Write(v); }
        w.Write(strings.ToArray());
        return ms.ToArray();
    }

    [Fact]
    public void CryXml_ParsesTreeAndAttributes()
    {
        var root = CryXml.Parse(BuildCryXml());

        Assert.Equal("Material", root.Tag);
        var sub = Assert.Single(Assert.Single(root.Children).Children);
        Assert.Equal("hair_m", sub["Name"]);
        Assert.Equal("a_opac.tif", root.Descendants().Single(e => e.Tag == "Texture")["File"]);
    }

    [Fact]
    public void CryXml_RejectsTextXml() =>
        Assert.Throws<InvalidDataException>(() => CryXml.Parse(Encoding.UTF8.GetBytes("<Material />")));

    // ---------- BC4 / split DDS ----------

    [Fact]
    public void TextureReader_DecodesBc4Block()
    {
        // Endpoints 255 and 0 (8-value mode); texel 0 uses index 0 (255), texel 1 index 1 (0),
        // texel 2 index 2 (6/7 of the way: 219); the 3-bit indices are packed little-endian
        ulong bits = 0;
        int[] indices = [0, 1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        for (var i = 0; i < 16; i++) bits |= (ulong)indices[i] << (3 * i);
        var block = new byte[8]; block[0] = 255; block[1] = 0;
        for (var i = 0; i < 6; i++) block[2 + i] = (byte)(bits >> (8 * i));

        var texels = TextureReader.DecodeFirstChannel(block, 4, 4, 80, 8);

        Assert.Equal(255, texels[0]);
        Assert.Equal(0, texels[1]);
        Assert.Equal(218, texels[2]); // (6 * 255 + 1 * 0) / 7, rounded down
    }

    [Fact]
    public void TextureReader_ReadsTheRightSplitMip()
    {
        // 8x8 BC4 texture with 4 mips: part .dds.1 holds mip 0 (8x8), the header file holds mips 1-3
        var header = new byte[148 + 8 * 3];
        BitConverter.GetBytes(0x20534444u).CopyTo(header, 0);           // "DDS "
        BitConverter.GetBytes(8).CopyTo(header, 12); BitConverter.GetBytes(8).CopyTo(header, 16);
        BitConverter.GetBytes(4).CopyTo(header, 28);
        BitConverter.GetBytes(0x30315844u).CopyTo(header, 84);          // "DX10"
        BitConverter.GetBytes(80u).CopyTo(header, 128);                 // BC4
        var mip1 = header.AsSpan(148, 8); mip1[0] = 200; mip1[1] = 200; // solid 200
        var mip0 = new byte[4 * 8];
        for (var b = 0; b < 4; b++) { mip0[b * 8] = 100; mip0[b * 8 + 1] = 100; } // solid 100
        var files = new Dictionary<string, byte[]> { ["t.dds"] = header, ["t.dds.1"] = mip0 };

        var full = TextureReader.ReadChannel(f => files.GetValueOrDefault(f), "t.dds", maxSize: 8);
        var smaller = TextureReader.ReadChannel(f => files.GetValueOrDefault(f), "t.dds", maxSize: 4);

        Assert.Equal((8, 8, 100), (full.Width, full.Height, (int)full.Texels[0]));
        Assert.Equal((4, 4, 200), (smaller.Width, smaller.Height, (int)smaller.Texels[0]));
    }

    [Fact]
    public void GrayTexture_SamplesWithWrapping()
    {
        var tex = new GrayTexture(2, 1, [0, 255]);
        Assert.Equal(0.5f, tex.Sample(0.5f, 0.5f), 0.01f);  // halfway between the two texels
        Assert.Equal(tex.Sample(0.25f, 0), tex.Sample(1.25f, 0), 0.001f);
    }

    // ---------- DataCore records ----------

    [Fact]
    public void GameDatabase_ReadsRecordNamesForMatchingFiles()
    {
        var hairId = Guid.NewGuid();
        var shipId = Guid.NewGuid();
        var table1 = Encoding.UTF8.GetBytes("libs/records/head/shared/hair/hair_24.xml\0libs/records/ships/x.xml\0");
        var table2 = Encoding.UTF8.GetBytes("EntityClassDefinition.hair_24\0EntityClassDefinition.ship\0");

        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0u); w.Write(8); w.Write(0); w.Write(0);
        for (var i = 0; i < 24; i++) w.Write(i == 4 ? 2 : 0); // only the record count is non-zero
        w.Write(table1.Length); w.Write(table2.Length);
        void Record(int name, int file, Guid id) { w.Write(name); w.Write(file); w.Write(-1); w.Write(0); w.Write(id.ToByteArray()); w.Write((ushort)0); w.Write((ushort)0); }
        Record(0, 0, hairId);
        Record(30, 42, shipId);
        w.Write(table1); w.Write(table2);

        var names = GameDatabase.ReadRecordNames(ms.ToArray(), f => f.Contains("/head/shared/"));

        Assert.Equal("hair_24", Assert.Single(names, kv => kv.Key == hairId).Value);
        Assert.DoesNotContain(shipId, names.Keys);
    }

    [Fact]
    public void GameDatabase_FindsMaterialPathsByIdBeforeTheReference()
    {
        var skinId = Guid.NewGuid();
        var table1 = Encoding.UTF8.GetBytes("objects/x/body.mtl\0objects/heads/male13_t1_head_material.mtl\0");
        var table2 = Encoding.UTF8.GetBytes("unused\0");

        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0u); w.Write(8); w.Write(0); w.Write(0);
        for (var i = 0; i < 24; i++) w.Write(0);
        w.Write(table1.Length); w.Write(table2.Length);
        w.Write(table1); w.Write(table2);
        // Instance data: an unrelated reference, then the skin material's id followed by its path offset
        w.Write(Guid.NewGuid().ToByteArray()); w.Write(0);
        w.Write(skinId.ToByteArray()); w.Write(19);

        var paths = GameDatabase.FindMaterialPaths(ms.ToArray(), "_head_material.mtl");

        Assert.Equal("objects/heads/male13_t1_head_material.mtl", Assert.Single(paths).Value);
        Assert.Equal(skinId, paths.Keys.Single());
    }

    // ---------- Colour textures ----------

    [Fact]
    public void TextureReader_ReadsLegacyDxt1Colour()
    {
        // 4x4 DXT1, one block: colour 0 pure red, colour 1 pure blue (4-colour mode as c0 > c1);
        // texel 0 uses index 0, texel 1 index 1, texel 2 index 2 (2/3 red + 1/3 blue)
        var dds = new byte[128 + 8];
        BitConverter.GetBytes(0x20534444u).CopyTo(dds, 0);
        BitConverter.GetBytes(4).CopyTo(dds, 12); BitConverter.GetBytes(4).CopyTo(dds, 16);
        BitConverter.GetBytes(1).CopyTo(dds, 28);
        Encoding.ASCII.GetBytes("DXT1").CopyTo(dds, 84);
        BitConverter.GetBytes((ushort)0xF800).CopyTo(dds, 128); // red
        BitConverter.GetBytes((ushort)0x001F).CopyTo(dds, 130); // blue
        BitConverter.GetBytes(0u | 1u << 2 | 2u << 4).CopyTo(dds, 132);

        var tex = TextureReader.ReadColor(f => f == "t.dds" ? dds : null, "t.dds");

        Assert.Equal((4, 4), (tex.Width, tex.Height));
        Assert.Equal(new byte[] { 255, 0, 0 }, tex.Rgb[..3]);
        Assert.Equal(new byte[] { 0, 0, 255 }, tex.Rgb[3..6]);
        Assert.Equal(new byte[] { 170, 0, 85 }, tex.Rgb[6..9]);
        Assert.Equal((14 * 255 + 170) / 16f / 255f, tex.Mean().X, 0.001f); // the other 13 texels use index 0 (red)
    }

    // ---------- .skinm UVs and submeshes ----------

    [Fact]
    public void SkinMeshReader_ReadsUvsAndSubmeshes()
    {
        var file = CharacterReaderTests.BuildSkin([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2],
            uvs: [new(0.25f, 0.5f), new(1, 0), new(0, 1)], submeshes: [(3, 0, 3)]);

        var mesh = SkinMeshReader.Read(file);

        Assert.Equal(new System.Numerics.Vector2(0.25f, 0.5f), mesh.Uvs[0]);
        Assert.Equal(new Submesh(3, 0, 3), Assert.Single(mesh.Submeshes));
    }
}

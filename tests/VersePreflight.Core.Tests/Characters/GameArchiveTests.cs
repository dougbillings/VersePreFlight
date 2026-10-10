using System.Text;
using VersePreflight.Core.Characters;
using ZstdSharp;

namespace VersePreflight.Core.Tests.Characters;

public class GameArchiveTests
{
    /// <summary>Writes a tiny ZIP64 archive laid out like Data.p4k (CIG extra fields, zstd).</summary>
    private static void WriteArchive(string path, params (string Name, byte[] Data, bool Encrypted)[] files)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var central = new MemoryStream(); var c = new BinaryWriter(central);
        foreach (var (name, data, encrypted) in files)
        {
            var offset = fs.Position;
            var compressed = new Compressor().Wrap(data).ToArray();
            var nameBytes = Encoding.UTF8.GetBytes(name);
            // Local header (CIG signature)
            w.Write(0x14034B50u); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)100);
            w.Write(0u); w.Write(0u); w.Write((uint)compressed.Length); w.Write((uint)data.Length);
            w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write(nameBytes); w.Write(compressed);

            // Extra: 0x0001 (sizes + offset), 0x5000, 0x5002 (encryption), 0x5003
            var extra = new MemoryStream(); var x = new BinaryWriter(extra);
            x.Write((ushort)0x0001); x.Write((ushort)24); x.Write((ulong)data.Length); x.Write((ulong)compressed.Length); x.Write((ulong)offset);
            x.Write((ushort)0x5000); x.Write((ushort)8); x.Write(0u);
            x.Write((ushort)0x5002); x.Write((ushort)6); x.Write((ushort)(encrypted ? 1 : 0));
            x.Write((ushort)0x5003); x.Write((ushort)4);
            c.Write(0x02014B50u); c.Write((ushort)45); c.Write((ushort)45); c.Write((ushort)0); c.Write((ushort)100);
            c.Write(0u); c.Write(0u); c.Write(0xFFFFFFFFu); c.Write(0xFFFFFFFFu);
            c.Write((ushort)nameBytes.Length); c.Write((ushort)extra.Length); c.Write((ushort)0); c.Write((ushort)0);
            c.Write((ushort)0); c.Write(0u); c.Write(0xFFFFFFFFu); c.Write(nameBytes); c.Write(extra.ToArray());
        }
        var cdOffset = fs.Position;
        w.Write(central.ToArray());
        var eocd64 = fs.Position;
        w.Write(0x06064B50u); w.Write(44UL); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
        w.Write((ulong)files.Length); w.Write((ulong)files.Length); w.Write((ulong)central.Length); w.Write((ulong)cdOffset);
        w.Write(0x07064B50u); w.Write(0u); w.Write((ulong)eocd64); w.Write(1u);
        w.Write(0x06054B50u); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)0);
    }

    [Fact]
    public void FindsAndReadsZstdEntries()
    {
        using var dir = new TempDir();
        var path = dir.Combine("Data.p4k");
        WriteArchive(path, ("Data/Objects/a.skinm", Encoding.ASCII.GetBytes("first file"), false),
                           ("Data/Other/b.txt", Encoding.ASCII.GetBytes("second"), false));

        using var archive = GameArchive.Open(path);
        var entries = archive.FindEntries(p => p.StartsWith(@"Data\Objects\", StringComparison.Ordinal));

        var entry = Assert.Single(entries);
        Assert.Equal(@"Data\Objects\a.skinm", entry.Path);
        Assert.False(entry.IsEncrypted);
        Assert.Equal("first file", Encoding.ASCII.GetString(archive.Read(entry)));
    }

    [Fact]
    public void RefusesEncryptedEntries()
    {
        using var dir = new TempDir();
        var path = dir.Combine("Data.p4k");
        WriteArchive(path, ("Data/secret.bin", [1, 2, 3], true));

        using var archive = GameArchive.Open(path);
        var entry = Assert.Single(archive.FindEntries(_ => true));

        Assert.True(entry.IsEncrypted);
        Assert.Throws<NotSupportedException>(() => archive.Read(entry));
    }

    [Fact]
    public void RejectsNonArchives()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("not.p4k", "hello");
        using var archive = GameArchive.Open(path);
        Assert.Throws<InvalidDataException>(() => archive.FindEntries(_ => true));
    }
}

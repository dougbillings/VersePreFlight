using VersePreflight.Core.IO;

namespace VersePreflight.Core.Tests.IO;

public class DirectoryLinksTests
{
    [Fact]
    public void Create_MakesLinkThatResolvesToTarget()
    {
        using var dir = new TempDir();
        dir.WriteFile("target/file.txt", "hello");
        var link = dir.Combine("link");

        DirectoryLinks.Create(link, dir.Combine("target"));

        Assert.True(DirectoryLinks.IsLink(link));
        Assert.True(PathUtil.IsSamePath(dir.Combine("target"), DirectoryLinks.GetTarget(link)!));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "file.txt")));
    }

    [Fact]
    public void Remove_DeletesOnlyTheLink()
    {
        using var dir = new TempDir();
        dir.WriteFile("target/file.txt", "keep me");
        var link = dir.Combine("link");
        DirectoryLinks.Create(link, dir.Combine("target"));

        DirectoryLinks.Remove(link);

        Assert.False(Path.Exists(link));
        Assert.Equal("keep me", File.ReadAllText(dir.Combine("target", "file.txt")));
    }

    [Fact]
    public void GetTarget_ReturnsNullForRealFolderAndMissingPath()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("real"));

        Assert.Null(DirectoryLinks.GetTarget(dir.Combine("real")));
        Assert.Null(DirectoryLinks.GetTarget(dir.Combine("missing")));
    }

    [Fact]
    public void Create_FailsIfPathExists()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("existing"));
        Directory.CreateDirectory(dir.Combine("target"));

        Assert.Throws<IOException>(() => DirectoryLinks.Create(dir.Combine("existing"), dir.Combine("target")));
    }

    [Fact]
    public void Remove_RefusesRealFolder()
    {
        using var dir = new TempDir();
        dir.WriteFile("real/file.txt", "x");

        Assert.Throws<IOException>(() => DirectoryLinks.Remove(dir.Combine("real")));
        Assert.True(File.Exists(dir.Combine("real", "file.txt")));
    }

    [Fact]
    public void BuildMountPointBuffer_HasExpectedLayout()
    {
        var buffer = DirectoryLinks.BuildMountPointBuffer(@"C:\T");

        Assert.Equal(0xA0000003u, BitConverter.ToUInt32(buffer, 0));
        var substituteLength = BitConverter.ToUInt16(buffer, 10);
        Assert.Equal(@"\??\C:\T".Length * 2, substituteLength);
        Assert.Equal(@"\??\C:\T", System.Text.Encoding.Unicode.GetString(buffer, 16, substituteLength));
        var printOffset = BitConverter.ToUInt16(buffer, 12);
        Assert.Equal(@"C:\T", System.Text.Encoding.Unicode.GetString(buffer, 16 + printOffset, BitConverter.ToUInt16(buffer, 14)));
        Assert.Equal(buffer.Length - 8, BitConverter.ToUInt16(buffer, 4));
    }
}

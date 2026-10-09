using VersePreflight.Core.IO;

namespace VersePreflight.Core.Tests.IO;

public class PathUtilTests
{
    [Fact]
    public void ResolveCaseInsensitive_MatchesExistingFoldersRegardlessOfCase()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("user", "client", "0", "Controls"));

        var resolved = PathUtil.ResolveCaseInsensitive(dir.Path, "USER/Client/0/controls/Mappings");

        // Existing segments take the on-disk casing; the missing one is appended as written
        Assert.Equal(dir.Combine("user", "client", "0", "Controls", "Mappings"), resolved);
    }

    [Fact]
    public void ResolveCaseInsensitive_AcceptsBothSeparators()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("a", "b"));

        Assert.Equal(dir.Combine("a", "b"), PathUtil.ResolveCaseInsensitive(dir.Path, @"a\b"));
        Assert.Equal(dir.Combine("a", "b"), PathUtil.ResolveCaseInsensitive(dir.Path, "a/b"));
    }

    [Fact]
    public void ResolveRelativeTo_HandlesRelativeAndRootedPaths()
    {
        using var dir = new TempDir();
        var configDir = dir.Combine("sync", "TOOLS");

        Assert.Equal(PathUtil.Normalize(dir.Combine("sync")), PathUtil.ResolveRelativeTo("..", configDir));
        Assert.Equal(PathUtil.Normalize(dir.Path), PathUtil.ResolveRelativeTo(dir.Path, configDir));
    }

    [Fact]
    public void IsSamePath_IgnoresTrailingSeparator()
    {
        using var dir = new TempDir();
        Assert.True(PathUtil.IsSamePath(dir.Path, dir.Path + Path.DirectorySeparatorChar));
    }
}

using VersePreflight.Core.IO;
using VersePreflight.Core.Links;
using VersePreflight.Core.Logging;

namespace VersePreflight.Core.Tests.Links;

public class FolderLinkerTests
{
    private static readonly LinkDefinition Mappings =
        new() { Name = "Control mappings", SourceSubPath = "user/client/0/Controls/Mappings", TargetName = "Mappings" };

    private readonly PreflightLog _log = new();
    private readonly FolderLinker _linker;

    public FolderLinkerTests() => _linker = new FolderLinker(_log, "TESTPC");

    [Fact]
    public void Plan_ClassifiesNewIdenticalAndConflictingFiles()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        dir.WriteFile("game/LIVE/user/client/0/Controls/Mappings/new.xml", "new");
        dir.WriteFile("game/LIVE/user/client/0/Controls/Mappings/same.xml", "same");
        dir.WriteFile("game/LIVE/user/client/0/Controls/Mappings/conflict.xml", "game default");
        dir.WriteFile("sync/Mappings/same.xml", "same");
        dir.WriteFile("sync/Mappings/conflict.xml", "my binds");

        var plan = _linker.Plan(env, Mappings, dir.Combine("sync"));

        Assert.Equal(LinkState.NotLinked, plan.State);
        Assert.Equal(["new.xml"], plan.FilesToMove);
        Assert.Equal(["same.xml"], plan.IdenticalFiles);
        Assert.Equal(["conflict.xml"], plan.Conflicts);
    }

    [Fact]
    public void Apply_NeverOverwritesSyncedFilesAndBacksUpConflicts()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        var source = dir.Combine("game", "LIVE", "user", "client", "0", "Controls", "Mappings");
        dir.WriteFile("game/LIVE/user/client/0/Controls/Mappings/new.xml", "new");
        dir.WriteFile("game/LIVE/user/client/0/Controls/Mappings/same.xml", "same");
        dir.WriteFile("game/LIVE/user/client/0/Controls/Mappings/sub/conflict.xml", "game default");
        dir.WriteFile("sync/Mappings/same.xml", "same");
        dir.WriteFile("sync/Mappings/sub/conflict.xml", "my binds");

        var result = _linker.Apply(env, Mappings, dir.Combine("sync"));

        Assert.True(result.Success, result.Message);
        Assert.True(PathUtil.IsSamePath(dir.Combine("sync", "Mappings"), DirectoryLinks.GetTarget(source)!));
        Assert.Equal("new", File.ReadAllText(dir.Combine("sync", "Mappings", "new.xml")));
        Assert.Equal("my binds", File.ReadAllText(dir.Combine("sync", "Mappings", "sub", "conflict.xml")));
        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(dir.Combine("sync", FolderLinker.BackupFolderName, "TESTPC"), result.BackupPath);
        Assert.Equal("game default", File.ReadAllText(Path.Combine(result.BackupPath, "sub", "conflict.xml")));
        Assert.False(File.Exists(Path.Combine(result.BackupPath, "same.xml")));
        Assert.Equal(0, _log.ErrorCount);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        _linker.Apply(env, Mappings, dir.Combine("sync"));

        var second = _linker.Apply(env, Mappings, dir.Combine("sync"));

        Assert.True(second.Success);
        Assert.Equal("Already linked.", second.Message);
        Assert.Equal(LinkState.Linked, _linker.Plan(env, Mappings, dir.Combine("sync")).State);
    }

    [Fact]
    public void Apply_CreatesLinkWhenGameFolderIsMissing()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();

        var result = _linker.Apply(env, Mappings, dir.Combine("sync"));

        Assert.True(result.Success, result.Message);
        Assert.True(DirectoryLinks.IsLink(dir.Combine("game", "LIVE", "user", "client", "0", "Controls", "Mappings")));
        Assert.True(Directory.Exists(dir.Combine("sync", "Mappings")));
    }

    [Fact]
    public void Apply_ReplacesLinkToWrongFolderAndLeavesOldFolderIntact()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        dir.WriteFile("old/Mappings/old.xml", "old");
        _linker.Apply(env, Mappings, dir.Combine("old"));

        Assert.Equal(LinkState.WrongTarget, _linker.Plan(env, Mappings, dir.Combine("sync")).State);
        var result = _linker.Apply(env, Mappings, dir.Combine("sync"));

        Assert.True(result.Success, result.Message);
        var source = dir.Combine("game", "LIVE", "user", "client", "0", "Controls", "Mappings");
        Assert.True(PathUtil.IsSamePath(dir.Combine("sync", "Mappings"), DirectoryLinks.GetTarget(source)!));
        Assert.Equal("old", File.ReadAllText(dir.Combine("old", "Mappings", "old.xml")));
    }

    [Fact]
    public void Apply_MatchesGameFolderCaseInsensitively()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        dir.WriteFile("game/LIVE/user/client/0/controls/mappings/a.xml", "a");
        var definition = new LinkDefinition { Name = "M", SourceSubPath = "USER/Client/0/Controls/Mappings", TargetName = "Mappings" };

        var result = _linker.Apply(env, definition, dir.Combine("sync"));

        Assert.True(result.Success, result.Message);
        Assert.True(DirectoryLinks.IsLink(dir.Combine("game", "LIVE", "user", "client", "0", "controls", "mappings")));
        Assert.Equal("a", File.ReadAllText(dir.Combine("sync", "Mappings", "a.xml")));
    }

    [Fact]
    public void Unlink_RestoresRealFolderWithCopiesAndKeepsSyncedFiles()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        dir.WriteFile("sync/Mappings/binds.xml", "binds");
        _linker.Apply(env, Mappings, dir.Combine("sync"));

        var result = _linker.Unlink(env, Mappings);

        Assert.True(result.Success, result.Message);
        var source = dir.Combine("game", "LIVE", "user", "client", "0", "Controls", "Mappings");
        Assert.False(DirectoryLinks.IsLink(source));
        Assert.Equal("binds", File.ReadAllText(Path.Combine(source, "binds.xml")));
        Assert.Equal("binds", File.ReadAllText(dir.Combine("sync", "Mappings", "binds.xml")));
    }
}

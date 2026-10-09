using VersePreflight.Core.Game;

namespace VersePreflight.Core.Tests.Game;

public class GameEnvironmentTests
{
    private const string Manifest = """
        {
            "Data": {
                "Branch": "sc-alpha-4.10.2",
                "RequestedP4ChangeNum": "12881860",
                "Version": "4.10.196.36804"
            }
        }
        """;

    [Fact]
    public void Detect_FindsFoldersContainingUserFolder()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("LIVE", "user"));
        Directory.CreateDirectory(dir.Combine("PTU", "USER"));
        Directory.CreateDirectory(dir.Combine("EasyAntiCheat"));
        dir.WriteFile("LIVE/build_manifest.id", Manifest);

        var envs = EnvironmentDetector.Detect(dir.Path);

        Assert.Equal(["LIVE", "PTU"], envs.Select(e => e.Name));
        Assert.Equal("sc-alpha-4.10.2", envs[0].Build!.Branch);
        Assert.Null(envs[1].Build);
    }

    [Fact]
    public void Detect_ReturnsEmptyForMissingFolder() =>
        Assert.Empty(EnvironmentDetector.Detect(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

    [Fact]
    public void TryRead_ParsesManifest()
    {
        using var dir = new TempDir();
        dir.WriteFile("build_manifest.id", Manifest);

        var build = GameBuild.TryRead(dir.Path);

        Assert.Equal(new GameBuild("sc-alpha-4.10.2", "12881860", "4.10.196.36804"), build);
    }

    [Fact]
    public void TryRead_ReturnsNullForInvalidManifest()
    {
        using var dir = new TempDir();
        dir.WriteFile("build_manifest.id", "not json");
        Assert.Null(GameBuild.TryRead(dir.Path));
    }

    [Theory]
    [InlineData("sc-alpha-4.10.2_live_12881860", true)]
    [InlineData("sc-alpha-4.10.2_live_12800000", false)]
    [InlineData("sc-alpha-4.10.1_live_12881860", false)]
    public void Matches_ComparesBranchAndChangeNumber(string label, bool expected) =>
        Assert.Equal(expected, new GameBuild("sc-alpha-4.10.2", "12881860", "").Matches(label));
}

using System.IO.Compression;
using System.Text;
using VersePreflight.Core.Game;
using VersePreflight.Core.Logging;
using VersePreflight.Core.StarStrings;

namespace VersePreflight.Core.Tests.StarStrings;

public class StarStringsTests
{
    private const string ReleaseUrl = "https://api.github.com/repos/MrKraken/StarStrings/releases/tags/latest";
    private const string AssetUrl = "https://github.com/MrKraken/StarStrings/releases/download/latest/StarStrings-LIVE.zip";
    private const string Commit = "292e6be75dff41e74180b35171fbb7af9b640509";
    private const string ReadmeUrl = $"https://api.github.com/repos/MrKraken/StarStrings/readme?ref={Commit}";

    private const string ReleaseJson = $$"""
        {
          "name": "SC LIVE Build (release-2026-10-09-292e6be)",
          "published_at": "2026-10-09T14:31:59Z",
          "target_commitish": "master",
          "body": "Automated release from commit {{Commit}}.\r\nIncludes `USER.cfg`.",
          "assets": [
            { "name": "StarStrings-LIVE.zip", "size": 2400000, "browser_download_url": "{{AssetUrl}}" }
          ]
        }
        """;

    private const string Readme = "# MrKraken's String Replacements\n### (English) Community Translated .ini for **Star Citizen [Build: sc-alpha-4.10.2_live_12881860]**";

    private static byte[] MakeZip(string iniContent)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("USER.cfg").Open())) w.Write("g_language = english");
            using (var w = new StreamWriter(zip.CreateEntry("Data/Localization/english/global.ini").Open())) w.Write(iniContent);
        }
        return buffer.ToArray();
    }

    private static StarStringsClient ClientFor(FakeHttpHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task GetLatestRelease_ParsesNameCommitAndAsset()
    {
        var client = ClientFor(new FakeHttpHandler().Respond(ReleaseUrl, ReleaseJson));

        var release = await client.GetLatestReleaseAsync(StarStringsChannel.For("LIVE")!);

        Assert.Equal("SC LIVE Build (release-2026-10-09-292e6be)", release.Name);
        Assert.Equal(Commit, release.Commit);
        Assert.Equal(new Uri(AssetUrl), release.AssetUrl);
        Assert.Equal(2400000, release.AssetSize);
    }

    [Fact]
    public async Task Check_ReportsBuildMatch()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpHandler().Respond(ReleaseUrl, ReleaseJson).Respond(ReadmeUrl, Readme);
        var installer = new StarStringsInstaller(ClientFor(handler), new PreflightLog(), dir.Combine("backup"));
        var env = new GameEnvironment("LIVE", dir.Path, new GameBuild("sc-alpha-4.10.2", "12881860", ""));

        var check = await installer.CheckAsync(env);

        Assert.NotNull(check);
        Assert.Equal("sc-alpha-4.10.2_live_12881860", check.TargetBuild);
        Assert.True(check.MatchesGameBuild);
    }

    [Fact]
    public async Task Check_ReturnsNullForEnvironmentWithoutChannel()
    {
        using var dir = new TempDir();
        var installer = new StarStringsInstaller(ClientFor(new FakeHttpHandler()), new PreflightLog(), dir.Path);

        Assert.Null(await installer.CheckAsync(new GameEnvironment("EPTU", dir.Path, null)));
    }

    [Fact]
    public async Task Install_WritesIniBacksUpOldAndSetsLanguage()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        dir.WriteFile("game/LIVE/data/Localization/english/global.ini", "old strings");
        dir.WriteFile("game/LIVE/user.cfg", "sys.openxr=42\r\n");
        var handler = new FakeHttpHandler().Respond(ReleaseUrl, ReleaseJson).Respond(AssetUrl, MakeZip("new strings"));
        var installer = new StarStringsInstaller(ClientFor(handler), new PreflightLog(), dir.Combine("backup"));
        var check = (await installer.CheckAsync(env))!;

        var status = await installer.InstallAsync(check);

        Assert.Equal(StarStringsStatus.Installed, status);
        Assert.Equal("new strings", File.ReadAllText(dir.Combine("game", "LIVE", "data", "Localization", "english", "global.ini")));
        Assert.Equal("old strings", File.ReadAllText(dir.Combine("backup", "LIVE", "global.ini")));
        Assert.Equal("sys.openxr=42\r\ng_language = english\r\n", File.ReadAllText(dir.Combine("game", "LIVE", "user.cfg")));

        Assert.Equal(StarStringsStatus.UpToDate, await installer.InstallAsync(check));
    }

    [Fact]
    public void ExtractGlobalIni_ThrowsWhenMissing()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("readme.md");

        Assert.Throws<InvalidDataException>(() => StarStringsClient.ExtractGlobalIni(buffer.ToArray()));
    }

    [Fact]
    public async Task Download_RefusesReleaseOverSizeLimit()
    {
        var handler = new FakeHttpHandler().Respond(AssetUrl, MakeZip("strings"));
        var release = new StarStringsRelease("big", DateTimeOffset.UnixEpoch, null, new Uri(AssetUrl), StarStringsClient.MaxDownloadSize + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => ClientFor(handler).DownloadGlobalIniAsync(release));
        Assert.Empty(handler.Requests); // refused before downloading anything
    }

    [Fact]
    public void ExtractGlobalIni_RefusesFileOverSizeLimit()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("Data/Localization/english/global.ini").Open())
        {
            var block = new byte[1024 * 1024];
            for (var i = 0; i <= StarStringsClient.MaxGlobalIniSize / block.Length; i++) entry.Write(block);
        }

        Assert.Throws<InvalidDataException>(() => StarStringsClient.ExtractGlobalIni(buffer.ToArray()));
    }

    [Fact]
    public void ExtractGlobalIni_ReturnsFileContent() =>
        Assert.Equal("abc", Encoding.UTF8.GetString(StarStringsClient.ExtractGlobalIni(MakeZip("abc"))));
}

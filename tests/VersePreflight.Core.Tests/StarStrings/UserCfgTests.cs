using VersePreflight.Core.StarStrings;

namespace VersePreflight.Core.Tests.StarStrings;

public class UserCfgTests
{
    [Fact]
    public void EnsureLanguage_CreatesFileWhenMissing()
    {
        using var dir = new TempDir();
        var cfg = dir.Combine("user.cfg");

        Assert.Equal(UserCfgChange.Added, UserCfg.EnsureLanguage(cfg));
        Assert.Equal("g_language = english\r\n", File.ReadAllText(cfg));
    }

    [Fact]
    public void EnsureLanguage_AppendsAndKeepsOtherSettings()
    {
        using var dir = new TempDir();
        var cfg = dir.WriteFile("user.cfg", "sys.openxr=42\r\nr_DisplayInfo = 1");

        Assert.Equal(UserCfgChange.Added, UserCfg.EnsureLanguage(cfg));
        Assert.Equal("sys.openxr=42\r\nr_DisplayInfo = 1\r\ng_language = english\r\n", File.ReadAllText(cfg));
    }

    [Fact]
    public void EnsureLanguage_ReplacesOtherLanguageInPlace()
    {
        using var dir = new TempDir();
        var cfg = dir.WriteFile("user.cfg", "g_language=german\nsys.openxr=42\n");

        Assert.Equal(UserCfgChange.Replaced, UserCfg.EnsureLanguage(cfg));
        Assert.Equal("g_language = english\nsys.openxr=42\n", File.ReadAllText(cfg));
    }

    [Fact]
    public void EnsureLanguage_LeavesCorrectFileUntouched()
    {
        using var dir = new TempDir();
        var cfg = dir.WriteFile("user.cfg", "G_Language=English\nsys.openxr=42");
        var before = File.GetLastWriteTimeUtc(cfg);

        Assert.Equal(UserCfgChange.AlreadySet, UserCfg.EnsureLanguage(cfg));
        Assert.Equal("G_Language=English\nsys.openxr=42", File.ReadAllText(cfg));
        Assert.Equal(before, File.GetLastWriteTimeUtc(cfg));
    }
}

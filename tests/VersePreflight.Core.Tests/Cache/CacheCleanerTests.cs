using VersePreflight.Core.Cache;
using VersePreflight.Core.Logging;

namespace VersePreflight.Core.Tests.Cache;

public class CacheCleanerTests
{
    [Fact]
    public void Scan_ListsOnlyExistingItemsWithSizesAndSafeDefaults()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        dir.WriteFile("game/LIVE/user/client/0/AutoPerfCaptures/cap.bin", new string('x', 100));
        dir.WriteFile("game/LIVE/user/client/0/attributes.cfg", "cfg");
        dir.WriteFile("game/LIVE/user.cfg", "g_language = english");
        dir.WriteFile("shaders/starcitizen_1/s.bin", new string('s', 50));
        var cleaner = new CacheCleaner(new FakePlatform(dir.Combine("shaders", "starcitizen_1")), new PreflightLog());

        var items = cleaner.Scan([env]);

        Assert.Equal(["LIVE:autoperf", "LIVE:cfg", "shaders"], items.Select(i => i.Id));
        Assert.Equal(100, items.Single(i => i.Id == "LIVE:autoperf").SizeBytes);
        Assert.False(items.Single(i => i.Id == "LIVE:cfg").SelectedByDefault);
        Assert.True(items.Single(i => i.Id == "shaders").SelectedByDefault);
        Assert.DoesNotContain(items.SelectMany(i => i.Paths), p => p.EndsWith("user.cfg", StringComparison.Ordinal));
    }

    [Fact]
    public void Clean_DeletesSelectedItemsOnly()
    {
        using var dir = new TempDir();
        var env = dir.CreateEnvironment();
        dir.WriteFile("game/LIVE/user/client/0/AutoPerfCaptures/cap.bin", "x");
        var cfg = dir.WriteFile("game/LIVE/user/client/0/attributes.cfg", "cfg");
        var cleaner = new CacheCleaner(new FakePlatform(), new PreflightLog());
        var items = cleaner.Scan([env]);

        cleaner.Clean(items.Where(i => i.SelectedByDefault));

        Assert.False(Directory.Exists(dir.Combine("game", "LIVE", "user", "client", "0", "AutoPerfCaptures")));
        Assert.True(File.Exists(cfg));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    public void FormatSize_UsesReadableUnits(long bytes, string expected) =>
        Assert.Equal(expected, CacheCleaner.FormatSize(bytes));
}

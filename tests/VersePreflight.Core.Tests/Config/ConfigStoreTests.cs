using VersePreflight.Core.Config;
using VersePreflight.Core.IO;

namespace VersePreflight.Core.Tests.Config;

public class ConfigStoreTests
{
    [Fact]
    public void Load_ReturnsDefaultsWhenMissing()
    {
        using var dir = new TempDir();
        var config = new ConfigStore(dir.Path, "PC1").Load();

        Assert.Null(config.GameBase);
        Assert.False(config.StarStringsEnabled);
        Assert.Equal(["Screenshots", "Control mappings", "Custom characters"], config.Links.Select(l => l.Name));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path, "PC1");
        var config = new PreflightConfig { GameBase = @"G:\Games\StarCitizen", LinkRoot = "..", StarStringsEnabled = true };
        config.Links[0].Enabled = false;

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(@"G:\Games\StarCitizen", loaded.GameBase);
        Assert.Equal("..", loaded.LinkRoot);
        Assert.True(loaded.StarStringsEnabled);
        Assert.False(loaded.Links[0].Enabled);
        Assert.EndsWith("config.PC1.json", store.FilePath);
    }

    [Fact]
    public void ConfigFilesArePerMachine()
    {
        using var dir = new TempDir();
        new ConfigStore(dir.Path, "DESKTOP").Save(new PreflightConfig { GameBase = "A" });
        new ConfigStore(dir.Path, "ALLY").Save(new PreflightConfig { GameBase = "B" });

        Assert.Equal("A", new ConfigStore(dir.Path, "DESKTOP").Load().GameBase);
        Assert.Equal("B", new ConfigStore(dir.Path, "ALLY").Load().GameBase);
    }

    [Fact]
    public void ResolveLinkRoot_IsRelativeToConfigDirectory()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Combine("sync", "VersePreflight"), "PC1");

        Assert.Equal(PathUtil.Normalize(dir.Combine("sync")), store.ResolveLinkRoot(new PreflightConfig { LinkRoot = ".." }));
        Assert.Null(store.ResolveLinkRoot(new PreflightConfig()));
    }

    // ---------- one-time import for the Store version ----------

    [Fact]
    public void Import_CopiesThisMachinesSettingsAndKeepsLinkFolderPointingAtTheSamePlace()
    {
        using var dir = new TempDir();
        var previous = dir.Combine("roaming", "VersePreflight");
        new ConfigStore(previous, "PC1").Save(new PreflightConfig { GameBase = "G", LinkRoot = "..", StarStringsEnabled = true });
        new ConfigStore(previous, "OTHERPC").Save(new PreflightConfig { GameBase = "other" });
        var store = new ConfigStore(dir.Combine("package", "LocalState"), "PC1");

        Assert.True(store.ImportIfMissing(previous));

        var imported = store.Load();
        Assert.Equal("G", imported.GameBase);
        Assert.True(imported.StarStringsEnabled);
        Assert.Equal(PathUtil.Normalize(dir.Combine("roaming")), store.ResolveLinkRoot(imported));
        Assert.Equal(["config.PC1.json"], Directory.GetFiles(store.ConfigDirectory).Select(Path.GetFileName)); // nothing else copied
        Assert.Equal("..", new ConfigStore(previous, "PC1").Load().LinkRoot); // original untouched
    }

    [Fact]
    public void Import_NeverOverwritesExistingSettings()
    {
        using var dir = new TempDir();
        new ConfigStore(dir.Combine("previous"), "PC1").Save(new PreflightConfig { GameBase = "old" });
        var store = new ConfigStore(dir.Combine("store"), "PC1");
        store.Save(new PreflightConfig { GameBase = "current" });

        Assert.False(store.ImportIfMissing(dir.Combine("previous")));
        Assert.Equal("current", store.Load().GameBase);
    }

    [Fact]
    public void Import_DoesNothingWithoutPreviousSettings()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Combine("store"), "PC1");

        Assert.False(store.ImportIfMissing(dir.Combine("missing")));
        Assert.False(store.Exists);
    }

    [Fact]
    public void Import_RejectsDamagedOrOversizedFilesWithoutCreatingSettings()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Combine("store"), "PC1");

        dir.WriteFile("damaged/config.PC1.json", "{ not json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => store.ImportIfMissing(dir.Combine("damaged")));

        dir.WriteFile("huge/config.PC1.json", new string(' ', (int)ConfigStore.MaxImportSize + 1));
        Assert.Throws<InvalidDataException>(() => store.ImportIfMissing(dir.Combine("huge")));

        Assert.False(store.Exists);
    }

    [Fact]
    public void CreateDefault_UsesAppDirectoryInPortableMode()
    {
        using var dir = new TempDir();
        var app = dir.Combine("app");
        Directory.CreateDirectory(app);

        Assert.Equal(dir.Combine("roaming"), ConfigStore.CreateDefault(app, dir.Combine("roaming"), "PC1").ConfigDirectory);

        Assert.False(ConfigStore.CreateDefault(app, dir.Combine("roaming"), "PC1").IsPortable);
        Assert.True(ConfigStore.CreateDefault(app, dir.Combine("roaming"), "PC1", forcePortable: true).IsPortable);

        File.WriteAllText(Path.Combine(app, ConfigStore.PortableMarkerFile), "");
        var store = ConfigStore.CreateDefault(app, dir.Combine("roaming"), "PC1");
        Assert.Equal(app, store.ConfigDirectory);
        Assert.True(store.IsPortable);
    }
}

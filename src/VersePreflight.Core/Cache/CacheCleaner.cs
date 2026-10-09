using VersePreflight.Core.Game;
using VersePreflight.Core.IO;
using VersePreflight.Core.Logging;
using VersePreflight.Core.Platforms;

namespace VersePreflight.Core.Cache;

/// <summary>Something that can be cleaned, shown to the user as a checkbox with an explanation.</summary>
public sealed record CacheItem(
    string Id,
    string Name,
    string Description,
    string? EnvironmentName,
    IReadOnlyList<string> Paths,
    long SizeBytes,
    bool SelectedByDefault);

public sealed class CacheCleaner(IPlatform platform, PreflightLog log)
{
    /// <summary>Lists the cleanable items that currently exist, with their sizes.</summary>
    public IReadOnlyList<CacheItem> Scan(IEnumerable<GameEnvironment> environments)
    {
        var items = new List<CacheItem>();

        foreach (var env in environments)
        {
            var client = PathUtil.ResolveCaseInsensitive(env.Path, "user/client/0");

            AddIfExists(items, $"{env.Name}:autoperf", "Performance captures",
                "Automatic performance recordings. Safe to delete; they only use disk space.",
                env.Name, [PathUtil.ResolveCaseInsensitive(client, "AutoPerfCaptures")], selected: true);

            AddIfExists(items, $"{env.Name}:debuggui", "Debug GUI data",
                "Developer debug overlay data. Safe to delete.",
                env.Name, [PathUtil.ResolveCaseInsensitive(client, "DebugGUI")], selected: true);

            var cfgFiles = Directory.Exists(client) ? Directory.GetFiles(client, "*.cfg") : [];
            AddIfExists(items, $"{env.Name}:cfg", "Client .cfg files",
                "Configuration files the game writes in user/client/0. Deleting them can fix settings problems " +
                "after a patch, but resets anything stored in them. Your user.cfg in the game folder is not affected.",
                env.Name, cfgFiles, selected: false);
        }

        AddIfExists(items, "shaders", "Shader cache",
            "Compiled shaders. The game rebuilds them on next launch (the first start is slower). " +
            "Recommended after every patch.",
            null, platform.GetShaderCacheDirectories(), selected: true);

        return items;
    }

    /// <summary>Deletes the given items. Returns the number of bytes freed.</summary>
    public long Clean(IEnumerable<CacheItem> items)
    {
        long freed = 0;
        foreach (var item in items)
        {
            var label = item.EnvironmentName is null ? item.Name : $"{item.EnvironmentName} - {item.Name}";
            var failed = false;
            foreach (var path in item.Paths)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                    log.Warning($"{label}: could not delete '{path}': {e.Message}");
                }
            }

            if (!failed)
            {
                freed += item.SizeBytes;
                log.Success($"{label}: cleaned ({FormatSize(item.SizeBytes)}).");
            }
        }
        return freed;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    private static void AddIfExists(List<CacheItem> items, string id, string name, string description,
        string? environmentName, IReadOnlyList<string> paths, bool selected)
    {
        var existing = paths.Where(Path.Exists).ToList();
        if (existing.Count == 0) return;
        items.Add(new CacheItem(id, name, description, environmentName, existing, existing.Sum(SizeOf), selected));
    }

    private static long SizeOf(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

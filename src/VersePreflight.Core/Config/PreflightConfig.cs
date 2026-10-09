using System.Text.Json;
using System.Text.Json.Serialization;
using VersePreflight.Core.IO;
using VersePreflight.Core.Links;

namespace VersePreflight.Core.Config;

public sealed class PreflightConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Folder containing LIVE, PTU, ... (e.g. "...\Roberts Space Industries\StarCitizen").</summary>
    public string? GameBase { get; set; }

    /// <summary>Where linked folders are stored. May be relative to the config folder (e.g. "..").</summary>
    public string? LinkRoot { get; set; }

    public List<LinkDefinition> Links { get; set; } = LinkDefinition.Defaults();

    /// <summary>Opt-in: StarStrings is a third-party fan project, so it is off until the user enables it.</summary>
    public bool StarStringsEnabled { get; set; }

    /// <summary>PTU strings often lag new builds, so PTU installs need a separate opt-in.</summary>
    public bool StarStringsIncludePtu { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PreflightConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves the per-machine config. In portable mode (a "VersePreflight.portable" file
/// next to the executable) the config lives beside the exe, so one synced folder can serve
/// several PCs; each machine still gets its own file.
/// </summary>
public sealed class ConfigStore
{
    public const string PortableMarkerFile = AppInfo.Id + ".portable";

    public ConfigStore(string configDirectory, string machineName)
    {
        ConfigDirectory = configDirectory;
        FilePath = Path.Combine(configDirectory, $"config.{machineName}.json");
    }

    public string ConfigDirectory { get; }

    public string FilePath { get; }

    public static ConfigStore CreateDefault(string appDirectory, string platformConfigDirectory, string machineName)
    {
        var portable = File.Exists(Path.Combine(appDirectory, PortableMarkerFile));
        return new ConfigStore(portable ? appDirectory : platformConfigDirectory, machineName);
    }

    public bool Exists => File.Exists(FilePath);

    public PreflightConfig Load()
    {
        if (!Exists) return new PreflightConfig();
        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize(stream, ConfigJsonContext.Default.PreflightConfig) ?? new PreflightConfig();
    }

    public void Save(PreflightConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.PreflightConfig));
        File.Move(temp, FilePath, overwrite: true); // atomic replace, so a crash never leaves a half-written config
    }

    /// <summary>The config's LinkRoot as an absolute path, or null if not set.</summary>
    public string? ResolveLinkRoot(PreflightConfig config) =>
        string.IsNullOrWhiteSpace(config.LinkRoot) ? null : PathUtil.ResolveRelativeTo(config.LinkRoot, ConfigDirectory);
}

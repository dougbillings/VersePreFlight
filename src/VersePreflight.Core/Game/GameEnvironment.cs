using System.Text.Json;
using VersePreflight.Core.IO;

namespace VersePreflight.Core.Game;

/// <summary>An installed game channel such as LIVE or PTU.</summary>
public sealed record GameEnvironment(string Name, string Path, GameBuild? Build);

/// <summary>Build information from the channel's <c>build_manifest.id</c>.</summary>
public sealed record GameBuild(string Branch, string ChangeNumber, string Version)
{
    /// <summary>e.g. "sc-alpha-4.10.2 (4.10.196.36804, CL 12881860)"</summary>
    public override string ToString() => $"{Branch} ({Version}, CL {ChangeNumber})";

    /// <summary>
    /// True if a build label such as "sc-alpha-4.10.2_live_12881860" refers to this build.
    /// </summary>
    public bool Matches(string buildLabel) =>
        buildLabel.StartsWith(Branch, StringComparison.OrdinalIgnoreCase) &&
        buildLabel.EndsWith(ChangeNumber, StringComparison.Ordinal);

    public static GameBuild? TryRead(string environmentPath)
    {
        var manifest = PathUtil.ResolveCaseInsensitive(environmentPath, "build_manifest.id");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var data = doc.RootElement.GetProperty("Data");
            var branch = data.GetProperty("Branch").GetString();
            var change = data.GetProperty("RequestedP4ChangeNum").GetString();
            var version = data.TryGetProperty("Version", out var v) ? v.GetString() : null;
            return branch is null || change is null ? null : new GameBuild(branch, change, version ?? "");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

public static class EnvironmentDetector
{
    /// <summary>Every sub-folder of the game base folder that contains a "user" folder (LIVE, PTU, ...).</summary>
    public static IReadOnlyList<GameEnvironment> Detect(string gameBasePath)
    {
        if (!Directory.Exists(gameBasePath)) return [];

        return Directory.EnumerateDirectories(gameBasePath)
            .Where(dir => Directory.Exists(PathUtil.ResolveCaseInsensitive(dir, "user")))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(dir => new GameEnvironment(System.IO.Path.GetFileName(dir), dir, GameBuild.TryRead(dir)))
            .ToList();
    }
}

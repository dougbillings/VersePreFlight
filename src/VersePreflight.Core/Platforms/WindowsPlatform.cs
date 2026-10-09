using System.Diagnostics;
using System.Runtime.Versioning;

namespace VersePreflight.Core.Platforms;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    private static readonly string[] GameProcessNames = ["StarCitizen", "RSI Launcher"];

    // Relative to a drive root; the launcher's default plus common custom locations
    private static readonly string[] GameBaseRelativePaths =
    [
        @"Program Files\Roberts Space Industries\StarCitizen",
        @"Roberts Space Industries\StarCitizen",
        @"Games\Roberts Space Industries\StarCitizen",
        @"StarCitizen",
    ];

    public string Name => "Windows";

    public string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Id);

    public string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Id);

    public IReadOnlyList<string> FindGameBaseCandidates() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .SelectMany(d => GameBaseRelativePaths.Select(rel => Path.Combine(d.RootDirectory.FullName, rel)))
            .Where(Directory.Exists)
            .ToList();

    public IReadOnlyList<string> GetShaderCacheDirectories()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Star Citizen");
        return Directory.Exists(root)
            ? Directory.GetDirectories(root, "starcitizen*")
            : [];
    }

    public IReadOnlyList<string> GetRunningGameProcesses()
    {
        var running = new List<string>();
        foreach (var name in GameProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0) running.Add(name);
            foreach (var p in processes) p.Dispose();
        }
        return running;
    }
}

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

    public string ConfigDirectory => PackageStateDirectory ?? RegularConfigDirectory;

    public string DataDirectory => PackageStateDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Id);

    // The Store version only reads this folder (never writes to it), so it sees the real files
    public string? PreviousConfigDirectory => PackageIdentity.IsPackaged ? RegularConfigDirectory : null;

    private static string RegularConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Id);

    /// <summary>
    /// Store (MSIX) installs keep everything in the package's own LocalState folder. Anywhere else under
    /// AppData, files they create are redirected to a hidden per-package copy, so paths shown in the log
    /// or opened in Explorer would point at nothing.
    /// </summary>
    private static string? PackageStateDirectory => PackageIdentity.FamilyName is { } family
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family, "LocalState")
        : null;

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

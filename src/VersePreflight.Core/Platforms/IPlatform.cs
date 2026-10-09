namespace VersePreflight.Core.Platforms;

/// <summary>
/// Everything that differs between operating systems. Core features take an
/// <see cref="IPlatform"/> so they can be tested with fakes and so Linux
/// (Wine/Proton installs) can be added without touching feature code.
/// </summary>
public interface IPlatform
{
    string Name { get; }

    /// <summary>Per-user settings folder (used when not running in portable mode).</summary>
    string ConfigDirectory { get; }

    /// <summary>Per-user, per-machine data folder for local backups. Never synced.</summary>
    string DataDirectory { get; }

    /// <summary>Likely game base folders (the folder containing LIVE, PTU, ...) that exist on this machine.</summary>
    IReadOnlyList<string> FindGameBaseCandidates();

    /// <summary>Shader cache folders the game recreates on next launch.</summary>
    IReadOnlyList<string> GetShaderCacheDirectories();

    /// <summary>Names of running game/launcher processes; empty when the game is closed.</summary>
    IReadOnlyList<string> GetRunningGameProcesses();
}

public static class Platform
{
    public static IPlatform Current { get; } = Create();

    private static IPlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        throw new PlatformNotSupportedException($"{AppInfo.Name} currently supports Windows only. Linux support is planned.");
    }
}

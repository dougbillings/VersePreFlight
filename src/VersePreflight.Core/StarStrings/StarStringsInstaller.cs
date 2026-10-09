using System.Security.Cryptography;
using VersePreflight.Core.Game;
using VersePreflight.Core.IO;
using VersePreflight.Core.Logging;

namespace VersePreflight.Core.StarStrings;

public enum StarStringsStatus { Installed, UpToDate, Skipped, Failed }

public sealed record StarStringsCheck(
    GameEnvironment Environment,
    StarStringsChannel Channel,
    StarStringsRelease Release,
    string? TargetBuild,
    bool? MatchesGameBuild);

public sealed class StarStringsInstaller(StarStringsClient client, PreflightLog log, string backupDirectory)
{
    public const string GlobalIniSubPath = "data/Localization/english/global.ini";

    /// <summary>
    /// Finds the latest release for an environment and whether it was built for the installed game build.
    /// Returns null for environments without a StarStrings channel.
    /// </summary>
    public async Task<StarStringsCheck?> CheckAsync(GameEnvironment environment, CancellationToken ct = default)
    {
        var channel = StarStringsChannel.For(environment.Name);
        if (channel is null) return null;

        var release = await client.GetLatestReleaseAsync(channel, ct).ConfigureAwait(false);
        var targetBuild = await client.GetTargetBuildAsync(release, ct).ConfigureAwait(false);
        bool? matches = targetBuild is not null && environment.Build is not null
            ? environment.Build.Matches(targetBuild)
            : null;
        return new StarStringsCheck(environment, channel, release, targetBuild, matches);
    }

    public async Task<StarStringsStatus> InstallAsync(StarStringsCheck check, CancellationToken ct = default)
    {
        var env = check.Environment;
        try
        {
            log.Info($"{env.Name}: downloading StarStrings {check.Release.Name} ({check.Release.AssetSize / 1048576.0:0.0} MB)...");
            var newIni = await client.DownloadGlobalIniAsync(check.Release, ct).ConfigureAwait(false);
            var status = Install(env, newIni, check.Release.Name);
            EnsureLanguage(env);
            return status;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            log.Error($"{env.Name}: StarStrings update failed: {e.Message}");
            return StarStringsStatus.Failed;
        }
    }

    internal StarStringsStatus Install(GameEnvironment env, byte[] newIni, string releaseName)
    {
        var dest = PathUtil.ResolveCaseInsensitive(env.Path, GlobalIniSubPath);
        if (File.Exists(dest) && SHA256.HashData(File.ReadAllBytes(dest)).AsSpan().SequenceEqual(SHA256.HashData(newIni)))
        {
            log.Success($"{env.Name}: StarStrings is already up to date.");
            return StarStringsStatus.UpToDate;
        }

        if (File.Exists(dest))
        {
            // One local (never synced) backup of the previous file, in case the new one has problems
            var backup = Path.Combine(backupDirectory, env.Name, "global.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(dest, backup, overwrite: true);
            log.Info($"{env.Name}: previous global.ini backed up to '{backup}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var temp = dest + ".tmp";
        File.WriteAllBytes(temp, newIni);
        File.Move(temp, dest, overwrite: true);
        log.Success($"{env.Name}: installed StarStrings {releaseName}.");
        return StarStringsStatus.Installed;
    }

    private void EnsureLanguage(GameEnvironment env)
    {
        var userCfg = PathUtil.ResolveCaseInsensitive(env.Path, "user.cfg");
        switch (UserCfg.EnsureLanguage(userCfg))
        {
            case UserCfgChange.Added: log.Info($"{env.Name}: added 'g_language = english' to user.cfg."); break;
            case UserCfgChange.Replaced: log.Warning($"{env.Name}: changed the g_language setting in user.cfg to english."); break;
        }
    }
}

using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using VersePreflight.Core;

namespace VersePreflight.App.Services;

/// <summary>
/// App self-update through Velopack, using this project's GitHub Releases as the only source.
/// Does nothing when the app wasn't installed by Velopack (e.g. a development build).
/// </summary>
public sealed class AppUpdater
{
    private readonly UpdateManager? _manager;

    public AppUpdater()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(AppInfo.RepositoryUrl, null, false, null), null, null);
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception)
        {
            // Not a Velopack install: updates are simply unavailable
        }
    }

    public bool IsSupported => _manager is not null;

    /// <summary>The newer release, or null if up to date (or updates aren't supported).</summary>
    public async Task<UpdateInfo?> CheckAsync() =>
        _manager is null ? null : await _manager.CheckForUpdatesAsync();

    /// <summary>Downloads the update, then exits and restarts into the new version.</summary>
    public async Task DownloadAndRestartAsync(UpdateInfo update, Action<int> progress)
    {
        if (_manager is null) return;
        await _manager.DownloadUpdatesAsync(update, progress, CancellationToken.None);
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease, null);
    }

    /// <summary>
    /// The folder that holds the app and survives updates, and whether it's a portable package.
    /// For Velopack installs this is the root folder (the app itself runs from its "current" subfolder).
    /// </summary>
    public static (string Directory, bool IsPortable) GetAppRoot()
    {
        try
        {
            if (VelopackLocator.IsCurrentSet && VelopackLocator.Current is { } locator
                && locator.RootAppDir is { } root && File.Exists(locator.UpdateExePath))
                return (root, locator.IsPortable);
        }
        catch (Exception)
        {
            // Fall back to the executable's folder
        }
        return (AppContext.BaseDirectory, false);
    }
}

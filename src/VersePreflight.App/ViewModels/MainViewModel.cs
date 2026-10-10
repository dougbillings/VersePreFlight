using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using VersePreflight.App.Services;
using VersePreflight.App.Theme;
using VersePreflight.Core;
using VersePreflight.Core.Cache;
using VersePreflight.Core.Config;
using VersePreflight.Core.Feedback;
using VersePreflight.Core.Game;
using VersePreflight.Core.IO;
using VersePreflight.Core.Links;
using VersePreflight.Core.Logging;
using VersePreflight.Core.Platforms;
using VersePreflight.Core.StarStrings;

namespace VersePreflight.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogEntries = 500;

    private readonly IPlatform _platform;
    private readonly ConfigStore _store;
    private readonly PreflightLog _log;
    private readonly IUiServices _ui;
    private readonly FolderLinker _linker;
    private readonly CacheCleaner _cleaner;
    private readonly HttpClient _http;
    private readonly StarStringsInstaller _starStrings;
    private readonly DispatcherTimer _gameTimer;
    private readonly AppUpdater _updater;
    private readonly Dictionary<string, bool> _cacheSelections = new(StringComparer.Ordinal);
    private PreflightConfig _config;
    private IReadOnlyList<GameEnvironment> _environments = [];
    private UpdateInfo? _pendingUpdate;

    public MainViewModel(IPlatform platform, ConfigStore store, PreflightLog log, IUiServices ui, AppUpdater updater)
    {
        _platform = platform;
        _store = store;
        _log = log;
        _ui = ui;
        _updater = updater;
        _log.EntryWritten += OnLogEntry;

        try
        {
            _config = store.Load();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _config = new PreflightConfig();
            _log.Error($"Could not read settings ({e.Message}). Starting with defaults.");
        }

        _linker = new FolderLinker(log, Environment.MachineName);
        _cleaner = new CacheCleaner(platform, log);
        _http = StarStringsClient.CreateHttpClient();
        _starStrings = new StarStringsInstaller(new StarStringsClient(_http), log,
            Path.Combine(platform.DataDirectory, "StarStrings-backup"));

        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _gameTimer.Tick += async (_, _) => await CheckGameRunningAsync();
    }

    public ObservableCollection<EnvironmentViewModel> Environments { get; } = [];
    public ObservableCollection<CacheItemViewModel> CacheItems { get; } = [];
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    public string Version => $"v{AppInfo.Version}";
    public string Disclaimer => AppInfo.ShortDisclaimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    public partial bool IsGameRunning { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "INITIALIZING";
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = Hud.AccentBrush;
    [ObservableProperty] public partial object? Overlay { get; set; }
    [ObservableProperty] public partial string CacheTotalText { get; set; } = "";
    [ObservableProperty] public partial string LinkRootText { get; set; } = "";
    [ObservableProperty] public partial bool HasEnvironments { get; set; }
    [ObservableProperty] public partial bool HasCacheItems { get; set; }
    [ObservableProperty] public partial bool IsUpdateAvailable { get; set; }
    [ObservableProperty] public partial string UpdateButtonText { get; set; } = "";

    public bool CanAct => !IsBusy && !IsGameRunning;

    public async Task InitializeAsync()
    {
        _log.Info($"{AppInfo.Name} {AppInfo.Version} started on {Environment.MachineName}" +
                  (_store.IsPortable ? " (portable)." : "."));
        await CheckGameRunningAsync();
        _gameTimer.Start();

        if (!_store.Exists || _config.GameBase is null)
        {
            SetStatus("SETUP REQUIRED", Hud.WarnBrush);
            OpenSetup(isFirstRun: !_store.Exists);
            return;
        }
        await RefreshAsync();
        if (_config.CheckForUpdates) await CheckForAppUpdateAsync();
    }

    private async Task CheckForAppUpdateAsync()
    {
        if (!_updater.IsSupported) return;
        try
        {
            _pendingUpdate = await _updater.CheckAsync();
            if (_pendingUpdate is null) return;
            var version = _pendingUpdate.TargetFullRelease.Version;
            UpdateButtonText = $"UPDATE TO v{version}";
            IsUpdateAvailable = true;
            _log.Info($"{AppInfo.Name} v{version} is available.");
        }
        catch (Exception e)
        {
            // An update check must never get in the way of using the app
            _log.Warning($"Could not check for app updates: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task InstallAppUpdateAsync()
    {
        if (_pendingUpdate is not { } update || IsBusy) return;
        var version = update.TargetFullRelease.Version;
        if (!await ConfirmAsync("APP UPDATE",
                $"{AppInfo.Name} v{version} is available (you have v{AppInfo.Version}).\n\n" +
                "Download it now? The app restarts when the update is ready. Your settings are kept.",
                "UPDATE & RESTART"))
            return;

        IsBusy = true;
        try
        {
            _log.Info($"Downloading {AppInfo.Name} v{version}...");
            await _updater.DownloadAndRestartAsync(update, percent =>
                Dispatcher.UIThread.Post(() => SetStatus($"DOWNLOADING UPDATE  {percent}%", Hud.AccentBrush)));
        }
        catch (Exception e)
        {
            _log.Error($"App update failed: {e.Message}");
            IsBusy = false;
            UpdateIdleStatus();
        }
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private Task RunPreflightAsync() => RunOperationAsync("PREFLIGHT", async () =>
    {
        if (_store.ResolveLinkRoot(_config) is not null && !await LinkFoldersCoreAsync()) return false;
        await CleanCacheCoreAsync();
        if (_config.StarStringsEnabled) await UpdateStarStringsCoreAsync();
        return true;
    }, isPreflight: true);

    [RelayCommand] private Task LinkFoldersAsync() => RunOperationAsync("LINK FOLDERS", LinkFoldersCoreAsync);
    [RelayCommand] private Task CleanCacheAsync() => RunOperationAsync("CACHE CLEAN", CleanCacheCoreAsync);
    [RelayCommand] private Task UpdateStarStringsAsync() => RunOperationAsync("STARSTRINGS", UpdateStarStringsCoreAsync);

    [RelayCommand]
    private Task UndoLinksAsync() => RunOperationAsync("UNDO LINKS", async () =>
    {
        if (!await ConfirmAsync("UNDO LINKS",
                "Replace every folder link with a normal folder?\n\nYour linked files are copied back into the game " +
                "folders. Your link folder and everything in it stays as it is.", "UNDO LINKS"))
            return false;

        var environments = _environments;
        var links = _config.Links;
        await Task.Run(() =>
        {
            foreach (var env in environments)
            foreach (var link in links)
                _linker.Unlink(env, link);
        });
        return true;
    });

    [RelayCommand] private Task RefreshNowAsync() => IsBusy ? Task.CompletedTask : RefreshAsync();

    [RelayCommand] private void OpenSettings() => OpenSetup(isFirstRun: false);

    [RelayCommand] private void OpenAbout() => Overlay = new AboutViewModel(_ui, () => Overlay = null);

    [RelayCommand]
    private void OpenFeedback()
    {
        var logLines = LogEntries.TakeLast(60).Select(e => FeedbackDiagnostics.Redact(e.FullText)).ToList();
        Overlay = new FeedbackViewModel(FeedbackDiagnostics.Redact(BuildDiagnosticsSummary()), logLines, _ui,
            message => _log.Info(message), () => Overlay = null);
    }

    /// <summary>Non-personal facts that help with a bug report; redacted again before use.</summary>
    private string BuildDiagnosticsSummary()
    {
        var text = new System.Text.StringBuilder()
            .AppendLine($"{AppInfo.Name} {AppInfo.Version}{(_store.IsPortable ? " (portable)" : "")}")
            .AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                        $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), .NET {Environment.Version}")
            .AppendLine($"Settings: {_config.Links.Count(l => l.Enabled)} of {_config.Links.Count} links on, " +
                        $"link folder {(_store.ResolveLinkRoot(_config) is null ? "not set" : "set")}, " +
                        $"StarStrings {(_config.StarStringsEnabled ? "on" : "off")}" +
                        $"{(_config.StarStringsIncludePtu ? " (incl. PTU)" : "")}, " +
                        $"update check {(_config.CheckForUpdates ? "on" : "off")}");

        if (Environments.Count == 0) text.AppendLine("Installs: none detected");
        foreach (var env in Environments)
        {
            text.AppendLine($"{env.Name}: {env.Environment.Build?.ToString() ?? "build unknown"}");
            foreach (var link in env.Links) text.AppendLine($"  {link.Name}: {link.StateText}");
            text.AppendLine($"  StarStrings: {env.StarStringsState}");
        }
        text.Append($"Status: {StatusText}");
        return text.ToString();
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        await _ui.CopyTextAsync(string.Join(Environment.NewLine, LogEntries.Select(e => e.FullText)));
        _log.Info("Log copied to the clipboard.");
    }

    [RelayCommand]
    private async Task OpenLogFileAsync()
    {
        if (_log.FilePath is { } path && File.Exists(path)) await _ui.OpenPathAsync(path);
    }

    // ---------------------------------------------------------------- operations

    private async Task RunOperationAsync(string name, Func<Task<bool>> body, bool isPreflight = false)
    {
        if (IsBusy) return;
        await CheckGameRunningAsync();
        if (IsGameRunning)
        {
            _log.Warning("Close Star Citizen and the RSI Launcher before running Verse Preflight.");
            return;
        }

        IsBusy = true;
        SetStatus($"{name} IN PROGRESS", Hud.AccentBrush);
        var errorsBefore = _log.ErrorCount;
        bool completed;
        try
        {
            completed = await body();
        }
        catch (Exception e)
        {
            // Last line of defence: an unexpected error must never take the app down mid-operation
            _log.Error($"{name}: unexpected error: {e.Message}");
            completed = true;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        var errors = _log.ErrorCount - errorsBefore;
        if (errors > 0) _log.Info("Something not right? Press FEEDBACK to report it; the log is included for you.");
        if (errors > 0) SetStatus($"{name} FINISHED WITH {errors} ISSUE(S) · SEE LOG", Hud.BadBrush);
        else if (!completed) SetStatus($"{name} CANCELED", Hud.TextDimBrush);
        else if (isPreflight) SetStatus("PREFLIGHT COMPLETE · CLEARED FOR LAUNCH", Hud.GoodBrush);
        else SetStatus($"{name} COMPLETE", Hud.GoodBrush);
    }

    private async Task<bool> LinkFoldersCoreAsync()
    {
        var linkRoot = _store.ResolveLinkRoot(_config);
        if (linkRoot is null)
        {
            _log.Warning("Choose a link folder in Settings first.");
            return false;
        }

        var environments = _environments;
        var links = _config.Links.Where(l => l.Enabled).ToList();
        var plans = await Task.Run(() => environments.SelectMany(e => links.Select(l => _linker.Plan(e, l, linkRoot))).ToList());

        var conflicts = plans.Sum(p => p.Conflicts.Count);
        var relinks = plans.Count(p => p.State == LinkState.WrongTarget);
        if (conflicts > 0 || relinks > 0)
        {
            var message = "";
            if (conflicts > 0)
                message += $"{conflicts} file(s) in the game folders differ from your copies in the link folder. " +
                           $"Your copies are kept; the game's versions are moved to '{FolderLinker.BackupFolderName}' in your link folder.\n\n";
            if (relinks > 0)
                message += $"{relinks} existing link(s) point to a different folder and will be redirected to:\n{linkRoot}\n\n";
            if (!await ConfirmAsync("CONFIRM FOLDER LINKS", message + "Continue?", "LINK FOLDERS")) return false;
        }

        _log.Info($"Linking folders into '{linkRoot}'...");
        await Task.Run(() =>
        {
            foreach (var env in environments)
            foreach (var link in links)
                _linker.Apply(env, link, linkRoot);
        });
        return true;
    }

    private async Task<bool> CleanCacheCoreAsync()
    {
        var selected = CacheItems.Where(c => c.IsSelected).Select(c => c.Item).ToList();
        if (selected.Count == 0)
        {
            _log.Info("Cache: nothing selected.");
            return true;
        }

        var freed = await Task.Run(() => _cleaner.Clean(selected));
        _log.Success($"Cache cleanup freed {CacheCleaner.FormatSize(freed)}.");
        return true;
    }

    private async Task<bool> UpdateStarStringsCoreAsync()
    {
        if (!_config.StarStringsEnabled)
        {
            _log.Info("StarStrings is turned off. Enable it in Settings.");
            return false;
        }

        foreach (var env in _environments)
        {
            var channel = StarStringsChannel.For(env.Name);
            if (channel is null || (channel.IsTestBuild && !_config.StarStringsIncludePtu)) continue;

            StarStringsCheck? check;
            try
            {
                check = await _starStrings.CheckAsync(env);
            }
            catch (Exception e) when (IsNetworkOrDataError(e))
            {
                _log.Error($"{env.Name}: could not check StarStrings: {e.Message}");
                continue;
            }
            if (check is null) continue;

            if (check.MatchesGameBuild == false &&
                !await ConfirmAsync("BUILD MISMATCH",
                    $"The latest StarStrings for {env.Name} was made for {check.TargetBuild}, but your game is " +
                    $"{env.Build?.Branch}_{env.Build?.ChangeNumber}.\n\nIt may show missing or wrong text until StarStrings " +
                    "is updated for this patch. Install it anyway?", "INSTALL ANYWAY"))
            {
                _log.Info($"{env.Name}: StarStrings skipped (build mismatch).");
                continue;
            }

            var status = await _starStrings.InstallAsync(check);
            if (status is StarStringsStatus.Installed or StarStringsStatus.UpToDate)
            {
                _config.StarStringsInstalled[env.Name] = check.Release.Name;
                SaveConfig();
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- refresh & status

    private async Task RefreshAsync()
    {
        var gameBase = _config.GameBase;
        var linkRoot = _store.ResolveLinkRoot(_config);
        var links = _config.Links.Where(l => l.Enabled).ToList();

        var (environments, cards, cacheItems) = await Task.Run(() =>
        {
            IReadOnlyList<GameEnvironment> envs = gameBase is null ? [] : EnvironmentDetector.Detect(gameBase);
            var vms = envs.Select(e => new EnvironmentViewModel(e, links.Select(l => PlanLink(e, l, linkRoot)).ToList())).ToList();
            return (envs, vms, _cleaner.Scan(envs));
        });

        _environments = environments;
        Environments.Clear();
        foreach (var card in cards) Environments.Add(card);
        HasEnvironments = Environments.Count > 0;
        LinkRootText = linkRoot is null ? "LINK FOLDER NOT SET" : $"LINK FOLDER  {linkRoot}";

        CacheItems.Clear();
        foreach (var item in cacheItems)
        {
            var selected = _cacheSelections.TryGetValue(item.Id, out var s) ? s : item.SelectedByDefault;
            CacheItems.Add(new CacheItemViewModel(item, selected, OnCacheSelectionChanged));
        }
        HasCacheItems = CacheItems.Count > 0;
        UpdateCacheTotal();

        foreach (var card in cards) await RefreshStarStringsAsync(card);
        UpdateIdleStatus();
    }

    private LinkStatusViewModel PlanLink(GameEnvironment environment, LinkDefinition link, string? linkRoot)
    {
        if (linkRoot is null) return LinkStatusViewModel.From(link, null);
        try
        {
            return LinkStatusViewModel.From(link, _linker.Plan(environment, link, linkRoot));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LinkStatusViewModel.Failed(link, e.Message);
        }
    }

    private async Task RefreshStarStringsAsync(EnvironmentViewModel card)
    {
        if (!_config.StarStringsEnabled)
        {
            card.SetStarStrings("OFF", "Optional community strings · enable in Settings", Hud.TextDimBrush, false);
            return;
        }
        var channel = StarStringsChannel.For(card.Name);
        if (channel is null)
        {
            card.SetStarStrings("N/A", "No StarStrings release for this channel", Hud.TextDimBrush, false);
            return;
        }
        if (channel.IsTestBuild && !_config.StarStringsIncludePtu)
        {
            card.SetStarStrings("OFF", "PTU is turned off in Settings", Hud.TextDimBrush, false);
            return;
        }

        card.SetStarStrings("CHECKING", "Contacting GitHub...", Hud.TextDimBrush, false);
        try
        {
            var check = await _starStrings.CheckAsync(card.Environment);
            if (check is null) return;

            var release = ShortReleaseName(check.Release.Name);
            var buildNote = check.MatchesGameBuild switch
            {
                true => "matches your game build",
                false => $"made for {check.TargetBuild}, not your build yet",
                null => "build not verified",
            };
            var iniPath = PathUtil.ResolveCaseInsensitive(card.Environment.Path, StarStringsInstaller.GlobalIniSubPath);
            var installed = _config.StarStringsInstalled.TryGetValue(card.Name, out var name)
                            && name == check.Release.Name && File.Exists(iniPath);

            if (installed)
                card.SetStarStrings("UP TO DATE", $"{release} · {buildNote}", Hud.GoodBrush, false);
            else
                card.SetStarStrings("UPDATE", $"{release} available · {buildNote}",
                    check.MatchesGameBuild == false ? Hud.WarnBrush : Hud.AccentBrush, true);
        }
        catch (Exception e) when (IsNetworkOrDataError(e))
        {
            card.SetStarStrings("OFFLINE", "Could not reach GitHub", Hud.WarnBrush, false);
        }
    }

    private async Task CheckGameRunningAsync()
    {
        var running = await Task.Run(_platform.GetRunningGameProcesses);
        var isRunning = running.Count > 0;
        if (isRunning == IsGameRunning) return;
        IsGameRunning = isRunning;
        if (!IsBusy) UpdateIdleStatus();
    }

    private void UpdateIdleStatus()
    {
        if (IsGameRunning) SetStatus("GAME RUNNING · CLOSE IT TO CONTINUE", Hud.WarnBrush);
        else if (_config.GameBase is null) SetStatus("SETUP REQUIRED", Hud.WarnBrush);
        else if (!HasEnvironments) SetStatus("NO GAME INSTALLS FOUND · CHECK SETTINGS", Hud.BadBrush);
        else if (Environments.Any(e => e.NeedsAttention)) SetStatus("PREFLIGHT RECOMMENDED", Hud.WarnBrush);
        else SetStatus("ALL SYSTEMS NOMINAL", Hud.GoodBrush);
    }

    private void SetStatus(string text, IBrush brush)
    {
        StatusText = text;
        StatusBrush = brush;
    }

    private void OnCacheSelectionChanged(CacheItemViewModel item)
    {
        _cacheSelections[item.Item.Id] = item.IsSelected;
        UpdateCacheTotal();
    }

    private void UpdateCacheTotal()
    {
        var selected = CacheItems.Where(c => c.IsSelected).Sum(c => c.Item.SizeBytes);
        CacheTotalText = $"SELECTED  {CacheCleaner.FormatSize(selected)}";
    }

    // ---------------------------------------------------------------- overlays & settings

    private Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var result = new TaskCompletionSource<bool>();
        Overlay = new ConfirmViewModel(title, message, confirmText, answer =>
        {
            Overlay = null;
            result.TrySetResult(answer);
        });
        return result.Task;
    }

    private void OpenSetup(bool isFirstRun) =>
        Overlay = new SetupViewModel(_config, _store, _platform, _ui, isFirstRun, OnSetupClosed);

    private async void OnSetupClosed(PreflightConfig? saved)
    {
        Overlay = null;
        if (saved is null) return;
        _config = saved;
        SaveConfig();
        _log.Info($"Settings saved to '{_store.FilePath}'.");
        try
        {
            await RefreshAsync();
            if (_config.CheckForUpdates && !IsUpdateAvailable) await CheckForAppUpdateAsync();
        }
        catch (Exception e)
        {
            _log.Error($"Refresh failed: {e.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            _store.Save(_config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not save settings: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private void OnLogEntry(LogEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        LogEntries.Add(new LogEntryViewModel(entry));
        if (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(0);
    });

    private static bool IsNetworkOrDataError(Exception e) =>
        e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or KeyNotFoundException;

    /// <summary>"SC LIVE Build (release-2026-10-09-292e6be)" -> "release-2026-10-09-292e6be"</summary>
    private static string ShortReleaseName(string name)
    {
        var match = Regex.Match(name, @"\(([^)]+)\)");
        return match.Success ? match.Groups[1].Value : name;
    }

    public void Dispose()
    {
        _gameTimer.Stop();
        _log.EntryWritten -= OnLogEntry;
        _http.Dispose();
    }
}

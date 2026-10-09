using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VersePreflight.App.Services;
using VersePreflight.App.Theme;
using VersePreflight.Core;
using VersePreflight.Core.Config;
using VersePreflight.Core.Game;
using VersePreflight.Core.IO;
using VersePreflight.Core.Links;
using VersePreflight.Core.Platforms;

namespace VersePreflight.App.ViewModels;

public sealed partial class LinkToggleViewModel(LinkDefinition definition) : ViewModelBase
{
    public LinkDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public string SourceSubPath => Definition.SourceSubPath.Replace('\\', '/');

    [ObservableProperty] public partial bool Enabled { get; set; } = definition.Enabled;
}

/// <summary>First-run setup and the Settings screen.</summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly PreflightConfig _current;
    private readonly ConfigStore _store;
    private readonly IUiServices _ui;
    private readonly Action<PreflightConfig?> _close;
    private readonly bool _isPortable;

    public SetupViewModel(PreflightConfig current, ConfigStore store, IPlatform platform, IUiServices ui,
        bool isFirstRun, Action<PreflightConfig?> close)
    {
        _current = current;
        _store = store;
        _ui = ui;
        _close = close;
        _isPortable = PathUtil.IsSamePath(store.ConfigDirectory, AppContext.BaseDirectory);
        IsFirstRun = isFirstRun;

        GameBaseCandidates = platform.FindGameBaseCandidates();
        GameBase = current.GameBase ?? GameBaseCandidates.FirstOrDefault() ?? "";
        LinkRoot = current.LinkRoot ?? (_isPortable
            ? ".."
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppInfo.Id));
        StarStringsEnabled = current.StarStringsEnabled;
        StarStringsIncludePtu = current.StarStringsIncludePtu;
        Links = new(current.Links.Select(l => new LinkToggleViewModel(new LinkDefinition
        {
            Name = l.Name, SourceSubPath = l.SourceSubPath, TargetName = l.TargetName, Enabled = l.Enabled,
        })));
    }

    public bool IsFirstRun { get; }
    public bool CanCancel => !IsFirstRun;
    public string Title => IsFirstRun ? "// FIRST-TIME SETUP" : "// SETTINGS";
    public string Disclaimer => AppInfo.Disclaimer;
    public string TrademarkNotice => AppInfo.TrademarkNotice;
    public string StarStringsCredit => AppInfo.StarStringsCredit;
    public IReadOnlyList<string> GameBaseCandidates { get; }
    public ObservableCollection<LinkToggleViewModel> Links { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameBaseHint), nameof(GameBaseHintBrush), nameof(CanSave))]
    public partial string GameBase { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkRootHint))]
    public partial string LinkRoot { get; set; }

    [ObservableProperty] public partial bool StarStringsEnabled { get; set; }
    [ObservableProperty] public partial bool StarStringsIncludePtu { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool DisclaimerAccepted { get; set; }

    public bool CanSave => GameBaseIsValid && (!IsFirstRun || DisclaimerAccepted);

    private bool GameBaseIsValid => !string.IsNullOrWhiteSpace(GameBase) && Directory.Exists(GameBase);

    public string GameBaseHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(GameBase)) return "Select the folder that contains LIVE / PTU.";
            if (!Directory.Exists(GameBase)) return "Folder not found.";
            var envs = EnvironmentDetector.Detect(GameBase);
            return envs.Count == 0
                ? "No game installs found here. Choose the folder that contains LIVE / PTU."
                : $"Found: {string.Join(", ", envs.Select(e => e.Name))}";
        }
    }

    public IBrush GameBaseHintBrush =>
        GameBaseIsValid && EnvironmentDetector.Detect(GameBase).Count > 0 ? Hud.GoodBrush : Hud.WarnBrush;

    public string LinkRootHint => string.IsNullOrWhiteSpace(LinkRoot)
        ? "Folder links are off until a folder is chosen."
        : $"→ {PathUtil.ResolveRelativeTo(LinkRoot, _store.ConfigDirectory)}";

    [RelayCommand]
    private async Task BrowseGameBaseAsync()
    {
        var picked = await _ui.PickFolderAsync("Select the game folder (contains LIVE / PTU)", GameBaseIsValid ? GameBase : null);
        if (picked is not null) GameBase = picked;
    }

    [RelayCommand]
    private async Task BrowseLinkRootAsync()
    {
        var start = string.IsNullOrWhiteSpace(LinkRoot) ? null : PathUtil.ResolveRelativeTo(LinkRoot, _store.ConfigDirectory);
        var picked = await _ui.PickFolderAsync("Select the folder to keep your linked files in", start);
        if (picked is null) return;

        // In portable mode keep paths relative, so the same config works wherever the synced folder lives
        LinkRoot = _isPortable && string.Equals(Path.GetPathRoot(picked), Path.GetPathRoot(_store.ConfigDirectory), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(_store.ConfigDirectory, picked)
            : picked;
    }

    [RelayCommand] private Task OpenStarStringsAsync() => _ui.OpenUrlAsync(AppInfo.StarStringsUrl);

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        _close(new PreflightConfig
        {
            SchemaVersion = _current.SchemaVersion,
            GameBase = GameBase.Trim(),
            LinkRoot = string.IsNullOrWhiteSpace(LinkRoot) ? null : LinkRoot.Trim(),
            Links = Links.Select(l => new LinkDefinition
            {
                Name = l.Definition.Name, SourceSubPath = l.Definition.SourceSubPath,
                TargetName = l.Definition.TargetName, Enabled = l.Enabled,
            }).ToList(),
            StarStringsEnabled = StarStringsEnabled,
            StarStringsIncludePtu = StarStringsIncludePtu,
            StarStringsInstalled = new(_current.StarStringsInstalled, StringComparer.OrdinalIgnoreCase),
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        if (CanCancel) _close(null);
    }
}

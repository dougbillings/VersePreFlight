using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VersePreflight.App.Theme;
using VersePreflight.Core.Cache;
using VersePreflight.Core.Game;
using VersePreflight.Core.Links;
using VersePreflight.Core.Logging;

namespace VersePreflight.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>One game install (LIVE, PTU, ...) shown as a card.</summary>
public sealed partial class EnvironmentViewModel(GameEnvironment environment, IReadOnlyList<LinkStatusViewModel> links) : ViewModelBase
{
    public GameEnvironment Environment { get; } = environment;
    public string Name => Environment.Name;
    public string BuildText => Environment.Build is { } b ? $"BUILD {b.Version} · CL {b.ChangeNumber}" : "BUILD UNKNOWN";
    public IReadOnlyList<LinkStatusViewModel> Links { get; } = links;

    [ObservableProperty] public partial string StarStringsState { get; set; } = "OFF";
    [ObservableProperty] public partial string StarStringsDetail { get; set; } = "";
    [ObservableProperty] public partial IBrush StarStringsBrush { get; set; } = Hud.TextDimBrush;
    public bool StarStringsNeedsAttention { get; private set; }

    public bool NeedsAttention => StarStringsNeedsAttention || Links.Any(l => l.NeedsAttention);

    public void SetStarStrings(string state, string detail, IBrush brush, bool needsAttention)
    {
        StarStringsState = state;
        StarStringsDetail = detail;
        StarStringsBrush = brush;
        StarStringsNeedsAttention = needsAttention;
    }
}

/// <summary>The state of one folder link within an install card.</summary>
public sealed record LinkStatusViewModel(string Name, string StateText, string Detail, IBrush Brush, bool NeedsAttention)
{
    public static LinkStatusViewModel From(LinkDefinition definition, LinkPlan? plan)
    {
        if (plan is null)
            return new(definition.Name, "NO FOLDER", "Choose a link folder in Settings", Hud.WarnBrush, true);

        return plan.State switch
        {
            LinkState.Linked => new(definition.Name, "LINKED", plan.TargetPath, Hud.GoodBrush, false),
            LinkState.WrongTarget => new(definition.Name, "RELINK", $"Points to {plan.CurrentTarget}", Hud.WarnBrush, true),
            _ => new(definition.Name, "NOT LINKED", DescribePending(plan),
                plan.Conflicts.Count > 0 ? Hud.WarnBrush : Hud.AccentBrush, true),
        };
    }

    public static LinkStatusViewModel Failed(LinkDefinition definition, string message) =>
        new(definition.Name, "ERROR", message, Hud.BadBrush, true);

    private static string DescribePending(LinkPlan plan)
    {
        var total = plan.FilesToMove.Count + plan.IdenticalFiles.Count + plan.Conflicts.Count;
        if (total == 0) return $"Will link to {plan.TargetPath}";
        var parts = new List<string>();
        if (plan.FilesToMove.Count > 0) parts.Add($"{plan.FilesToMove.Count} to move");
        if (plan.IdenticalFiles.Count > 0) parts.Add($"{plan.IdenticalFiles.Count} duplicate");
        if (plan.Conflicts.Count > 0) parts.Add($"{plan.Conflicts.Count} to backup");
        return string.Join(" · ", parts);
    }
}

/// <summary>A cleanable cache item with a checkbox.</summary>
public sealed partial class CacheItemViewModel(CacheItem item, bool isSelected, Action<CacheItemViewModel> selectionChanged) : ViewModelBase
{
    public CacheItem Item { get; } = item;
    public string Name => Item.Name;
    public string Description => Item.Description;
    public string Scope => Item.EnvironmentName ?? "ALL";
    public string SizeText => CacheCleaner.FormatSize(Item.SizeBytes);

    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    partial void OnIsSelectedChanged(bool value) => selectionChanged(this);
}

public sealed record LogEntryViewModel(LogEntry Entry)
{
    public string TimeText => Entry.Time.ToString("HH:mm:ss");
    public string Message => Entry.Message;

    public IBrush Brush => Entry.Level switch
    {
        PreflightLogLevel.Success => Hud.GoodBrush,
        PreflightLogLevel.Warning => Hud.WarnBrush,
        PreflightLogLevel.Error => Hud.BadBrush,
        _ => Hud.TextBrush,
    };

    public string FullText => $"[{Entry.Time:yyyy-MM-dd HH:mm:ss}] [{Entry.Level.ToString().ToUpperInvariant()}] {Entry.Message}";
}

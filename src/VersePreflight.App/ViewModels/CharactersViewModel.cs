using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VersePreflight.App.Rendering;
using VersePreflight.App.Services;
using VersePreflight.Core.Characters;
using VersePreflight.Core.Game;
using VersePreflight.Core.IO;
using VersePreflight.Core.Logging;

namespace VersePreflight.App.ViewModels;

public sealed record CharacterItemViewModel(string Name, string Path, DateTime Modified, string Location)
{
    public string Details => $"{Modified:yyyy-MM-dd}  ·  {Location}";
}

/// <summary>
/// Experimental character viewer: lists the player's custom characters and rebuilds the selected
/// one's head shape from their own game files.
/// </summary>
public sealed partial class CharactersViewModel(PreflightLog log, IUiServices ui, string cacheDirectory) : ViewModelBase, IDisposable
{
    private readonly HeadLibraryCache _cache = new(cacheDirectory);
    private Task<List<HeadLibrary>>? _librariesTask;
    private Task<AccessoryLibrary?>? _accessoriesTask;
    private string? _librariesKey;
    private IReadOnlyList<GameEnvironment> _environments = [];
    private int _loadVersion;

    public ObservableCollection<CharacterItemViewModel> Characters { get; } = [];

    [ObservableProperty] public partial CharacterItemViewModel? Selected { get; set; }
    [ObservableProperty] public partial IReadOnlyList<MeshPart>? Parts { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "Select a character.";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial bool HasCharacters { get; set; }

    public bool HasModel => Parts is not null;

    partial void OnPartsChanged(IReadOnlyList<MeshPart>? value) => OnPropertyChanged(nameof(HasModel));

    /// <summary>Lists .chf files from the link folder and every install's CustomCharacters folder.</summary>
    public void Refresh(IReadOnlyList<GameEnvironment> environments, string? linkRoot)
    {
        _environments = environments;
        var folders = new List<(string Folder, string Label)>();
        if (linkRoot is not null) folders.Add((System.IO.Path.Combine(linkRoot, "CustomCharacters"), "Link folder"));
        foreach (var env in environments)
            folders.Add((PathUtil.ResolveCaseInsensitive(env.Path, "user/client/0/CustomCharacters"), env.Name));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CharacterItemViewModel>();
        foreach (var (folder, label) in folders)
        {
            if (!Directory.Exists(folder)) continue;
            var real = DirectoryLinks.GetTarget(folder) ?? folder; // linked install folders point at the same files
            if (!seen.Add(PathUtil.Normalize(real))) continue;
            items.AddRange(Directory.GetFiles(folder, "*.chf").Select(f =>
                new CharacterItemViewModel(System.IO.Path.GetFileNameWithoutExtension(f), f, File.GetLastWriteTime(f), label)));
        }

        var sorted = items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (sorted.SequenceEqual(Characters)) return; // nothing changed: keep the list and selection as they are

        var previous = Selected?.Path;
        _updatingList = true;
        try
        {
            Characters.Clear();
            foreach (var item in sorted) Characters.Add(item);
        }
        finally
        {
            _updatingList = false;
        }
        HasCharacters = Characters.Count > 0;
        if (!HasCharacters) Message = "No custom characters found. Save one in the game's character customizer first.";
        Selected = Characters.FirstOrDefault(c => c.Path == previous) ?? Characters.FirstOrDefault();
    }

    private bool _updatingList;

    partial void OnSelectedChanged(CharacterItemViewModel? value)
    {
        // The list control resets its selection while items are replaced; ignore that churn
        if (_updatingList) return;
        if (value is not null) _ = LoadAsync(value);
    }

    private async Task LoadAsync(CharacterItemViewModel item)
    {
        var version = ++_loadVersion;
        Title = item.Name.ToUpperInvariant();
        Subtitle = "";
        IsLoading = true;
        try
        {
            var character = await Task.Run(() => CharacterFile.Read(item.Path));
            var libraries = await GetLibrariesAsync();
            if (version != _loadVersion) return; // another character was selected meanwhile

            var library = libraries?.FirstOrDefault(l => l.BodyHash == character.BodyHash);
            if (library is null)
            {
                Parts = null;
                Message = libraries is null
                    ? "No game install with a Data.p4k was found. Check the game folder in Settings."
                    : "This character's body type isn't supported yet.";
                return;
            }

            var accessories = await (_accessoriesTask ?? Task.FromResult<AccessoryLibrary?>(null));
            var parts = await Task.Run(() => BuildParts(library, character, accessories));
            if (version != _loadVersion) return;
            Parts = parts;
            Subtitle = $"{(library.IsFemale ? "FEMALE" : "MALE")}  ·  SKIN {character.SkinColor?.ToString() ?? "?"}  ·  EYES {character.EyeColor?.ToString() ?? "?"}";
            Message = "";
        }
        catch (Exception e)
        {
            // This runs fire-and-forget from a selection change, so every failure must be shown, not lost
            if (version != _loadVersion) return;
            Parts = null;
            Message = $"Couldn't show this character: {e.Message}";
            log.Warning($"Character viewer: {item.Name}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>The head with the character's skin, the eyes, and the equipped hair, beard and eyebrows.</summary>
    private List<MeshPart> BuildParts(HeadLibrary library, CharacterFile character, AccessoryLibrary? accessories) =>
        CharacterScene.Build(library, character, accessories, warn: message => log.Warning($"Character viewer: {message}"));

    /// <summary>Head data comes from the first install with a Data.p4k (LIVE preferred), cached per game build.</summary>
    private async Task<List<HeadLibrary>?> GetLibrariesAsync()
    {
        var env = _environments.OrderBy(e => e.Name.Equals("LIVE", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault(e => File.Exists(System.IO.Path.Combine(e.Path, "Data.p4k")));
        if (env is null) return null;

        var key = $"{env.Name}-{env.Build?.ChangeNumber ?? File.GetLastWriteTimeUtc(System.IO.Path.Combine(env.Path, "Data.p4k")).Ticks.ToString()}";
        if (_librariesTask is null || _librariesKey != key)
        {
            // One shared load, even if several characters are selected while it runs
            Message = "Reading head data from your game files (first time after each patch)...";
            var p4k = System.IO.Path.Combine(env.Path, "Data.p4k");
            _librariesKey = key;
            _librariesTask = Task.Run(() => _cache.GetOrBuild(p4k, key));
            var old = _accessoriesTask;
            _accessoriesTask = Task.Run(() => OpenAccessories(p4k, key));
            if (old is not null) (await old)?.Dispose();
            var libraries = await _librariesTask;
            log.Info($"Character viewer: head data ready ({string.Join(", ", libraries.Select(l => l.IsFemale ? "female" : "male"))}).");
        }
        try
        {
            return await _librariesTask;
        }
        catch
        {
            _librariesTask = null; // allow a retry next time
            throw;
        }
    }

    /// <summary>Hair, beard and eyebrows are optional: a failure here never stops the head from showing.</summary>
    private AccessoryLibrary? OpenAccessories(string p4k, string key)
    {
        try
        {
            var library = AccessoryLibrary.Open(p4k, cacheDirectory, key);
            log.Info($"Character viewer: hair and facial hair ready ({library.ItemCount} items).");
            return library;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            log.Warning($"Character viewer: hair isn't available: {e.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_accessoriesTask is { IsCompletedSuccessfully: true } t) t.Result?.Dispose();
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (Selected is { } s && System.IO.Path.GetDirectoryName(s.Path) is { } dir) await ui.OpenPathAsync(dir);
    }
}

namespace VersePreflight.App.Services;

/// <summary>Window-level services the view models need, implemented by the main window.</summary>
public interface IUiServices
{
    Task<string?> PickFolderAsync(string title, string? startPath);
    Task OpenUrlAsync(string url);
    Task OpenPathAsync(string path);
    Task CopyTextAsync(string text);
}

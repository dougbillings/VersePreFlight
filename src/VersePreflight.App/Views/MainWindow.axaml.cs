using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VersePreflight.App.Services;
using VersePreflight.App.ViewModels;

namespace VersePreflight.App.Views;

public partial class MainWindow : Window, IUiServices
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.LogEntries.CollectionChanged += OnLogChanged;
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);

    public async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startPath is not null && Directory.Exists(startPath))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startPath);

        var result = await StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task OpenUrlAsync(string url) => await Launcher.LaunchUriAsync(new Uri(url));

    public async Task OpenPathAsync(string path) => await Launcher.LaunchFileInfoAsync(new FileInfo(path));

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}

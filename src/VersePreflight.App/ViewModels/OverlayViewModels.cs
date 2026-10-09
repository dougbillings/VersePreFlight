using CommunityToolkit.Mvvm.Input;
using VersePreflight.App.Services;
using VersePreflight.Core;

namespace VersePreflight.App.ViewModels;

/// <summary>A yes/no question shown over the main window.</summary>
public sealed partial class ConfirmViewModel(string title, string message, string confirmText, Action<bool> done) : ViewModelBase
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;

    [RelayCommand] private void Confirm() => done(true);
    [RelayCommand] private void Cancel() => done(false);
}

/// <summary>Credits, notices and links.</summary>
public sealed partial class AboutViewModel(IUiServices ui, Action close) : ViewModelBase
{
    public string Name => AppInfo.Name.ToUpperInvariant();
    public string Version => $"Version {AppInfo.Version} · Free and open source (MIT License)";
    public string Disclaimer => AppInfo.Disclaimer;
    public string TrademarkNotice => AppInfo.TrademarkNotice;
    public string StarStringsCredit => AppInfo.StarStringsCredit;

    [RelayCommand] private Task OpenOfficialSiteAsync() => ui.OpenUrlAsync(AppInfo.OfficialSiteUrl);
    [RelayCommand] private Task OpenRepositoryAsync() => ui.OpenUrlAsync(AppInfo.RepositoryUrl);
    [RelayCommand] private Task OpenStarStringsAsync() => ui.OpenUrlAsync(AppInfo.StarStringsUrl);
    [RelayCommand] private void Close() => close();
}

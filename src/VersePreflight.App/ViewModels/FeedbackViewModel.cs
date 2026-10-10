using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VersePreflight.App.Services;
using VersePreflight.Core;
using VersePreflight.Core.Feedback;

namespace VersePreflight.App.ViewModels;

/// <summary>
/// Bug report / idea / feedback form. Builds a pre-filled GitHub issue link or text to paste;
/// nothing is sent by the app itself.
/// </summary>
public sealed partial class FeedbackViewModel(
    string diagnosticsSummary,
    IReadOnlyList<string> logLines,
    IUiServices ui,
    Action<string> reportSent,
    Action close) : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBug), nameof(IsFeature), nameof(IsFeedback), nameof(DetailsHint))]
    public partial FeedbackKind Kind { get; set; } = FeedbackKind.Bug;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string Details { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiagnosticsPreview))]
    public partial bool IncludeDiagnostics { get; set; } = true;

    [ObservableProperty] public partial string Message { get; set; } = "";

    public bool IsBug { get => Kind == FeedbackKind.Bug; set { if (value) Kind = FeedbackKind.Bug; } }
    public bool IsFeature { get => Kind == FeedbackKind.Feature; set { if (value) Kind = FeedbackKind.Feature; } }
    public bool IsFeedback { get => Kind == FeedbackKind.Feedback; set { if (value) Kind = FeedbackKind.Feedback; } }

    public string DetailsHint => Kind switch
    {
        FeedbackKind.Bug => "What did you do, what did you expect, and what happened instead?",
        FeedbackKind.Feature => "What would you like Verse Preflight to do, and what problem would it solve?",
        _ => "What do you like, what's confusing, what could be better?",
    };

    public bool CanSend => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Details);

    public string DiagnosticsPreview => IncludeDiagnostics
        ? Build().FormatDiagnostics(logLines)
        : "Diagnostics will not be included.";

    private FeedbackReport Build() =>
        new(Kind, Title, Details, IncludeDiagnostics ? diagnosticsSummary : null, IncludeDiagnostics ? logLines : []);

    [RelayCommand]
    private async Task OpenOnGitHubAsync()
    {
        if (!CanSend) return;
        await ui.OpenUrlAsync(Build().ToIssueUrl(AppInfo.RepositoryUrl).AbsoluteUri);
        reportSent("Feedback opened in your browser. Review it there and press Submit to send it.");
        close();
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (!CanSend) return;
        await ui.CopyTextAsync(Build().ToText());
        Message = "Report copied. Paste it wherever you'd like to share it.";
    }

    [RelayCommand] private void Cancel() => close();
}

using System.Text;
using System.Text.RegularExpressions;

namespace VersePreflight.Core.Feedback;

public enum FeedbackKind { Bug, Feature, Feedback }

/// <summary>
/// A user-written report, turned into a pre-filled GitHub issue link or plain text to paste elsewhere.
/// Verse Preflight never sends it anywhere itself: the user reviews and submits it in their browser.
/// </summary>
public sealed record FeedbackReport(
    FeedbackKind Kind,
    string Title,
    string Description,
    string? DiagnosticsSummary,
    IReadOnlyList<string> LogLines)
{
    /// <summary>Stay well under the URL length GitHub and browsers accept.</summary>
    public const int MaxUrlLength = 7000;

    /// <summary>Issue form in .github/ISSUE_TEMPLATE; its field ids match the query parameters below.</summary>
    public string TemplateFile => Kind switch
    {
        FeedbackKind.Bug => "bug_report.yml",
        FeedbackKind.Feature => "feature_request.yml",
        _ => "feedback.yml",
    };

    public string KindLabel => Kind switch
    {
        FeedbackKind.Bug => "Bug",
        FeedbackKind.Feature => "Idea",
        _ => "Feedback",
    };

    /// <summary>
    /// A "new issue" link with the form pre-filled. If the result would be too long, the oldest
    /// log lines are dropped first.
    /// </summary>
    public Uri ToIssueUrl(string repositoryUrl)
    {
        var lines = LogLines.ToList();
        while (true)
        {
            var url = BuildUrl(repositoryUrl, lines);
            if (url.Length <= MaxUrlLength || lines.Count == 0) return new Uri(url);
            lines.RemoveAt(0);
        }
    }

    /// <summary>The full report as plain text, for pasting into a forum post or chat.</summary>
    public string ToText()
    {
        var text = new StringBuilder()
            .AppendLine($"[{KindLabel}] {Title.Trim()}")
            .AppendLine()
            .AppendLine(Description.Trim());
        var diagnostics = FormatDiagnostics(LogLines);
        if (diagnostics.Length > 0)
            text.AppendLine().AppendLine("--- Diagnostics ---").AppendLine(diagnostics);
        return text.ToString().TrimEnd();
    }

    public string FormatDiagnostics(IReadOnlyList<string> logLines)
    {
        if (DiagnosticsSummary is null) return "";
        var text = new StringBuilder(DiagnosticsSummary.TrimEnd());
        if (logLines.Count > 0)
        {
            text.AppendLine().AppendLine().AppendLine("Recent log:");
            foreach (var line in logLines) text.AppendLine(line);
        }
        return text.ToString().TrimEnd();
    }

    private string BuildUrl(string repositoryUrl, IReadOnlyList<string> logLines)
    {
        var query = new List<(string Key, string Value)>
        {
            ("template", TemplateFile),
            ("title", $"[{KindLabel}] {Title.Trim()}"),
            ("description", Description.Trim()),
            ("version", AppInfo.Version),
        };
        var diagnostics = FormatDiagnostics(logLines);
        if (diagnostics.Length > 0) query.Add(("diagnostics", diagnostics));

        return $"{repositoryUrl.TrimEnd('/')}/issues/new?" +
               string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
    }
}

/// <summary>Builds the diagnostics block and removes personal details from it.</summary>
public static partial class FeedbackDiagnostics
{
    /// <summary>
    /// Hides the Windows user name (in profile paths and on its own) and the PC name.
    /// Run on everything that goes into a report.
    /// </summary>
    public static string Redact(string text, string? userProfile, string? userName, string? machineName)
    {
        if (!string.IsNullOrEmpty(userProfile))
            text = text.Replace(userProfile.TrimEnd('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        // Any other profile path, e.g. on another drive or with forward slashes
        text = ProfilePath().Replace(text, "${prefix}<user>");

        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
            text = Regex.Replace(text, $@"\b{Regex.Escape(userName)}\b", "<user>", RegexOptions.IgnoreCase);
        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= 3)
            text = Regex.Replace(text, $@"\b{Regex.Escape(machineName)}\b", "<pc>", RegexOptions.IgnoreCase);
        return text;
    }

    public static string Redact(string text) =>
        Redact(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    [GeneratedRegex(@"(?<prefix>[A-Za-z]:[\\/]Users[\\/]|/home/)(?!<user>)[^\\/\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex ProfilePath();
}

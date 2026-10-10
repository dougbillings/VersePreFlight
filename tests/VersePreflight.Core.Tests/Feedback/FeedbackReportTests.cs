using VersePreflight.Core.Feedback;

namespace VersePreflight.Core.Tests.Feedback;

public class FeedbackReportTests
{
    private const string Repo = "https://github.com/example/VersePreFlight";

    [Fact]
    public void Redact_HidesProfilePathUserNameAndPcName()
    {
        var text = @"Linked to 'C:\Users\Jane.Doe\Nextcloud\Star Citizen\Mappings' on JANES-PC; D:\Users\Other\x; user jane.doe";

        var redacted = FeedbackDiagnostics.Redact(text, @"C:\Users\Jane.Doe", "jane.doe", "JANES-PC");

        Assert.Equal(@"Linked to '%USERPROFILE%\Nextcloud\Star Citizen\Mappings' on <pc>; D:\Users\<user>\x; user <user>", redacted);
    }

    [Fact]
    public void Redact_IgnoresVeryShortNamesToAvoidMangling()
    {
        // A two-letter user name would match all over the place; only paths are redacted then
        var redacted = FeedbackDiagnostics.Redact(@"G:\Games ok C:\Users\al\x", null, "al", "PC");

        Assert.Equal(@"G:\Games ok C:\Users\<user>\x", redacted);
    }

    [Fact]
    public void ToIssueUrl_PrefillsTemplateFields()
    {
        var report = new FeedbackReport(FeedbackKind.Bug, "Links fail", "It broke & stopped", "Version: 1", ["a", "b"]);

        var url = report.ToIssueUrl(Repo).AbsoluteUri;

        Assert.StartsWith($"{Repo}/issues/new?template=bug_report.yml&title=%5BBug%5D%20Links%20fail", url);
        Assert.Contains("&description=It%20broke%20%26%20stopped", url);
        Assert.Contains("&diagnostics=Version%3A%201", url);
        Assert.Contains("Recent%20log%3A", url);
    }

    [Fact]
    public void ToIssueUrl_OmitsDiagnosticsWhenNotIncluded()
    {
        var report = new FeedbackReport(FeedbackKind.Feature, "Idea", "Text", null, ["log"]);

        var url = report.ToIssueUrl(Repo).AbsoluteUri;

        Assert.Contains("template=feature_request.yml", url);
        Assert.DoesNotContain("diagnostics=", url);
    }

    [Fact]
    public void ToIssueUrl_DropsOldestLogLinesToFitLengthLimit()
    {
        var lines = Enumerable.Range(1, 400).Select(i => $"line {i:000} {new string('x', 40)}").ToList();
        var report = new FeedbackReport(FeedbackKind.Bug, "T", "D", "Summary", lines);

        var url = report.ToIssueUrl(Repo).AbsoluteUri;

        Assert.True(url.Length <= FeedbackReport.MaxUrlLength, $"URL length {url.Length}");
        Assert.Contains("line%20400", url);       // newest kept
        Assert.DoesNotContain("line%20001", url); // oldest dropped
    }

    [Fact]
    public void ToText_IncludesEverythingForPasting()
    {
        var report = new FeedbackReport(FeedbackKind.Feedback, "Nice", "Thanks!", "Version: 1", ["entry"]);

        var text = report.ToText();

        Assert.StartsWith("[Feedback] Nice", text);
        Assert.Contains("Thanks!", text);
        Assert.Contains("--- Diagnostics ---", text);
        Assert.EndsWith("entry", text);
    }
}

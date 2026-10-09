namespace VersePreflight.Core;

/// <summary>
/// Product identity, credits and the fan-project notices required by the
/// Star Citizen Fankit and Fandom FAQ. Every UI surface (main window footer,
/// About, first run) reads from here so the wording stays consistent.
/// </summary>
public static class AppInfo
{
    public const string Name = "Verse Preflight";

    /// <summary>Folder/file-name friendly identifier.</summary>
    public const string Id = "VersePreflight";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Short notice for the always-visible main window footer.</summary>
    public const string ShortDisclaimer =
        "Unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of companies.";

    /// <summary>Full notice for the About screen, first-run screen and README.</summary>
    public const string Disclaimer =
        "Verse Preflight is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium " +
        "group of companies. All content not authored by its developers or users is property of their " +
        "respective owners.";

    // Verify against the trademark notice in the official Fan Kit before each public release.
    public const string TrademarkNotice =
        "Star Citizen®, Squadron 42®, Roberts Space Industries® and Cloud Imperium® are registered " +
        "trademarks of Cloud Imperium Rights LLC.";

    public const string OfficialSiteUrl = "https://robertsspaceindustries.com";

    public const string RepositoryUrl = "https://github.com/dougbillings/VersePreFlight";

    public const string StarStringsCredit =
        "StarStrings is a community-made (unofficial) string replacement pack by MrKraken. " +
        "Verse Preflight downloads it directly from the author's GitHub releases.";

    public const string StarStringsUrl = "https://github.com/MrKraken/StarStrings";
}

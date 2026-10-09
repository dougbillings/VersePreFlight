using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VersePreflight.Core.StarStrings;

/// <summary>A StarStrings release channel, mapped from the game channel folder name.</summary>
public sealed record StarStringsChannel(string EnvironmentName, string Tag, string AssetName, bool IsTestBuild)
{
    public static IReadOnlyList<StarStringsChannel> All { get; } =
    [
        new("LIVE", "latest", "StarStrings-LIVE.zip", IsTestBuild: false),
        new("PTU", "latest-ptu", "StarStrings-PTU.zip", IsTestBuild: true),
    ];

    public static StarStringsChannel? For(string environmentName) =>
        All.FirstOrDefault(c => string.Equals(c.EnvironmentName, environmentName, StringComparison.OrdinalIgnoreCase));
}

public sealed record StarStringsRelease(string Name, DateTimeOffset PublishedAt, string? Commit, Uri AssetUrl, long AssetSize);

/// <summary>
/// Reads MrKraken's StarStrings releases from GitHub. The file is always downloaded from the
/// author's own release at run time; Verse Preflight never bundles or mirrors it.
/// </summary>
public sealed partial class StarStringsClient(HttpClient http)
{
    public const string Repository = "MrKraken/StarStrings";

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Id, AppInfo.Version));
        return http;
    }

    public async Task<StarStringsRelease> GetLatestReleaseAsync(StarStringsChannel channel, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/tags/{channel.Tag}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == channel.AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"Release '{channel.Tag}' has no {channel.AssetName}.");

        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var commitMatch = CommitInBody().Match(body);
        var commit = commitMatch.Success
            ? commitMatch.Groups[1].Value
            : root.TryGetProperty("target_commitish", out var t) ? t.GetString() : null;

        return new StarStringsRelease(
            root.GetProperty("name").GetString() ?? channel.Tag,
            root.GetProperty("published_at").GetDateTimeOffset(),
            commit,
            new Uri(asset.GetProperty("browser_download_url").GetString()!),
            asset.GetProperty("size").GetInt64());
    }

    /// <summary>
    /// The game build the release targets, from its README ("[Build: sc-alpha-4.10.2_live_12881860]").
    /// Null if it can't be determined.
    /// </summary>
    public async Task<string?> GetTargetBuildAsync(StarStringsRelease release, CancellationToken ct = default)
    {
        if (release.Commit is null) return null;

        // The readme endpoint finds the file regardless of its exact name/case
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/readme?ref={release.Commit}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var match = BuildInReadme().Match(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Downloads the release zip and returns the bytes of Localization/english/global.ini.</summary>
    public async Task<byte[]> DownloadGlobalIniAsync(StarStringsRelease release, CancellationToken ct = default)
    {
        var zipBytes = await http.GetByteArrayAsync(release.AssetUrl, ct).ConfigureAwait(false);
        return ExtractGlobalIni(zipBytes);
    }

    internal static byte[] ExtractGlobalIni(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.Replace('\\', '/').EndsWith("Localization/english/global.ini", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The download does not contain Localization/english/global.ini.");

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [GeneratedRegex(@"commit ([0-9a-f]{7,40})")]
    private static partial Regex CommitInBody();

    [GeneratedRegex(@"Build:\s*([^\]\s]+)")]
    private static partial Regex BuildInReadme();
}

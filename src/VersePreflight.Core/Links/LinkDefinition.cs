namespace VersePreflight.Core.Links;

/// <summary>A game folder that is replaced by a link into the user's link root.</summary>
public sealed class LinkDefinition
{
    public required string Name { get; set; }

    /// <summary>Path inside a game channel folder, e.g. "user/client/0/Controls/Mappings". Matched case-insensitively.</summary>
    public required string SourceSubPath { get; set; }

    /// <summary>Folder name inside the link root.</summary>
    public required string TargetName { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Why this definition is unsafe to act on, or null if it's fine. Linking moves a folder's files
    /// and deletes the folder, so the source must be a real sub-folder of the game channel (never the
    /// channel itself or anything outside it) and the target a single folder inside the link root.
    /// </summary>
    public string? GetProblem()
    {
        var segments = (SourceSubPath ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || Path.IsPathRooted(SourceSubPath) || !segments.All(IsPlainName))
            return $"'{SourceSubPath}' must be a folder inside the game channel folder.";
        if (!IsPlainName(TargetName ?? "") || string.Equals(TargetName, FolderLinker.BackupFolderName, StringComparison.OrdinalIgnoreCase))
            return $"'{TargetName}' must be a single folder name inside the link folder.";
        return null;
    }

    // A single path segment: not empty, not "." or "..", no separators or drive colons
    private static bool IsPlainName(string name) =>
        name.Trim().Length > 0 && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(['/', '\\', ':']) < 0;

    public static List<LinkDefinition> Defaults() =>
    [
        new() { Name = "Screenshots", SourceSubPath = "ScreenShots", TargetName = "ScreenShots" },
        new() { Name = "Control mappings", SourceSubPath = "user/client/0/Controls/Mappings", TargetName = "Mappings" },
        new() { Name = "Custom characters", SourceSubPath = "user/client/0/CustomCharacters", TargetName = "CustomCharacters" },
    ];
}

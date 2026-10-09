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

    public static List<LinkDefinition> Defaults() =>
    [
        new() { Name = "Screenshots", SourceSubPath = "ScreenShots", TargetName = "ScreenShots" },
        new() { Name = "Control mappings", SourceSubPath = "user/client/0/Controls/Mappings", TargetName = "Mappings" },
        new() { Name = "Custom characters", SourceSubPath = "user/client/0/CustomCharacters", TargetName = "CustomCharacters" },
    ];
}

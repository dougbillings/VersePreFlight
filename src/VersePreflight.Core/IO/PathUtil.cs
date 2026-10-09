namespace VersePreflight.Core.IO;

public static class PathUtil
{
    /// <summary>
    /// Case sensitivity of the host file system's path comparisons.
    /// Windows is case-insensitive; Linux (Phase 3) is not.
    /// </summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Joins <paramref name="relativePath"/> onto <paramref name="basePath"/>, matching each
    /// existing segment case-insensitively. The game's folders are named inconsistently
    /// ("USER" vs "user"), which matters on case-sensitive Linux file systems.
    /// Segments that don't exist yet are appended as written.
    /// </summary>
    public static string ResolveCaseInsensitive(string basePath, string relativePath)
    {
        var current = basePath;
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (!Directory.Exists(current))
            {
                current = Path.Combine(current, segment);
                continue;
            }

            // Prefer an exact match, then any case-insensitive one, so results use the on-disk casing
            var names = Directory.EnumerateFileSystemEntries(current).Select(Path.GetFileName).ToList();
            var match = names.FirstOrDefault(n => string.Equals(n, segment, StringComparison.Ordinal))
                        ?? names.FirstOrDefault(n => string.Equals(n, segment, StringComparison.OrdinalIgnoreCase));
            current = Path.Combine(current, match ?? segment);
        }

        return current;
    }

    /// <summary>Full, normalized path without a trailing separator.</summary>
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsSamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>Resolves a possibly relative path against <paramref name="baseDirectory"/>.</summary>
    public static string ResolveRelativeTo(string path, string baseDirectory) =>
        Normalize(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
}

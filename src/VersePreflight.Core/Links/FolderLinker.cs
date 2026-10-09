using VersePreflight.Core.Game;
using VersePreflight.Core.IO;
using VersePreflight.Core.Logging;

namespace VersePreflight.Core.Links;

public enum LinkState
{
    /// <summary>The game folder is a real folder (or missing) and will be linked.</summary>
    NotLinked,
    /// <summary>Already linked to the right folder; nothing to do.</summary>
    Linked,
    /// <summary>Linked, but to a different folder; the link will be replaced.</summary>
    WrongTarget,
}

/// <summary>What linking one folder would do. Used for the preview and recomputed when applied.</summary>
public sealed record LinkPlan(
    GameEnvironment Environment,
    LinkDefinition Definition,
    string SourcePath,
    string TargetPath,
    LinkState State,
    string? CurrentTarget,
    IReadOnlyList<string> FilesToMove,
    IReadOnlyList<string> IdenticalFiles,
    IReadOnlyList<string> Conflicts);

public sealed record LinkResult(bool Success, string Message, string? BackupPath = null);

/// <summary>
/// Replaces game folders with links into the link root. Files already in the link root
/// are never overwritten: new game files are moved in, identical copies are dropped, and
/// conflicting game copies are moved to a timestamped per-machine backup folder.
/// </summary>
public sealed class FolderLinker(PreflightLog log, string machineName, TimeProvider? clock = null)
{
    public const string BackupFolderName = "_VersePreflight-Backup";

    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // never descend into nested links
        IgnoreInaccessible = false,
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public LinkPlan Plan(GameEnvironment environment, LinkDefinition definition, string linkRoot)
    {
        var source = PathUtil.ResolveCaseInsensitive(environment.Path, definition.SourceSubPath);
        var target = PathUtil.ResolveCaseInsensitive(linkRoot, definition.TargetName);

        var currentTarget = DirectoryLinks.GetTarget(source);
        if (currentTarget is not null)
        {
            var state = PathUtil.IsSamePath(currentTarget, target) ? LinkState.Linked : LinkState.WrongTarget;
            return new LinkPlan(environment, definition, source, target, state, currentTarget, [], [], []);
        }

        List<string> toMove = [], identical = [], conflicts = [];
        if (Directory.Exists(source))
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", AllFiles))
            {
                var relative = Path.GetRelativePath(source, file);
                var existing = Path.Combine(target, relative);
                if (!File.Exists(existing)) toMove.Add(relative);
                else if (FileCompare.AreIdentical(file, existing)) identical.Add(relative);
                else conflicts.Add(relative);
            }
        }

        return new LinkPlan(environment, definition, source, target, LinkState.NotLinked, null, toMove, identical, conflicts);
    }

    public LinkResult Apply(GameEnvironment environment, LinkDefinition definition, string linkRoot)
    {
        var plan = Plan(environment, definition, linkRoot); // always act on the current state of the disk
        var label = $"{environment.Name} - {definition.Name}";

        if (plan.State == LinkState.Linked)
        {
            log.Info($"{label}: already linked.");
            return new LinkResult(true, "Already linked.");
        }

        try
        {
            if (plan.State == LinkState.WrongTarget)
            {
                log.Warning($"{label}: linked to '{plan.CurrentTarget}', relinking to '{plan.TargetPath}'.");
                DirectoryLinks.Remove(plan.SourcePath); // removes only the link; the old folder keeps its files
            }

            Directory.CreateDirectory(plan.TargetPath);

            string? backupRoot = null;
            if (Directory.Exists(plan.SourcePath))
            {
                foreach (var relative in plan.FilesToMove)
                {
                    var from = Path.Combine(plan.SourcePath, relative);
                    var to = Path.Combine(plan.TargetPath, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    try
                    {
                        File.Move(from, to, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(to))
                    {
                        // Appeared in the link root since planning (e.g. synced from another PC): keep both
                        backupRoot ??= CreateBackupRoot(linkRoot, environment, definition);
                        MoveToBackup(from, backupRoot, relative);
                    }
                }

                foreach (var relative in plan.IdenticalFiles)
                    File.Delete(Path.Combine(plan.SourcePath, relative));

                if (plan.Conflicts.Count > 0)
                {
                    backupRoot ??= CreateBackupRoot(linkRoot, environment, definition);
                    log.Warning($"{label}: {plan.Conflicts.Count} file(s) differ from the copies in '{plan.TargetPath}'. " +
                                $"Your existing copies were kept; the game's copies were moved to '{backupRoot}'.");
                    foreach (var relative in plan.Conflicts)
                        MoveToBackup(Path.Combine(plan.SourcePath, relative), backupRoot, relative);
                }

                if (Directory.EnumerateFiles(plan.SourcePath, "*", AllFiles).Any())
                {
                    log.Error($"{label}: could not empty '{plan.SourcePath}' (files may be in use). Link not created.");
                    return new LinkResult(false, "Game folder still contains files.", backupRoot);
                }

                Directory.Delete(plan.SourcePath, recursive: true); // only empty folders (or nested links) remain
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plan.SourcePath)!);
            }

            DirectoryLinks.Create(plan.SourcePath, plan.TargetPath);
            log.Success($"{label}: linked to '{plan.TargetPath}' ({plan.FilesToMove.Count} file(s) moved).");
            return new LinkResult(true, "Linked.", backupRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log.Error($"{label}: {e.Message}");
            return new LinkResult(false, e.Message);
        }
    }

    /// <summary>
    /// Undo: replaces the link with a real folder holding a copy of the linked files.
    /// Files are copied, not moved, because the link root may be shared with other PCs.
    /// </summary>
    public LinkResult Unlink(GameEnvironment environment, LinkDefinition definition)
    {
        var source = PathUtil.ResolveCaseInsensitive(environment.Path, definition.SourceSubPath);
        var label = $"{environment.Name} - {definition.Name}";
        var target = DirectoryLinks.GetTarget(source);
        if (target is null)
        {
            log.Info($"{label}: not linked, nothing to undo.");
            return new LinkResult(true, "Not linked.");
        }

        try
        {
            DirectoryLinks.Remove(source);
            Directory.CreateDirectory(source);
            var copied = 0;
            if (Directory.Exists(target))
            {
                foreach (var file in Directory.EnumerateFiles(target, "*", AllFiles))
                {
                    var to = Path.Combine(source, Path.GetRelativePath(target, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to);
                    copied++;
                }
            }
            log.Success($"{label}: link removed, {copied} file(s) copied back into the game folder.");
            return new LinkResult(true, "Unlinked.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Error($"{label}: {e.Message}");
            return new LinkResult(false, e.Message);
        }
    }

    private string CreateBackupRoot(string linkRoot, GameEnvironment environment, LinkDefinition definition)
    {
        var stamp = _clock.GetLocalNow().ToString("yyyyMMdd-HHmmss");
        return Path.Combine(linkRoot, BackupFolderName, machineName, stamp, environment.Name, definition.TargetName);
    }

    private static void MoveToBackup(string file, string backupRoot, string relative)
    {
        var to = Path.Combine(backupRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(file, to, overwrite: true);
    }
}

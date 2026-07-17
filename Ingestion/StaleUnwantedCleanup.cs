using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>
/// Conservative relocation of stale <c>staging</c>/<c>source</c> work files that
/// belong to releases which are now marked <c>unwanted</c> (a curator veto).
///
/// These files are edge-case residue: a release partially staged then vetoed, or
/// failed-transform/failed-delete residue for a release later vetoed. They must
/// not linger as active pipeline state, but they are never deleted silently —
/// recoverable files are moved to <c>incoming-skip\&lt;platform&gt;</c> with
/// collision-safe names.
///
/// Safety: a release folder is cleaned ONLY when every release whose name maps to
/// that folder is <c>unwanted</c>. Folders that also map to a wanted/pending/
/// missing release (name-sanitization collision) or to no release at all
/// (orphan) are left untouched — the mapping is otherwise ambiguous. Transform
/// workdirs live under <c>transform-work\</c>, never in staging/source, so they
/// are never in scope.
/// </summary>
public static class StaleUnwantedCleanup
{
    /// <param name="stagingRoot"><c>staging\&lt;platform&gt;\&lt;datLine&gt;</c>.</param>
    /// <param name="sourceRoot"><c>source\&lt;platform&gt;\&lt;datLine&gt;</c>.</param>
    /// <param name="skipDir"><c>incoming-skip\&lt;platform&gt;</c> quarantine root.</param>
    /// <param name="releases">Every release for the DAT line as (Name, Status) — needed for collision-safe mapping.</param>
    public static StaleUnwantedCleanupResult Run(
        string stagingRoot,
        string sourceRoot,
        string skipDir,
        IReadOnlyList<(string Name, string Status)> releases)
    {
        var result = new StaleUnwantedCleanupResult();

        // safeFolder → statuses of every release that produces that folder name.
        var folderStatuses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, status) in releases)
        {
            var sf = IngestionPaths.SafeFolderName(name);
            if (!folderStatuses.TryGetValue(sf, out var list))
                folderStatuses[sf] = list = new List<string>();
            list.Add(status);
        }

        // Cleanable ⇔ folder maps to ≥1 release AND every mapping release is unwanted.
        bool IsCleanable(string safeFolder) =>
            folderStatuses.TryGetValue(safeFolder, out var statuses)
            && statuses.Count > 0
            && statuses.All(s => string.Equals(s, "unwanted", StringComparison.OrdinalIgnoreCase));

        CleanArea(stagingRoot, staging: true,  IsCleanable, skipDir, result);
        CleanArea(sourceRoot,  staging: false, IsCleanable, skipDir, result);
        return result;
    }

    private static void CleanArea(
        string root, bool staging, Func<string, bool> isCleanable,
        string skipDir, StaleUnwantedCleanupResult result)
    {
        if (!Directory.Exists(root)) return;

        var movedAction  = staging ? "stale-staging-unwanted-moved" : "stale-source-unwanted-moved";
        var failedAction = staging ? "stale-staging-cleanup-failed" : "stale-source-cleanup-failed";
        var platformSeg  = Path.GetFileName(skipDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (var folder in Directory.GetDirectories(root))
        {
            var safeFolder = Path.GetFileName(folder);
            // Skip ambiguous / wanted / orphan folders — never guess.
            if (!isCleanable(safeFolder)) continue;

            Directory.CreateDirectory(skipDir);
            bool allMoved = true;

            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                try
                {
                    var dest = IngestionPaths.CollisionSafePath(skipDir, fileName);
                    File.Move(file, dest, overwrite: false);
                    if (staging) result.StaleStagingMoved++;
                    else         result.StaleSourceMoved++;
                    result.Operations.Add(new IngestionOperation(
                        fileName, movedAction,
                        $"incoming-skip/{platformSeg}/{Path.GetFileName(dest)}"));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Report and leave the file in place — never delete on failure.
                    allMoved = false;
                    result.Operations.Add(new IngestionOperation(fileName, failedAction, ex.Message));
                }
            }

            // Remove the now-empty release folder (and any emptied subfolders) only
            // when every file was relocated. If any move failed, keep everything.
            if (!allMoved) continue;
            try
            {
                foreach (var dir in Directory
                    .GetDirectories(folder, "*", SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Length))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);

                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch { /* best-effort — leaving an empty folder is harmless */ }
        }
    }
}

/// <summary>Outcome of a <see cref="StaleUnwantedCleanup.Run"/> pass.</summary>
public sealed class StaleUnwantedCleanupResult
{
    public int StaleStagingMoved { get; set; }
    public int StaleSourceMoved  { get; set; }
    public List<IngestionOperation> Operations { get; } = new();
}

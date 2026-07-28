using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>
/// Conservative cleanup of leftover <c>staging</c> release folders that contain ONLY
/// cuesheet(s) (<c>.cue</c>) and whose release is already satisfied per the DB
/// (status <c>present</c> with a derived_artifacts row). These are residue from earlier
/// cue-only runs — a release whose derived artifact already exists, so a lone staged
/// <c>.cue</c> can never complete anything.
///
/// Safety rules (never delete silently, never touch useful work):
///   • Only folders whose files are ALL <c>.cue</c> are considered — a folder with a
///     <c>.bin</c> (or any non-cue file) is left untouched (it may still complete a release).
///   • Only when the folder's release(s) are satisfied — the caller supplies the predicate,
///     which is DB-authoritative (present + derived artifact row), not a filesystem probe.
///   • Files are MOVED to <c>incoming-skip\&lt;platform&gt;</c> (collision-safe), never deleted;
///     the empty folder is removed only after every file moved.
/// </summary>
public static class StaleCueOnlyStagingCleanup
{
    /// <param name="stagingRoot"><c>staging\&lt;platform&gt;\&lt;datLine&gt;</c>.</param>
    /// <param name="skipDir"><c>incoming-skip\&lt;platform&gt;</c> quarantine root.</param>
    /// <param name="isReleaseFolderSatisfied">safeFolder → true when its release(s) are satisfied
    ///   per the DB (present + derived artifact row); ambiguous/unsatisfied folders return false.</param>
    public static StaleCueOnlyCleanupResult Run(
        string stagingRoot,
        string skipDir,
        Func<string, bool> isReleaseFolderSatisfied)
    {
        var result = new StaleCueOnlyCleanupResult();
        if (!Directory.Exists(stagingRoot)) return result;

        var platformSeg = Path.GetFileName(skipDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (var folder in Directory.GetDirectories(stagingRoot))
        {
            var safeFolder = Path.GetFileName(folder);
            var files      = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);

            if (!IsCueOnly(files.Select(f => Path.GetFileName(f) ?? "").ToList())) continue;   // has .bin/other → keep
            if (!isReleaseFolderSatisfied(safeFolder)) continue;                  // not satisfied → keep

            Directory.CreateDirectory(skipDir);
            bool allMoved = true;

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                try
                {
                    var dest = IngestionPaths.CollisionSafePath(skipDir, fileName);
                    File.Move(file, dest, overwrite: false);
                    result.Moved++;
                    result.Operations.Add(new IngestionOperation(
                        fileName, "stale-cue-only-staging-moved",
                        $"incoming-skip/{platformSeg}/{Path.GetFileName(dest)}"));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    allMoved = false;
                    result.Operations.Add(new IngestionOperation(fileName, "stale-cue-only-cleanup-failed", ex.Message));
                }
            }

            if (!allMoved) continue;   // leave the folder intact if any move failed
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch { /* best-effort — an empty folder is harmless */ }
        }

        return result;
    }

    /// <summary>Pure decision: the folder contains at least one file and every file is a <c>.cue</c>.</summary>
    internal static bool IsCueOnly(IReadOnlyList<string> fileNames)
        => fileNames.Count > 0
        && fileNames.All(f => f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Outcome of a <see cref="StaleCueOnlyStagingCleanup.Run"/> pass.</summary>
public sealed class StaleCueOnlyCleanupResult
{
    public int Moved { get; set; }
    public List<IngestionOperation> Operations { get; } = new();
}

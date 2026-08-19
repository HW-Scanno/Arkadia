using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>The per-release inputs handed to the caller's transform dispatch delegate.</summary>
public sealed record LeafReleaseFinalizeArgs(
    ReleaseRecord                    Release,
    string                           SafeFolder,
    string                           SourceDir,
    List<ReleaseFileRecord>          ExpectedFiles);

/// <summary>
/// Explicit outcome of finalizing one leaf: the releases that stayed incomplete, finalized successfully, or
/// failed promotion/transform. Exposes the exact sets the historical Phase 8/9 consumes so a future Group
/// orchestrator can read per-leaf outcomes without reinterpreting log strings.
/// </summary>
public sealed class LeafFinalizationResult
{
    public required IReadOnlyCollection<string> IncompleteReleaseIds      { get; init; }
    public required IReadOnlyCollection<string> SuccessfulReleaseIds      { get; init; }
    public required IReadOnlyCollection<string> TransformFailedReleaseIds { get; init; }
}

/// <summary>
/// Phase 7 (staging completeness → staging→source promotion → transform dispatch → status), extracted
/// verbatim so a future Group run can finalize each in-scope leaf after shared staging completes. Behavior
/// is preserved exactly: name/path completeness (no hash/size/DB revalidation), <c>File.Move(overwrite:true)</c>
/// promotion with no rollback and best-effort staging-dir cleanup, the same operation/result strings, and
/// the same present-status write only when the transform did not fail. The transform itself is dispatched
/// through <paramref name="transformRelease"/> so the existing per-strategy processors remain in place,
/// untouched. UI-free: it depends only on the passed leaf identity/paths/config, the store, and delegates —
/// never on MainWindow/Avalonia/selection state.
/// </summary>
public static class LeafFinalizationEngine
{
    public static LeafFinalizationResult FinalizeLeaf(
        HashSet<string>                            affectedReleaseIds,
        IReadOnlyList<string>                      resumableReleaseIds,
        IReadOnlyDictionary<string, ReleaseRecord> releases,
        DatLineStore                               store,
        string                                     stagingRoot,
        string                                     sourceRoot,
        string                                     platformId,
        string                                     datLineId,
        Func<string, string>                       safeFileName,
        IngestionResult                            result,
        IProgress<IngestionProgress>               progress,
        HashSet<string>                            transformFailedReleases,
        HashSet<string>                            incompleteReleases,
        HashSet<string>                            successfulReleaseIds,
        Action<LeafReleaseFinalizeArgs>            transformRelease)
    {
        // ── Resume interrupted wanted staging ─────────────────────────────────
        // Add releases whose staging is already complete (detected up front) so they are promoted +
        // transformed exactly like freshly-staged releases (completeness is re-checked below).
        foreach (var rid in resumableReleaseIds)
        {
            if (!affectedReleaseIds.Add(rid)) continue;   // already staged this run
            if (!releases.TryGetValue(rid, out var resumeRel)) continue;
            result.StagingResumed++;
            var resumeOp = new IngestionOperation(resumeRel.Name, "staging-resumed",
                $"staging/{platformId}/{datLineId}/{safeFileName(resumeRel.Name)}");
            result.Operations.Add(resumeOp);
            progress.Report(new IngestionProgress { NewOperation = resumeOp });
        }

        foreach (var releaseId in affectedReleaseIds)
        {
            if (!releases.TryGetValue(releaseId, out var release)) continue;

            var safeFolder  = safeFileName(release.Name);
            var stagingDir  = Path.Combine(stagingRoot, safeFolder);
            var sourceDir   = Path.Combine(sourceRoot,  safeFolder);
            var expectedFiles = store.LoadReleaseFiles(releaseId);

            if (expectedFiles.Count == 0) continue;

            // rom.name may be a relative path inside the release; resolve it safely below the staging root. An
            // invalid/escaping name (never legitimately present) resolves to "not present" ⇒ incomplete.
            bool Staged(ReleaseFileRecord f) =>
                IngestionPaths.TryResolveReleaseChild(stagingDir, f.RomName, out var p) && File.Exists(p);

            bool complete = expectedFiles.All(Staged);

            if (!complete)
            {
                incompleteReleases.Add(releaseId);
                var missingFiles = expectedFiles
                    .Where(f => !Staged(f))
                    .Select(f => f.RomName)
                    .ToList();
                var incompleteOp = new IngestionOperation(
                    release.Name,
                    "incomplete-skipped",
                    $"missing: {string.Join(", ", missingFiles)}");
                result.Operations.Add(incompleteOp);
                progress.Report(new IngestionProgress { NewOperation = incompleteOp });
                continue;
            }

            // Move every file from staging → source
            bool sourceOk = true;
            try
            {
                Directory.CreateDirectory(sourceDir);
                foreach (var f in expectedFiles)
                {
                    // Preserve the DAT relative path on both sides; create the destination's parent for nested
                    // rom.name values. ResolveReleaseChild throws on an escaping name → caught below → the release
                    // fails assembly and its incoming is retained (no external write).
                    var src  = IngestionPaths.ResolveReleaseChild(stagingDir, f.RomName);
                    var dest = IngestionPaths.ResolveReleaseChild(sourceDir,  f.RomName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(src, dest, overwrite: true);
                }
                // Drain the staging release tree (best-effort). Nested rom.name paths leave empty subdirectories
                // after promotion; collapse them bottom-up and remove the release dir itself once empty. Files are
                // never deleted — any branch still holding unresolved material (and its ancestors) is preserved.
                IngestionPaths.RemoveEmptyDirectoriesBottomUp(stagingDir);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sourceOk = false;
                var failOp = new IngestionOperation(release.Name, "release-input-assembly-failed", ex.Message);
                result.Operations.Add(failOp);
                progress.Report(new IngestionProgress { NewOperation = failOp });
            }

            if (!sourceOk) continue;

            // staging → source is assembling a complete release input for transform,
            // not a durable promotion. Surfaced as "release-input-assembled".
            result.ReleaseInputsAssembled++;
            var archOp = new IngestionOperation(
                release.Name, "release-input-assembled",
                $"source/{platformId}/{datLineId}/{safeFolder}");
            result.Operations.Add(archOp);
            progress.Report(new IngestionProgress { NewOperation = archOp });

            // ── Dispatch to the leaf's configured transform ───────────────────
            // The existing per-strategy processors stay in the caller; they populate transformFailedReleases
            // on failure (and leave the managed source intact — the safe-copy invariant).
            transformRelease(new LeafReleaseFinalizeArgs(release, safeFolder, sourceDir, expectedFiles));

            if (!transformFailedReleases.Contains(releaseId))
            {
                store.UpdateReleaseStatus(releaseId, "present");
                result.ReleasesPresent++;
                successfulReleaseIds.Add(releaseId);
            }
        }

        return new LeafFinalizationResult
        {
            IncompleteReleaseIds      = incompleteReleases,
            SuccessfulReleaseIds      = successfulReleaseIds,
            TransformFailedReleaseIds = transformFailedReleases,
        };
    }
}

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
/// is preserved exactly for releases made only of physical requirements: name/path completeness (no hash/size/DB
/// revalidation), <c>File.Move(overwrite:true)</c> promotion with no rollback and best-effort staging-dir cleanup,
/// the same operation/result strings, and the same present-status write only when the transform did not fail. The
/// transform itself is dispatched through <paramref name="transformRelease"/> so the existing per-strategy
/// processors remain in place, untouched. UI-free: it depends only on the passed leaf identity/paths/config, the
/// store, and delegates — never on MainWindow/Avalonia/selection state.
///
/// <para>Every expected DAT file is classified with <see cref="DatFileRequirement"/> and completeness is always
/// evaluated over the COMPLETE authoritative file set:</para>
/// <list type="number">
///   <item>An unmatchable or contradictory requirement makes the release incomplete (<c>requirement-unsatisfiable</c>).</item>
///   <item>Every physical requirement must already be staged; otherwise the release is incomplete and nothing is synthesized.</item>
///   <item>Missing authoritative empty requirements are generated in staging (generate → verify length 0) — only for a
///         <c>missing</c> release, never overwriting an existing file (<c>empty-synthesized</c> / <c>empty-synthesis-failed</c>).</item>
///   <item>The completeness check then requires every file staged, and every synthesizable one exactly 0 bytes.</item>
/// </list>
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
            Report(result, progress, resumeOp);
        }

        foreach (var releaseId in affectedReleaseIds)
        {
            if (!releases.TryGetValue(releaseId, out var release)) continue;

            var safeFolder  = safeFileName(release.Name);
            var stagingDir  = Path.Combine(stagingRoot, safeFolder);
            var sourceDir   = Path.Combine(sourceRoot,  safeFolder);
            var expectedFiles = store.LoadReleaseFiles(releaseId);

            if (expectedFiles.Count == 0) continue;

            var classified = expectedFiles.Select(f => (File: f, Kind: DatFileRequirement.Classify(f))).ToList();

            // rom.name may be a relative path inside the release; resolve it safely below the staging root. An
            // invalid/escaping name (never legitimately present) resolves to "not present" ⇒ incomplete.
            bool Staged(ReleaseFileRecord f) =>
                IngestionPaths.TryResolveReleaseChild(stagingDir, f.RomName, out var p) && File.Exists(p);

            // ── 1. Requirements no ingestion path can satisfy → release stays incomplete (explicit reason) ──
            var unsatisfiable = classified.Where(c => DatFileRequirement.IsUnsatisfiable(c.Kind)).ToList();
            if (unsatisfiable.Count > 0)
            {
                incompleteReleases.Add(releaseId);
                Report(result, progress, new IngestionOperation(release.Name, "requirement-unsatisfiable",
                    string.Join("; ", unsatisfiable.Select(c =>
                        $"{c.File.RomName}: {DatFileRequirement.DescribeUnsatisfiable(c.Kind)}"))));
                continue;
            }

            // ── 2. Physical requirements must all be staged before anything is synthesized ──
            if (classified.Any(c => c.Kind == DatFileRequirementKind.Physical && !Staged(c.File)))
            {
                ReportIncomplete(release, expectedFiles.Where(f => !Staged(f)), incompleteReleases, result, progress);
                continue;
            }

            // ── 3. Authoritative empty requirements: generate → verify (only for a 'missing' release) ──
            bool synthesisFailed = false;
            foreach (var (f, kind) in classified)
            {
                if (kind != DatFileRequirementKind.SynthesizableEmpty) continue;
                var reason = EnsureSynthesizedEmpty(release, stagingDir, f, result, progress);
                if (reason is null) continue;

                synthesisFailed = true;
                Report(result, progress, new IngestionOperation($"{release.Name}/{f.RomName}", "empty-synthesis-failed", reason));
            }
            if (synthesisFailed)
            {
                incompleteReleases.Add(releaseId);
                continue;   // staging is preserved exactly as found (plus any file verified before the failure)
            }

            // ── 4. Completeness over the COMPLETE authoritative requirement set ──
            bool Satisfied(ReleaseFileRecord f)
            {
                if (!IngestionPaths.TryResolveReleaseChild(stagingDir, f.RomName, out var p) || !File.Exists(p)) return false;
                if (!DatFileRequirement.IsSynthesizableEmpty(f)) return true;
                try { return new FileInfo(p).Length == 0; } catch { return false; }
            }

            if (!expectedFiles.All(Satisfied))
            {
                ReportIncomplete(release, expectedFiles.Where(f => !Satisfied(f)), incompleteReleases, result, progress);
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
                Report(result, progress, new IngestionOperation(release.Name, "release-input-assembly-failed", ex.Message));
            }

            if (!sourceOk) continue;

            // staging → source is assembling a complete release input for transform,
            // not a durable promotion. Surfaced as "release-input-assembled".
            result.ReleaseInputsAssembled++;
            Report(result, progress, new IngestionOperation(
                release.Name, "release-input-assembled",
                $"source/{platformId}/{datLineId}/{safeFolder}"));

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

        // Drain the now-empty staging structure (DIRECTORY-ONLY, never files). The per-release loop above removed
        // each completed release tree; this prunes the empty leaf staging root and — when the platform's last leaf
        // drains — the platform staging root too. Any incomplete/resumable release keeps its files, its release
        // dir, the leaf dir, and the platform dir (RemoveEmptyDirectoriesBottomUp only collapses empty branches).
        // Safe here because all staging completes before any leaf is finalized.
        IngestionPaths.RemoveEmptyDirectoriesBottomUp(stagingRoot);
        if (Path.GetDirectoryName(stagingRoot) is { } platformStagingRoot)
            IngestionPaths.RemoveEmptyDirectoriesBottomUp(platformStagingRoot);

        return new LeafFinalizationResult
        {
            IncompleteReleaseIds      = incompleteReleases,
            SuccessfulReleaseIds      = successfulReleaseIds,
            TransformFailedReleaseIds = transformFailedReleases,
        };
    }

    /// <summary>
    /// Ensures one authoritative empty requirement exists in staging as a verified 0-byte file. Returns null on
    /// success (already-present-and-empty, or freshly generated and verified), else the failure reason. Never
    /// overwrites an existing file and never synthesizes for a release whose status is not <c>missing</c>.
    /// </summary>
    private static string? EnsureSynthesizedEmpty(
        ReleaseRecord release, string stagingDir, ReleaseFileRecord f,
        IngestionResult result, IProgress<IngestionProgress> progress)
    {
        // Same containment/path-safety rule used for normal staged files.
        if (!IngestionPaths.TryResolveReleaseChild(stagingDir, f.RomName, out var path))
            return "unsafe or invalid DAT file path; nothing was created";

        if (File.Exists(path))
        {
            long len;
            try { len = new FileInfo(path).Length; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return $"cannot inspect existing staged file: {ex.Message}"; }
            return len == 0
                ? null   // already materialized (e.g. an earlier interrupted run) — verified empty, nothing to do
                : $"an unexpected {len}-byte file already exists at the staging path; it was not overwritten";
        }

        if (Directory.Exists(path))
            return "a directory already occupies the staging path; nothing was created";

        // LOST is authoritative and UNWANTED/present are never re-created as a side effect: synthesis is permitted
        // only while the release is still 'missing'.
        if (!string.Equals(release.Status, "missing", StringComparison.OrdinalIgnoreCase))
            return $"release status is '{release.Status}' — empty-file synthesis is only permitted for missing releases";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            var len = new FileInfo(path).Length;   // re-read: verify exactly zero bytes before completeness
            if (len != 0)
                return $"verification failed: synthesized file is {len} bytes, expected 0";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"could not create the empty file: {ex.Message}";
        }

        Report(result, progress, new IngestionOperation(
            $"{release.Name}/{f.RomName}", "empty-synthesized", "0 bytes (DAT size 0, no hashes) — verified"));
        return null;
    }

    private static void ReportIncomplete(
        ReleaseRecord release, IEnumerable<ReleaseFileRecord> missing, HashSet<string> incompleteReleases,
        IngestionResult result, IProgress<IngestionProgress> progress)
    {
        incompleteReleases.Add(release.Id);
        Report(result, progress, new IngestionOperation(
            release.Name,
            "incomplete-skipped",
            $"missing: {string.Join(", ", missing.Select(f => f.RomName))}"));
    }

    private static void Report(IngestionResult result, IProgress<IngestionProgress> progress, IngestionOperation op)
    {
        result.Operations.Add(op);
        progress.Report(new IngestionProgress { NewOperation = op });
    }
}

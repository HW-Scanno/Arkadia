using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>One release's inputs for resumable-staging detection.</summary>
/// <param name="SynthesizableEmptyFiles">
/// Expected rom names classified <see cref="DatFileRequirementKind.SynthesizableEmpty"/> (authoritative zero-byte,
/// hashless). They are generated during finalization, so they need not be staged for a <c>missing</c> release to be
/// resumable. Null/empty keeps the historical behavior exactly.
/// </param>
public sealed record ResumeReleaseInput(
    string ReleaseId,
    string Name,
    string Status,
    IReadOnlyList<string> ExpectedFiles,
    IReadOnlyCollection<string>? SynthesizableEmptyFiles = null);

/// <summary>Result of <see cref="ResumableStagingDetector.Detect"/>.</summary>
public sealed record ResumableStagingResult(
    IReadOnlyList<string> ResumableReleaseIds,
    IReadOnlyList<(string ReleaseId, string Reason)> Skipped);

/// <summary>
/// Read-only detector for interrupted-run recovery: identifies WANTED releases
/// whose <c>staging</c> folder is complete but which never produced a derived
/// artifact (an ingest was interrupted after staging, before/around transform).
/// Such releases must be routed back through the normal transform path (Phase 7).
///
/// This helper only inspects the filesystem and the supplied release list — it
/// never moves, deletes, or writes anything, and never touches the DB. The
/// actual promote/transform/commit and any cleanup remain the pipeline's job.
///
/// Conservative rules (a release is resumable ONLY if all hold):
///   1. status is not <c>unwanted</c> (curator veto — handled by stale cleanup);
///   2. status is not <c>present</c> (a valid derived artifact already exists —
///      by the pipeline invariant, <c>present</c> is set only after a verified
///      derived commit, so re-processing is unnecessary);
///   3. its <see cref="IngestionPaths.SafeFolderName"/> maps to exactly one
///      release (no ambiguous name-sanitization collision);
///   4. every expected file is staged — except that, for a <c>missing</c> release
///      ONLY, authoritative synthesizable empty files may still be absent (they are
///      generated and verified during finalization). A <c>missing</c> release made
///      entirely of synthesizable empty files is resumable even with no staging folder.
///      <c>lost</c> releases are never routed through synthesis.
/// Orphan staging folders (no matching release) are never considered because the
/// detector iterates releases, not folders.
/// </summary>
public static class ResumableStagingDetector
{
    public static ResumableStagingResult Detect(
        string stagingRoot,
        IReadOnlyList<ResumeReleaseInput> releases)
    {
        var resumable = new List<string>();
        var skipped   = new List<(string, string)>();

        // safeFolder → number of releases (any status) that map to it — ambiguity guard.
        var folderCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in releases)
        {
            var sf = IngestionPaths.SafeFolderName(r.Name);
            folderCount[sf] = folderCount.GetValueOrDefault(sf) + 1;
        }

        foreach (var r in releases)
        {
            if (string.Equals(r.Status, "unwanted", StringComparison.OrdinalIgnoreCase))
            { skipped.Add((r.ReleaseId, "unwanted")); continue; }

            // 'present' ⟺ a valid derived artifact was already committed.
            if (string.Equals(r.Status, "present", StringComparison.OrdinalIgnoreCase))
            { skipped.Add((r.ReleaseId, "already-present")); continue; }

            var safeFolder = IngestionPaths.SafeFolderName(r.Name);
            if (folderCount[safeFolder] > 1)
            { skipped.Add((r.ReleaseId, "ambiguous-folder")); continue; }

            // Synthesis may stand in for staging ONLY for a 'missing' release (never lost/pending/other).
            var synthesizable = string.Equals(r.Status, "missing", StringComparison.OrdinalIgnoreCase)
                                && r.SynthesizableEmptyFiles is { Count: > 0 }
                ? new HashSet<string>(r.SynthesizableEmptyFiles, StringComparer.Ordinal)
                : null;

            var folder = Path.Combine(stagingRoot, safeFolder);
            if (!Directory.Exists(folder))
            {
                // A missing release made entirely of authoritative empty files needs no staged material at all.
                if (synthesizable is not null && r.ExpectedFiles.Count > 0
                    && r.ExpectedFiles.All(synthesizable.Contains))
                { resumable.Add(r.ReleaseId); continue; }

                skipped.Add((r.ReleaseId, "no-staging")); continue;
            }

            if (r.ExpectedFiles.Count == 0)
            { skipped.Add((r.ReleaseId, "no-expected-files")); continue; }

            // rom names may be relative paths inside the release; resolve safely below the staging folder (an
            // invalid/escaping name resolves to "not present" ⇒ incomplete-staging).
            var complete = r.ExpectedFiles.All(f =>
                (IngestionPaths.TryResolveReleaseChild(folder, f, out var p) && File.Exists(p))
                || (synthesizable is not null && synthesizable.Contains(f)));
            if (!complete)
            { skipped.Add((r.ReleaseId, "incomplete-staging")); continue; }

            resumable.Add(r.ReleaseId);
        }

        return new ResumableStagingResult(resumable, skipped);
    }
}

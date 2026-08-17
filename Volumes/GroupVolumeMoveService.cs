using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Builds the authoritative expected-hash snapshot for <b>Move Volume</b> on a Group-owned volume, BEFORE any
/// physical copy (V-G4). Group volumes nest artifacts under leaf segments, so the verification identity is the
/// normalized volume-relative path (leaf-segment + '/' + FileName), never the basename.
///
/// Authoritative artifact ownership is <c>volume_artifacts.dat_line_id</c> (the exact leaf), not the current
/// Group membership: the snapshot resolves expected hashes from those exact leaf stores. Group membership is used
/// only to resolve each leaf's DB path and to revalidate the leaf→segment mapping (via
/// <see cref="GroupRelativeLeafPath"/>). Any leaf that cannot be reconciled (absent from membership, missing DB,
/// unreadable, unresolved DerivedArtifactId, invalid/colliding segment, duplicate relative path) BLOCKS the move
/// with a diagnostic — the assignment is never mutated to "fix" it. Reads only (leaf stores via OpenReadOnly).
/// </summary>
public static class GroupVolumeMoveService
{
    /// <summary>One expected destination file: its relative path plus the authoritative catalog metadata.</summary>
    public sealed record ExpectedFile(
        string RelativePath, long SizeBytes, string ExpectedSha1, string DatLineId, string DerivedArtifactId);

    /// <summary>
    /// The result of a snapshot attempt. On success <see cref="BlockReason"/> is null and
    /// <see cref="ExpectedSha1ByRelativePath"/> (normalized key → valid SHA1) drives destination verification.
    /// On any reconciliation failure <see cref="BlockReason"/> is a user-facing message and the move must not copy.
    /// </summary>
    public sealed record Snapshot(
        IReadOnlyDictionary<string, string> ExpectedSha1ByRelativePath,
        IReadOnlyList<ExpectedFile>         Files,
        string?                             BlockReason)
    {
        public static Snapshot Blocked(string reason) =>
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<ExpectedFile>(), reason);
    }

    public static Snapshot Build(
        CatalogService                 catalog,
        string                         dataDir,
        VolumeRecord                   volume,
        IReadOnlyList<GroupLeafRecord> groupLeaves)
    {
        if (volume.GroupId is not { } groupId)
            return Snapshot.Blocked("Internal error: Move snapshot requested for a non-Group volume.");

        var vas = catalog.GetVolumeArtifacts(volume.Id);

        // Authoritative leaf ids for THIS volume come from the assignment rows, not current navigation state.
        var assignedLeafIds = vas.Select(v => v.DatLineId).Distinct(StringComparer.Ordinal)
                                 .OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Revalidate the leaf → physical-segment mapping (empty/unsafe/collision) for exactly those leaves.
        var (segmentMap, segErr) = GroupRelativeLeafPath.BuildMap(groupId, assignedLeafIds);
        if (segErr is not null)
            return Snapshot.Blocked($"Cannot move this Group volume — {segErr}");

        var membershipById = groupLeaves.ToDictionary(l => l.DatLine.Id, StringComparer.Ordinal);
        var files          = new List<ExpectedFile>();

        foreach (var leafId in assignedLeafIds)
        {
            // Ownership must be reconcilable against current membership (path resolution) — never dropped.
            if (!membershipById.TryGetValue(leafId, out var leaf))
                return Snapshot.Blocked(
                    $"Assigned leaf '{leafId}' is no longer a member of group '{groupId}'. " +
                    "Reconcile the group before moving this volume. No files were copied.");

            var dsp = leaf.DatLine.DataStorePath;
            var abs = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";
            if (abs.Length == 0 || !File.Exists(abs))
                return Snapshot.Blocked(
                    $"The database for leaf '{leafId}' is unavailable, so its authoritative artifact hashes " +
                    "cannot be read. No files were copied.");

            var segment    = segmentMap[leafId];
            var leafDaIds  = vas.Where(v => v.DatLineId == leafId).Select(v => v.DerivedArtifactId).ToList();

            Dictionary<string, ArtifactBuildInfo> infoByDaId;
            try
            {
                var store = DatLineStore.OpenReadOnly(abs);   // read-only; no schema/DDL
                infoByDaId = store.GetArtifactBuildInfos(leafDaIds)
                                  .ToDictionary(i => i.DerivedArtifactId, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Snapshot.Blocked(
                    $"The database for leaf '{leafId}' could not be read ({ex.GetType().Name}). No files were copied.");
            }

            foreach (var daId in leafDaIds)
            {
                if (!infoByDaId.TryGetValue(daId, out var info))
                    return Snapshot.Blocked(
                        $"Artifact '{daId}' assigned from leaf '{leafId}' could not be resolved in its database. " +
                        "No files were copied.");

                files.Add(new ExpectedFile(
                    RelativePath:      $"{segment}/{info.FileName}",
                    SizeBytes:         info.SizeBytes,
                    ExpectedSha1:      info.ExpectedSha1,
                    DatLineId:         leafId,
                    DerivedArtifactId: daId));
            }
        }

        // Final relative-path identity check (catches same-FileName within a single leaf too).
        var (map, duplicate) = VolumeMoveFileVerifier.BuildExpectedSha1MapByRelativePath(
            files.Select(f => (f.RelativePath, f.ExpectedSha1, f.DatLineId, f.DerivedArtifactId)));

        if (duplicate is not null)
            return Snapshot.Blocked(
                $"Two assigned artifacts map to the same volume path '{duplicate.RelativePath}':\n\n" +
                $"  • leaf '{duplicate.DatLineIdA}', artifact '{duplicate.DerivedArtifactIdA}'\n" +
                $"  • leaf '{duplicate.DatLineIdB}', artifact '{duplicate.DerivedArtifactIdB}'\n\n" +
                "Resolve this ambiguity before moving the volume. No files were copied.");

        return new Snapshot(map, files, null);
    }
}

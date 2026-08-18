using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// One artifact to reabsorb from a volume back into the local archive. Shared by Single and Group so the exact
/// historical copy→verify→delete loop runs unchanged over both. The only owner difference is <see cref="SourcePath"/>
/// (flat for Single, leaf-nested for Group); the archive destination is always the DB-authoritative
/// <c>derived_artifacts.relative_path</c>, and <see cref="LeafDbAbsPath"/> lets DA/release finalization run on the
/// correct leaf store (one store for Single, per-leaf for Group). <see cref="DatLineId"/> is the exact leaf owner.
/// </summary>
public sealed record ReabsorbCandidate(
    string DerivedArtifactId,
    string DatLineId,
    string FileName,
    long   SizeBytes,
    string SourcePath,
    string ArchiveDestPath,
    string LeafDbAbsPath);

/// <summary>
/// Read-only Group Reabsorb candidate builder (V-G8). PORTS Single Reabsorb: it enumerates the Group volume's
/// assignments, groups them by exact leaf <c>dat_line_id</c>, reads each leaf's <c>GetArtifactBuildInfos</c> from
/// its own store, and produces leaf-nested source paths (<c>&lt;groupRoot&gt;/&lt;leaf-segment&gt;/&lt;file&gt;</c>)
/// with DB-authoritative archive destinations — the same shared <see cref="ReabsorbCandidate"/> set the Single path
/// builds. It performs NO physical mutation and invents NO archive layout. Membership / segment-map / leaf-DB drift
/// BLOCKS (surfaced as <see cref="BlockReason"/>); an empty volume returns <c>AssignmentCount == 0</c> so the caller
/// applies the historical no-op. Reads only (leaf stores via <see cref="DatLineStore.OpenReadOnly"/>).
/// </summary>
public static class GroupReabsorbPlanningService
{
    public sealed record Result(
        IReadOnlyList<ReabsorbCandidate> Candidates,
        int    LostCount,
        int    AssignmentCount,
        string? BlockReason);

    public static Result Plan(
        CatalogService                 catalog,
        string                         dataDir,
        string                         groupVolumeId,
        string                         groupId,
        string                         groupVolumeRoot,
        string                         appRoot,
        IReadOnlyList<GroupLeafRecord> groupLeaves)
    {
        var assignments     = catalog.GetVolumeArtifacts(groupVolumeId);
        int assignmentCount = assignments.Count;
        int lostCount       = assignments.Count(va => va.Status == "lost");
        var active          = assignments.Where(va => va.Status != "lost").ToList();

        // Empty (or lost-only) volume → historical no-op is decided by the caller from these counts.
        if (active.Count == 0)
            return new Result(Array.Empty<ReabsorbCandidate>(), lostCount, assignmentCount, null);

        var assignedLeafIds = active.Select(v => v.DatLineId).Distinct(StringComparer.Ordinal)
                                    .OrderBy(id => id, StringComparer.Ordinal).ToList();

        var (segmentMap, segErr) = GroupRelativeLeafPath.BuildMap(groupId, assignedLeafIds);
        if (segErr is not null)
            return new Result(Array.Empty<ReabsorbCandidate>(), lostCount, assignmentCount,
                $"Cannot reabsorb this Group volume — {segErr}");

        var membershipById = groupLeaves.ToDictionary(l => l.DatLine.Id, StringComparer.Ordinal);

        var candidates = new List<ReabsorbCandidate>();
        foreach (var leafId in assignedLeafIds)
        {
            if (!membershipById.TryGetValue(leafId, out var leaf))
                return new Result(Array.Empty<ReabsorbCandidate>(), lostCount, assignmentCount,
                    $"Assigned leaf '{leafId}' is no longer a member of collection '{groupId}'. " +
                    "Reconcile the collection before reabsorbing this volume.");

            var dsp = leaf.DatLine.DataStorePath;
            var abs = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";
            if (abs.Length == 0 || !File.Exists(abs))
                return new Result(Array.Empty<ReabsorbCandidate>(), lostCount, assignmentCount,
                    $"The database for leaf '{leafId}' is unavailable, so it cannot be reabsorbed.");

            var segment    = segmentMap[leafId];
            var leafActive = active.Where(v => v.DatLineId == leafId).ToList();
            var leafDaIds  = leafActive.Select(v => v.DerivedArtifactId).ToList();

            var store    = DatLineStore.OpenReadOnly(abs);   // read-only metadata
            var infoById = store.GetArtifactBuildInfos(leafDaIds)
                                .ToDictionary(b => b.DerivedArtifactId, StringComparer.Ordinal);

            foreach (var va in leafActive)
            {
                if (!infoById.TryGetValue(va.DerivedArtifactId, out var info)) continue;
                var src = VolumeArtifactPathBuilder.GetGroupFlatFullPath(groupVolumeRoot, segment, info.FileName);
                if (!File.Exists(src)) continue;   // only artifacts physically present on the volume (matches Single)
                var dst = Path.Combine(appRoot, info.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                candidates.Add(new ReabsorbCandidate(
                    info.DerivedArtifactId, leafId, info.FileName, info.SizeBytes, src, dst, abs));
            }
        }

        return new Result(candidates, lostCount, assignmentCount, null);
    }
}

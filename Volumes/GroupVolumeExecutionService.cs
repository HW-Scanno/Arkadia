using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>Progress tick during Group Apply + local Build.</summary>
public sealed record GroupVolumeBuildProgress(string PhaseText, int Index, int Total);

/// <summary>Outcome of a Group local Build.</summary>
public enum GroupBuildStatus { Built, NothingToBuild }

/// <summary>Compact result of a Group Apply + local Build.</summary>
public sealed class GroupVolumeExecutionResult
{
    public int             AssignedCount     { get; init; }
    public int             MovedCount        { get; init; }
    public int             AlreadyBuiltCount { get; init; }
    public int             TotalArtifacts    { get; init; }
    public int             LeafCount         { get; init; }
    public string          VolumeFolder      { get; init; } = "";
    public GroupBuildStatus Status           { get; init; }
}

/// <summary>
/// Group volume Apply + LOCAL Build (V-G3). Mirrors Single local-Build semantics (File.Move archive→workspace,
/// no destination hashing — the strong verification belongs to the future Group Move Volume), extended to the
/// nested per-leaf layout <c>volumes/&lt;label&gt;/&lt;leaf-segment&gt;/&lt;artifact&gt;</c>. Apply writes one
/// batch of per-leaf <c>volume_artifacts</c> (each row's dat_line_id = the exact leaf; the Group is only the
/// volume owner). Build groups the committed rows by leaf, resolves each leaf store, pre-scans the ENTIRE build
/// set before the first move, moves deterministically, finalizes per-leaf derived/release state, and always
/// finalizes the workspace location + init→present (even when everything was already built — the Single
/// recovery gap is not reproduced here). Never touches physical disks, Move Volume, or the verifier.
/// </summary>
public static class GroupVolumeExecutionService
{
    // ── Stale-plan guard (pure): the approved vs fresh INCLUDED identity, by (DatLineId, ReleaseId) ──
    public static IReadOnlyList<(string DatLineId, string ReleaseId)> IncludedIdentity(GroupVolumePlanningResult r)
        => r.Leaves
            .SelectMany(l => l.Items.Where(i => i.Decision.Decision == "include")
                                    .Select(i => (l.DatLineId, i.Decision.ReleaseId)))
            .OrderBy(x => x.DatLineId, StringComparer.Ordinal)
            .ThenBy(x => x.ReleaseId, StringComparer.Ordinal)
            .ToList();

    public static bool IncludedSetMatches(GroupVolumePlanningResult preview, GroupVolumePlanningResult fresh)
        => IncludedIdentity(preview).SequenceEqual(IncludedIdentity(fresh));

    // ── Apply: commit per-leaf assignments for the included plan items ──
    public static int Apply(
        CatalogService                 catalog,
        string                         dataDir,
        VolumeRecord                   volume,
        IReadOnlyList<GroupLeafRecord> leaves,
        GroupVolumePlanningResult      plan)
    {
        var leafById = leaves.ToDictionary(l => l.DatLine.Id, StringComparer.Ordinal);
        var now      = DateTime.UtcNow;
        var batch    = new List<VolumeArtifactRecord>();
        var sizeByDa = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var leaf in plan.Leaves)   // plan leaves are already DatLineId-Ordinal ordered
        {
            var includedReleaseIds = leaf.Items
                .Where(i => i.Decision.Decision == "include")
                .Select(i => i.Decision.ReleaseId)
                .ToList();
            if (includedReleaseIds.Count == 0) continue;
            if (!leafById.TryGetValue(leaf.DatLineId, out var rec)) continue;

            var store            = new DatLineStore(Path.Combine(dataDir, rec.DatLine.DataStorePath));
            var derivedByRelease = store.GetDerivedArtifactIdsForReleases(includedReleaseIds);

            foreach (var releaseId in includedReleaseIds)
            {
                if (!derivedByRelease.TryGetValue(releaseId, out var daItems)) continue;
                foreach (var (daId, ck) in daItems)
                    batch.Add(new VolumeArtifactRecord
                    {
                        Id                 = Guid.NewGuid().ToString("N"),
                        VolumeId           = volume.Id,
                        DatLineId          = leaf.DatLineId,   // exact leaf — the Group is NOT the artifact owner
                        DerivedArtifactId  = daId,
                        ContentIdentityKey = ck,
                        Status             = "present_in_final",
                        AddedAtUtc         = now,
                    });
            }

            foreach (var da in store.GetDerivedArtifacts())
                sizeByDa[da.Id] = da.DerivedSizeBytes;   // authoritative expected sizes (not filesystem)
        }

        catalog.SaveVolumeArtifactsBatch(batch);
        catalog.RecalculateVolumeActualSize(volume.Id, sizeByDa);
        return batch.Count;
    }

    // ── Build: materialize committed assignments into the nested workspace and finalize ──
    public static GroupVolumeExecutionResult Build(
        CatalogService                     catalog,
        string                             dataDir,
        string                             appRoot,
        VolumeRecord                       volume,
        IReadOnlyList<GroupLeafRecord>     leaves,
        IReadOnlyDictionary<string, string> segmentMap,
        IProgress<GroupVolumeBuildProgress>? progress = null)
    {
        var volumeFolder = Path.Combine(appRoot, "volumes", Ingestion.IngestionPaths.SafeFolderName(volume.Label));
        var vas          = catalog.GetVolumeArtifacts(volume.Id);
        if (vas.Count == 0)
            return new GroupVolumeExecutionResult { VolumeFolder = volumeFolder, Status = GroupBuildStatus.NothingToBuild };

        var leafById = leaves.ToDictionary(l => l.DatLine.Id, StringComparer.Ordinal);

        // Deterministic order: leaves by DatLineId Ordinal, artifacts by DerivedArtifactId Ordinal.
        var leafGroups = vas.GroupBy(v => v.DatLineId).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        var buildSet = new List<(string Leaf, string DaId, string Src, string Dst)>();
        foreach (var g in leafGroups)
        {
            var datLineId = g.Key;
            if (!segmentMap.TryGetValue(datLineId, out var segment))
                throw new InvalidOperationException($"No physical segment resolved for leaf '{datLineId}'.");
            if (!leafById.TryGetValue(datLineId, out var rec))
                throw new InvalidOperationException($"Leaf '{datLineId}' is no longer a member of the Group.");

            var store = new DatLineStore(Path.Combine(dataDir, rec.DatLine.DataStorePath));
            var infos = store.GetArtifactBuildInfos(g.Select(v => v.DerivedArtifactId).ToList())
                             .OrderBy(i => i.DerivedArtifactId, StringComparer.Ordinal);
            foreach (var info in infos)
            {
                var src = Path.Combine(appRoot, info.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var dst = VolumeArtifactPathBuilder.GetGroupBuildDestinationPath(volumeFolder, segment, info);
                buildSet.Add((datLineId, info.DerivedArtifactId, src, dst));
            }
        }

        // ── Pre-scan the ENTIRE Group build set before the first move (block on any inconsistency) ──
        var notBuilt     = new List<(string Leaf, string DaId, string Src, string Dst)>();
        var alreadyBuilt = new List<(string Leaf, string DaId)>();
        var inconsistent = new List<string>();
        foreach (var (leaf, daId, src, dst) in buildSet)
        {
            bool s = File.Exists(src), d = File.Exists(dst);
            if      (s && !d) notBuilt.Add((leaf, daId, src, dst));
            else if (!s && d) alreadyBuilt.Add((leaf, daId));
            else if (s && d)  inconsistent.Add($"{leaf}/{Path.GetFileName(dst)}: both source and destination exist");
            else              inconsistent.Add($"{leaf}/{Path.GetFileName(dst)}: neither source nor destination exists");
        }
        if (inconsistent.Count > 0)
        {
            const int max = 5;
            var lines = string.Join("\n", inconsistent.Take(max).Select(x => "  " + x));
            var more  = inconsistent.Count > max ? $"\n  …and {inconsistent.Count - max} more." : "";
            throw new InvalidOperationException(
                $"{inconsistent.Count} artifact(s) in an inconsistent state — no files were moved:\n\n{lines}{more}");
        }

        // ── Move (deterministic order). A mid-move failure propagates BEFORE finalization → volume stays init ──
        int total = buildSet.Count, moved = 0;
        foreach (var (leaf, daId, src, dst) in notBuilt)
        {
            progress?.Report(new GroupVolumeBuildProgress("Building", moved + 1, notBuilt.Count));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Move(src, dst);
            moved++;
        }

        // ── Finalize per-leaf derived/release state for everything now built (moved ∪ already-built) ──
        var builtByLeaf = notBuilt.Select(x => (x.Leaf, x.DaId))
            .Concat(alreadyBuilt)
            .GroupBy(x => x.Leaf, StringComparer.Ordinal);
        foreach (var g in builtByLeaf.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!leafById.TryGetValue(g.Key, out var rec)) continue;
            var store  = new DatLineStore(Path.Combine(dataDir, rec.DatLine.DataStorePath));
            var daIds  = g.Select(x => x.DaId).ToList();
            store.BatchUpdateDerivedArtifactStatus(daIds, "present");
            store.RecalculateReleaseStatusForArtifacts(daIds);
        }

        // ── Finalize the volume: workspace location + init→present (ALWAYS, even when moved == 0) ──
        catalog.SetCurrentLocation(new VolumeLocationRecord
        {
            Id           = Guid.NewGuid().ToString("N"),
            VolumeId     = volume.Id,
            LocationType = "workspace",
            DiskId       = null,
            Path         = volumeFolder,
            IsCurrent    = true,
            CreatedAt    = DateTime.UtcNow,
        });
        if (volume.Status == "init")
            catalog.UpdateVolumeStatus(volume.Id, "present");

        return new GroupVolumeExecutionResult
        {
            MovedCount        = moved,
            AlreadyBuiltCount = alreadyBuilt.Count,
            TotalArtifacts    = total,
            LeafCount         = leafGroups.Count,
            VolumeFolder      = volumeFolder,
            Status            = GroupBuildStatus.Built,
        };
    }
}

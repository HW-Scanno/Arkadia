using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data.Identifiers;

namespace Arkadia.Data;

/// <summary>Why an undo-only Group delete was refused (<see cref="None"/> = allowed).</summary>
public enum GroupDeleteBlock
{
    None,
    GroupNotFound,
    GroupOwnedVolumes,
    LeafVolumeAssignments,
    PresentContent,
    StagingMaterial,
}

/// <summary>Result of the pre-mutation safety gate.</summary>
public sealed record GroupDeleteEvaluation(bool CanDelete, GroupDeleteBlock Reason, string Message)
{
    public static GroupDeleteEvaluation Allowed() => new(true, GroupDeleteBlock.None, "");
    public static GroupDeleteEvaluation Blocked(GroupDeleteBlock reason, string message) => new(false, reason, message);
}

/// <summary>Result of a delete attempt. When <see cref="Deleted"/> is false the catalog/filesystem are unchanged.</summary>
public sealed record GroupDeleteOutcome(
    bool Deleted, GroupDeleteBlock Reason, string Message, int LeavesDeleted, IReadOnlyList<string> FileDeleteFailures);

/// <summary>
/// Undo-only "Delete Group": removes a mistaken Group and its leaf DAT catalogs ONLY when the Group has no
/// ingested / assigned / physical ownership state — i.e. it is equivalent to undoing Group creation. It never
/// deletes archive/volume/staging/incoming files; if any such authoritative state exists it REFUSES rather than
/// cleaning up. The authoritative gate re-reads state at delete time (never trusting the displayed 0%), reusing
/// the narrowest existing queries; catalog removal is one transaction via
/// <see cref="CatalogService.DeleteDatGroupCatalog"/>, and leaf DB files are deleted best-effort after commit
/// (matching Single Delete DAT). Single Delete DAT is untouched.
/// </summary>
public static class GroupDeleteService
{
    private static readonly IReadOnlySet<string> EmptyAssigned = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The authoritative safety gate. Cheap catalog checks first (volumes / volume_artifacts), then per-leaf DB
    /// present/derived checks (leaf DB IO — done only on an explicit delete, never on selection), then a
    /// filesystem staging-material probe (empty structural directories do not count). Read-only.
    /// </summary>
    public static GroupDeleteEvaluation Evaluate(
        CatalogService catalog, string dataDir, string appRoot, string groupId, CancellationToken ct = default)
    {
        var group = catalog.GetDatGroup(DatGroupId.FromPersisted(groupId));
        if (group is null)
            return GroupDeleteEvaluation.Blocked(GroupDeleteBlock.GroupNotFound, "That Group no longer exists.");

        var leaves  = catalog.GetLeavesForGroup(groupId);
        var leafIds = new HashSet<string>(leaves.Select(l => l.DatLine.Id), StringComparer.Ordinal);

        // ── Volume ownership (catalog, cheap) ──
        var volumes = catalog.GetVolumes();
        if (volumes.Any(v => string.Equals(v.GroupId, groupId, StringComparison.OrdinalIgnoreCase)))
            return GroupDeleteEvaluation.Blocked(GroupDeleteBlock.GroupOwnedVolumes,
                "This Group cannot be deleted because it has assigned volumes.");
        if (volumes.Any(v => v.DatLineId is { } dl && leafIds.Contains(dl)))
            return GroupDeleteEvaluation.Blocked(GroupDeleteBlock.LeafVolumeAssignments,
                "This Group cannot be deleted because one of its leaf DATs has an assigned volume.");

        // ── Leaf volume_artifacts (catalog, one bulk query) ──
        var assigned = catalog.GetAllAssignedDerivedIdsByDatLine();
        if (leafIds.Any(id => assigned.TryGetValue(id, out var set) && set.Count > 0))
            return GroupDeleteEvaluation.Blocked(GroupDeleteBlock.LeafVolumeAssignments,
                "This Group cannot be deleted because it has volume-assigned artifacts.");

        // ── Present / owned derived state (leaf DB IO) ──
        foreach (var leaf in leaves)
        {
            ct.ThrowIfCancellationRequested();
            var dsp = leaf.DatLine.DataStorePath;
            if (dsp.Length == 0) continue;
            var abs = Path.Combine(dataDir, dsp);
            if (!File.Exists(abs)) continue;

            var store = new DatLineStore(abs);
            if (store.GetAllStatusCounts().Present > 0)
                return GroupDeleteEvaluation.Blocked(GroupDeleteBlock.PresentContent,
                    "This Group cannot be deleted because it contains ingested (present) releases.");
            if (store.GetPresentDerivedAssignmentStats(EmptyAssigned).TotalPresent > 0)
                return GroupDeleteEvaluation.Blocked(GroupDeleteBlock.PresentContent,
                    "This Group cannot be deleted because it contains derived artifacts in the Local Archive.");
        }

        // ── Staging material (filesystem; empty structural dirs don't count) ──
        var staging = Arkadia.Systems.StagingSnapshot.LeafIdsWithStaging(appRoot, group.HardwareFamilyId);
        if (leafIds.Any(staging.Contains))
            return GroupDeleteEvaluation.Blocked(GroupDeleteBlock.StagingMaterial,
                "This Group cannot be deleted because it has staged material still being processed.");

        return GroupDeleteEvaluation.Allowed();
    }

    /// <summary>
    /// Re-evaluates the gate and, only if safe, deletes the Group's leaf <c>dat_lines</c> then the
    /// <c>dat_groups</c> row in one catalog transaction, then best-effort deletes the leaf DB files. A blocked
    /// gate leaves everything unchanged. A catalog failure leaves the catalog unchanged (transaction rollback);
    /// leaf-file delete failures are surfaced but never roll back the committed catalog state.
    /// </summary>
    public static GroupDeleteOutcome Delete(
        CatalogService catalog, string dataDir, string appRoot, string groupId, CancellationToken ct = default)
    {
        var eval = Evaluate(catalog, dataDir, appRoot, groupId, ct);
        if (!eval.CanDelete)
            return new GroupDeleteOutcome(false, eval.Reason, eval.Message, 0, Array.Empty<string>());

        var leaves  = catalog.GetLeavesForGroup(groupId);
        var leafIds = leaves.Select(l => l.DatLine.Id).ToList();
        var paths   = leaves.Select(l => l.DatLine.DataStorePath).Where(p => p.Length > 0).ToList();

        // One catalog transaction — throws → nothing committed → catalog unchanged.
        catalog.DeleteDatGroupCatalog(groupId, leafIds);

        // Best-effort leaf DB file cleanup AFTER commit (never rolls back catalog). The safety preflight opened
        // each leaf DB read-only, so release any pooled connection first (unlocks the file for deletion) —
        // the same release the leaf-DB builder uses before publishing by rename.
        var failures = new List<string>();
        foreach (var p in paths)
        {
            var abs = Path.Combine(dataDir, p);
            if (!File.Exists(abs)) continue;
            try
            {
                new DatLineStore(abs).ReleaseConnections();
                File.Delete(abs);
            }
            catch { failures.Add(p); }
        }

        return new GroupDeleteOutcome(true, GroupDeleteBlock.None, "", leafIds.Count, failures);
    }
}

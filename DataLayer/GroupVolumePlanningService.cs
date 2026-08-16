using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Data;

/// <summary>Progress tick during Group volume planning (per leaf).</summary>
public sealed record GroupVolumePlanProgress(string PhaseText, int LeafIndex, int LeafTotal);

/// <summary>
/// Read-only gathering front-end for a Group volume plan. For every leaf of the Group it reuses the EXACT
/// Single eligibility path — <see cref="CatalogService.GetAssignedDerivedIdsByDatLine"/> (assignments across
/// ALL volumes) + <see cref="DatLineStore.GetPlanningCandidates"/> via a strictly read-only
/// <see cref="DatLineStore.OpenReadOnly"/> open — then hands the tagged per-leaf candidates to the pure
/// <see cref="GroupVolumePlanner"/> (one global capacity budget). It never creates/repairs a missing leaf DB:
/// a missing/unreadable leaf becomes a blocking diagnostic (Readiness = Blocked), never a silent omission.
/// Performs only catalog/leaf-DB reads and archive-presence checks — no catalog/filesystem mutation.
/// </summary>
public static class GroupVolumePlanningService
{
    public static GroupVolumePlanningResult Plan(
        CatalogService                     catalog,
        string                             dataDir,
        string                             appRoot,
        VolumeRecord                       volume,
        IReadOnlyList<GroupLeafRecord>     leaves,
        IProgress<GroupVolumePlanProgress>? progress = null)
    {
        if (volume.GroupId is null)
            throw new InvalidOperationException("GroupVolumePlanningService requires a Group-owned volume.");

        // Deterministic leaf order: DatLineId Ordinal (also enforced inside the pure planner).
        var ordered = leaves.OrderBy(l => l.DatLine.Id, StringComparer.Ordinal).ToList();

        var inputs      = new List<GroupLeafPlanInput>(ordered.Count);
        var diagnostics = new List<string>();

        for (int i = 0; i < ordered.Count; i++)
        {
            var leaf      = ordered[i];
            var datLineId = leaf.DatLine.Id;
            progress?.Report(new GroupVolumePlanProgress($"Planning leaf {i + 1} / {ordered.Count}", i + 1, ordered.Count));

            var dsp = leaf.DatLine.DataStorePath;
            var abs = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";

            // Never construct a writable store for a missing path (that would create/repair it). File.Exists gate.
            if (abs.Length == 0 || !File.Exists(abs))
            {
                diagnostics.Add($"{datLineId}: leaf database is missing.");
                continue;
            }

            try
            {
                var store      = DatLineStore.OpenReadOnly(abs);   // Mode=ReadOnly, no EnsureSchema/DDL
                var assigned   = catalog.GetAssignedDerivedIdsByDatLine(datLineId);
                var candidates = store.GetPlanningCandidates(appRoot, assigned);
                var display    = leaf.DatLine.Name.Length > 0 ? leaf.DatLine.Name : datLineId;
                inputs.Add(new GroupLeafPlanInput(datLineId, display, candidates));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostics.Add($"{datLineId}: leaf database unreadable ({ex.GetType().Name}).");
            }
        }

        return GroupVolumePlanner.Plan(
            volume.Id, volume.GroupId, volume.PlannedSizeBytes, volume.ActualSizeBytes,
            inputs, diagnostics);
    }
}

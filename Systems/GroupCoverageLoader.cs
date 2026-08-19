using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Arkadia.Data;

namespace Arkadia.Systems;

/// <summary>
/// Aggregate Group storage facts gathered by opening each leaf DB exactly ONCE, off the UI thread. Feeds both the
/// Group card's wanted coverage (Σ Present ÷ Σ max(0, ReleaseCount − Unwanted)) and its archive-lifecycle badge
/// (Σ owned present derived artifacts, Σ unassigned). Anything catalog- or filesystem-derived (LOCAL volumes,
/// staging) is combined by the caller — this struct is only the leaf-DB half.
/// </summary>
public readonly record struct GroupStorageFacts(int Present, int Unwanted, int OwnedPresentDerived, int Unassigned);

/// <summary>
/// Computes <see cref="GroupStorageFacts"/> for a Group by summing over its leaf databases. This is the ONLY place
/// that opens Group-leaf DBs for card enrichment, and it is always called off the UI thread (lazily, on demand) —
/// never during the synchronous startup/RefreshSystems path (guarded by <see cref="DatLineStore.ConstructionCount"/>
/// in the startup-cost regression test). Each existing leaf DB is constructed exactly once and queried for both its
/// status counts and its present-derived assignment stats, so adding the lifecycle facts costs no extra leaf opens.
/// </summary>
public static class GroupCoverageLoader
{
    /// <summary>
    /// Sums coverage and lifecycle facts across the given leaves (each existing leaf DB opened exactly once).
    /// <paramref name="assignedByDatLine"/> is the catalog's leaf id → assigned derived-artifact-id set (a single
    /// bulk query taken by the caller); a leaf absent from it has no assignments. <paramref name="onLeafLoaded"/>
    /// is invoked after each leaf so a UI can report progress (e.g. "173 / 410").
    /// </summary>
    public static GroupStorageFacts Compute(
        IReadOnlyList<(string DatLineId, string DataStorePath, int ReleaseCount)> leaves,
        string                                                                    dataDir,
        IReadOnlyDictionary<string, HashSet<string>>                              assignedByDatLine,
        Action<int>?                                                              onLeafLoaded      = null,
        CancellationToken                                                         cancellationToken = default)
    {
        int present = 0, unwanted = 0, owned = 0, unassigned = 0;
        var empty = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < leaves.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (datLineId, dataStorePath, _) = leaves[i];

            if (dataStorePath.Length > 0)
            {
                var abs = Path.Combine(dataDir, dataStorePath);
                if (File.Exists(abs))
                {
                    var store = new DatLineStore(abs);                       // ONE construction per leaf
                    var c = store.GetAllStatusCounts();
                    present  += c.Present;
                    unwanted += c.Unwanted;

                    var assigned = assignedByDatLine.TryGetValue(datLineId, out var s) ? s : empty;
                    var (totalPresent, un) = store.GetPresentDerivedAssignmentStats(assigned);
                    owned      += totalPresent;
                    unassigned += un;
                }
            }
            onLeafLoaded?.Invoke(i + 1);
        }
        return new GroupStorageFacts(present, unwanted, owned, unassigned);
    }
}

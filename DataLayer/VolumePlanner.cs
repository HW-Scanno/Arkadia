using System;
using System.Collections.Generic;

namespace Arkadia.Data;

/// <summary>
/// Pure greedy planner that assigns planning candidates to a single volume.
/// Stateless and side-effect free — does not read or write any database.
/// </summary>
public static class VolumePlanner
{
    /// <summary>
    /// Runs a greedy first-fit pass over <paramref name="candidates"/> and
    /// returns a <see cref="PlanningResult"/> with a decision for every candidate.
    /// </summary>
    /// <param name="volumeCapacityBytes">Declared capacity of the target volume.</param>
    /// <param name="volumeActualSizeBytes">Bytes already committed to the volume.</param>
    /// <param name="candidates">
    ///   Ordered list of candidates from <see cref="DatLineStore.GetPlanningCandidates"/>.
    ///   The input order is preserved; no re-sorting is applied.
    /// </param>
    public static PlanningResult Plan(
        long volumeCapacityBytes,
        long volumeActualSizeBytes,
        IReadOnlyList<PlanningCandidate> candidates)
    {
        long remaining = Math.Max(0L, volumeCapacityBytes - volumeActualSizeBytes);
        long plannedBytes = 0L;

        var items = new List<PlanningDecision>(candidates.Count);

        foreach (var c in candidates)
        {
            string decision;
            string reason;

            if (c.IsAlreadyAssignedToAnyVolume)
            {
                decision = "skip";
                reason   = "already assigned";
            }
            else if (!c.IsCompleteInArchive)
            {
                decision = "skip";
                reason   = "archive incomplete";
            }
            else if (c.TotalSizeBytes <= remaining)
            {
                decision      = "include";
                reason        = "fits";
                remaining    -= c.TotalSizeBytes;
                plannedBytes += c.TotalSizeBytes;
            }
            else
            {
                decision = "defer";
                reason   = "capacity exceeded";
            }

            items.Add(new PlanningDecision
            {
                ReleaseId     = c.ReleaseId,
                ReleaseName   = c.ReleaseName,
                TotalSizeBytes = c.TotalSizeBytes,
                DerivedCount  = c.DerivedCount,
                Decision      = decision,
                Reason        = reason,
            });
        }

        long remainingBefore = Math.Max(0L, volumeCapacityBytes - volumeActualSizeBytes);

        return new PlanningResult
        {
            VolumeCapacityBytes          = volumeCapacityBytes,
            VolumeActualSizeBytes        = volumeActualSizeBytes,
            RemainingBytesBeforePlanning = remainingBefore,
            PlannedBytes                 = plannedBytes,
            RemainingBytesAfterPlanning  = remaining,
            Items                        = items,
        };
    }
}

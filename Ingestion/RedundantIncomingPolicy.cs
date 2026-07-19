using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>Where a release's derived artifact was found (or not) during the pre-staging scan.</summary>
internal enum ReleaseSatisfaction
{
    /// <summary>No durable copy found — the incoming files may be needed; stage normally.</summary>
    NotSatisfied,
    /// <summary>A derived artifact physically exists in the local archive.</summary>
    LocalArchive,
    /// <summary>A derived artifact is on an assigned volume that is reachable and holds the file.</summary>
    ReachableVolume,
    /// <summary>A volume assignment exists, but the volume is unreachable or the file is absent —
    /// the release cannot be confirmed satisfied, and DB must not be trusted as filesystem reality.</summary>
    AssignedVolumeUnavailable,
}

/// <summary>Result of probing one assigned-volume location for an artifact file.</summary>
internal enum VolumeProbeResult { Unreachable, FilePresent, FileMissing }

/// <summary>One volume assignment for a derived artifact: the flat file lives at
/// <c>&lt;resolved volume root&gt;\&lt;FileName&gt;</c>.</summary>
internal readonly record struct VolumeAssignmentRef(string VolumeLabel, string? DiskId, string FileName);

/// <summary>A release's derived artifact and every durable location it may live in.</summary>
internal readonly record struct ArtifactAvailability(
    string RelativePath,
    IReadOnlyList<VolumeAssignmentRef> VolumeAssignments);

/// <summary>
/// Pre-staging guard: decides whether a release is already satisfied by a durable
/// Arkadia-managed copy, so re-ingesting its constituent source files (e.g. a lone
/// <c>.cue</c> for a CD release whose CHD already exists) is REDUNDANT and must not
/// create a new staging folder.
///
/// A release is considered satisfied only when its status is "present" AND at least one
/// derived artifact is found in a durable location:
///   • local archive (<c>derived_artifacts.relative_path</c>), or
///   • an assigned volume that is reachable and actually holds the file.
///
/// If the ONLY location is an assigned volume that is unreachable (or the file is
/// missing there), the result is <see cref="ReleaseSatisfaction.AssignedVolumeUnavailable"/>:
/// the incoming file must NOT create staging clutter, but must NOT be deleted either
/// (DB is not trusted as filesystem reality) — it is quarantined to incoming-skip.
///
/// This is deliberately lighter than <see cref="DerivedArtifactSatisfactionChecker"/>
/// (existence only, no hashing): it only prevents staging clutter. The authoritative
/// per-release transform/skip decision still runs later in Phase 7.
/// </summary>
internal static class RedundantIncomingPolicy
{
    /// <summary>
    /// Locates the strongest durable satisfaction for a release across its artifacts.
    /// Local archive wins over volume; a reachable volume wins over an unavailable one.
    /// </summary>
    /// <param name="releaseStatus">Release.Status as loaded from the DB at run start.</param>
    /// <param name="artifacts">Derived artifacts for the release with their durable locations.</param>
    /// <param name="localExists">Existence probe for a local relative_path (file OR folder).</param>
    /// <param name="volumeProbe">Probe for one assigned-volume location.</param>
    internal static ReleaseSatisfaction Locate(
        string releaseStatus,
        IReadOnlyList<ArtifactAvailability> artifacts,
        Func<string, bool> localExists,
        Func<VolumeAssignmentRef, VolumeProbeResult> volumeProbe)
    {
        if (releaseStatus != "present")
            return ReleaseSatisfaction.NotSatisfied;

        bool anyReachableVolume     = false;
        bool anyAssignedUnavailable = false;

        foreach (var art in artifacts)
        {
            if (!string.IsNullOrEmpty(art.RelativePath) && localExists(art.RelativePath))
                return ReleaseSatisfaction.LocalArchive;   // strongest — short-circuit

            foreach (var v in art.VolumeAssignments)
            {
                switch (volumeProbe(v))
                {
                    case VolumeProbeResult.FilePresent: anyReachableVolume     = true; break;
                    case VolumeProbeResult.Unreachable:
                    case VolumeProbeResult.FileMissing: anyAssignedUnavailable = true; break;
                }
            }
        }

        if (anyReachableVolume)     return ReleaseSatisfaction.ReachableVolume;
        if (anyAssignedUnavailable) return ReleaseSatisfaction.AssignedVolumeUnavailable;
        return ReleaseSatisfaction.NotSatisfied;
    }

    /// <summary>
    /// Local-archive-only convenience: true when a 'present' release has a derived artifact
    /// physically present in the local archive. Retained for the existing callers/tests;
    /// volume-aware callers use <see cref="Locate"/>.
    /// </summary>
    internal static bool IsReleaseAlreadyComplete(
        string releaseStatus,
        IReadOnlyList<string> artifactRelativePaths,
        Func<string, bool> physicalExists)
    {
        var artifacts = artifactRelativePaths
            .Select(rp => new ArtifactAvailability(rp, Array.Empty<VolumeAssignmentRef>()))
            .ToList();
        return Locate(releaseStatus, artifacts, physicalExists, _ => VolumeProbeResult.Unreachable)
               == ReleaseSatisfaction.LocalArchive;
    }
}

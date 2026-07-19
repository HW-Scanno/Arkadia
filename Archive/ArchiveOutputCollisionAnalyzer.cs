using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Archive;

/// <summary>
/// Detects ambiguous DAT-line archive plans: two or more WANTED releases that map
/// to the same top-level archive entry (flat filename for SingleFileFlat, release
/// folder name for MultiFileReleaseFolder). Pure/DB-free.
///
/// Common inner filenames (e.g. <c>track01.bin</c>) inside distinct release folders
/// are NOT collisions — the release folder isolates them, so grouping is by the
/// top-level entry name only, never by inner filenames.
///
/// A collision means the plan is ambiguous; the only allowed resolutions (a later
/// phase) are Exclude A / Exclude B / Abort. This analyzer only produces the model.
/// </summary>
public static class ArchiveOutputCollisionAnalyzer
{
    /// <summary>Collisions over the current WANTED subset (unwanted releases excluded).</summary>
    public static IReadOnlyList<ArchiveOutputCollisionGroup> Analyze(
        IReadOnlyList<ArchiveOutputCandidate> candidates)
        => Group(candidates.Where(c => !ArchiveDatLineOutputFormResolver.IsUnwanted(c.Status)));

    /// <summary>
    /// Collisions over the FULL release set — status-agnostic (every release counts,
    /// even unwanted). Used to decide whether a DAT line is collision-free for its
    /// full set (ValidFullSet) versus valid only because of exclusions.
    /// </summary>
    public static IReadOnlyList<ArchiveOutputCollisionGroup> AnalyzeFullSet(
        IReadOnlyList<ArchiveOutputCandidate> candidates)
        => Group(candidates);

    private static IReadOnlyList<ArchiveOutputCollisionGroup> Group(
        IEnumerable<ArchiveOutputCandidate> candidates)
    {
        return candidates
            .Where(c => c.ArchiveEntryName.Length > 0)
            .GroupBy(c => c.ArchiveEntryName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => new ArchiveOutputCollisionGroup(g.Key, g.ToList()))
            .ToList();
    }
}

using System;
using System.Collections.Generic;

namespace Arkadia.Archive;

/// <summary>Two colliding candidates shown side-by-side, plus group context.</summary>
public sealed record ArchiveCollisionPair(
    string ArchiveEntryName,
    int GroupSize,
    ArchiveOutputCandidate A,
    ArchiveOutputCandidate B);

/// <summary>
/// Production view-model / session driving the archive collision review. Holds all
/// decision logic (which pair to show, exclude A/B, iterative resolution, revalidate)
/// so the AXAML dialog stays a thin shell and the behavior is fully unit-tested.
///
/// It is DB-agnostic: the caller supplies a reload delegate (returns the current
/// release set, reflecting any exclusions applied) and an exclude delegate (marks a
/// release unwanted via existing curation — never deletes files). After each
/// exclusion the plan is re-validated against the reloaded set.
/// </summary>
public sealed class ArchiveCollisionReviewSession
{
    private readonly ArchiveOutputConfig _config;
    private readonly Func<IReadOnlyList<ArchiveReleaseInput>> _loadReleases;
    private readonly Action<string> _excludeRelease;
    private readonly List<(string ReleaseId, string OriginalStatus)> _excluded = new();

    private ArchiveOutputValidationResult _result;

    public ArchiveCollisionReviewSession(
        ArchiveOutputConfig config,
        Func<IReadOnlyList<ArchiveReleaseInput>> loadReleases,
        Action<string> excludeRelease)
    {
        _config         = config;
        _loadReleases   = loadReleases;
        _excludeRelease = excludeRelease;
        _result         = ArchiveOutputValidator.Validate(config, loadReleases());
    }

    public ArchiveOutputValidationResult Result => _result;
    public ArchiveOutputValidationState State   => _result.State;
    public ArchiveDatLineOutputForm Form         => _result.Form;

    /// <summary>True while the current wanted subset still has an unresolved collision.</summary>
    public bool HasUnresolvedCollision => _result.WantedSubsetCollisions.Count > 0;

    /// <summary>
    /// The first unresolved collision group's first two candidates (A, B), or null
    /// when nothing is left to resolve. For 3+ way groups this returns the next two;
    /// after an exclusion the group is recomputed, so review proceeds iteratively.
    /// </summary>
    public ArchiveCollisionPair? CurrentPair()
    {
        if (_result.WantedSubsetCollisions.Count == 0) return null;
        var g = _result.WantedSubsetCollisions[0];   // groups always have >= 2 candidates
        return new ArchiveCollisionPair(g.ArchiveEntryName, g.Candidates.Count, g.Candidates[0], g.Candidates[1]);
    }

    /// <summary>
    /// Releases this session marked unwanted (in order), with the status each had
    /// before exclusion. The caller uses this to roll exclusions back on Abort so a
    /// cancelled review leaves no release excluded.
    /// </summary>
    public IReadOnlyList<(string ReleaseId, string OriginalStatus)> ExcludedReleases => _excluded;

    /// <summary>Excludes candidate A of the current pair (mark unwanted), then re-validates.</summary>
    public void ExcludeA()
    {
        var pair = CurrentPair() ?? throw new InvalidOperationException("No collision to resolve.");
        Exclude(pair.A.ReleaseId, pair.A.Status);
    }

    /// <summary>Excludes candidate B of the current pair (mark unwanted), then re-validates.</summary>
    public void ExcludeB()
    {
        var pair = CurrentPair() ?? throw new InvalidOperationException("No collision to resolve.");
        Exclude(pair.B.ReleaseId, pair.B.Status);
    }

    /// <summary>Abort: applies no pending exclusion and changes nothing. Provided for API clarity.</summary>
    public void Abort() { /* intentionally no-op — caller discards the session and rolls back ExcludedReleases */ }

    private void Exclude(string releaseId, string originalStatus)
    {
        _excludeRelease(releaseId);                                        // persist unwanted (no file deletion)
        _excluded.Add((releaseId, originalStatus));
        _result = ArchiveOutputValidator.Validate(_config, _loadReleases()); // reload + re-validate
    }
}

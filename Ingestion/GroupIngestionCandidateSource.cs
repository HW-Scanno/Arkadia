using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>
/// Builds the Group execution candidate set from a real incoming tree, with the preservation-critical archive
/// rule baked in. For <c>archives_pre_extraction</c> the candidate list is frozen AFTER pre-extraction and
/// excludes EVERY container-extension file — whether it was successfully extracted (its members are the
/// candidates), failed/skipped (kept untouched in incoming for retry), or a nested container surfaced inside an
/// extraction folder (kept for a later run). An uninspected container is therefore never hashed into a false
/// zero-match. For <c>all_files</c> no pre-extraction applies and archives are ordinary candidates. Pure
/// filesystem helpers — no DB, no mutation.
/// </summary>
public static class GroupIngestionCandidateSource
{
    /// <summary>
    /// Every container (<c>.zip/.7z/.rar</c>) currently in the incoming tree — the set <c>RunPreIngest</c> may
    /// attempt this run. Snapshot this BEFORE pre-extraction to report successful vs failed/skipped.
    /// Deterministic Ordinal order.
    /// </summary>
    public static List<string> SnapshotContainers(string incomingDir)
    {
        if (!Directory.Exists(incomingDir)) return new List<string>();
        return Directory.GetFiles(incomingDir, "*", SearchOption.AllDirectories)
            .Where(ArchiveContainerExtensions.IsContainer)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The frozen candidate list. Call AFTER <c>RunPreIngest</c> so extracted members exist. In
    /// <c>archives_pre_extraction</c> every container-extension file is excluded; in <c>all_files</c> all files
    /// are included. Deterministic Ordinal order (the executor re-sorts, but determinism here keeps tests simple).
    /// </summary>
    public static List<string> EnumerateCandidates(string incomingDir, string? fileHandling)
    {
        if (!Directory.Exists(incomingDir)) return new List<string>();
        var all = Directory.GetFiles(incomingDir, "*", SearchOption.AllDirectories).AsEnumerable();

        if (string.Equals(fileHandling, "archives_pre_extraction", StringComparison.Ordinal))
            all = all.Where(f => !ArchiveContainerExtensions.IsContainer(f));

        return all.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Splits the attempted containers into those successfully extracted (present in
    /// <paramref name="extracted"/> by full path) and those that failed/were skipped (the remainder — kept in
    /// incoming, never passed to the executor, never moved to incoming-skip). Deterministic Ordinal order.
    /// </summary>
    public static (List<string> Successful, List<string> FailedOrSkipped) ClassifyContainers(
        IReadOnlyList<string> attempted, IEnumerable<ExtractedArchiveInfo> extracted)
    {
        var success = new HashSet<string>(
            extracted.Select(a => Path.GetFullPath(a.ArchivePath)), StringComparer.OrdinalIgnoreCase);

        var successful      = new List<string>();
        var failedOrSkipped = new List<string>();
        foreach (var c in attempted.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (success.Contains(Path.GetFullPath(c))) successful.Add(c);
            else                                       failedOrSkipped.Add(c);
        }
        return (successful, failedOrSkipped);
    }
}

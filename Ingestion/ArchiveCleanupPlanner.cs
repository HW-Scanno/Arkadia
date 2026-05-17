using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>
/// Decides, per extracted archive, whether it is safe to delete after an ingestion run.
/// Isolated from the UI so the logic is unit-testable without real ZIP extraction.
/// </summary>
internal static class ArchiveCleanupPlanner
{
    internal sealed record ArchiveDecision(
        ExtractedArchiveInfo Archive,
        bool                 ShouldDelete,
        string               Reason);

    /// <summary>
    /// Returns one <see cref="ArchiveDecision"/> per archive.
    /// </summary>
    /// <param name="archives">All archives extracted during pre-ingest.</param>
    /// <param name="archiveTouchedReleaseIds">
    /// Map: archive path → set of release IDs that any file from that archive
    /// participated in (matched and copied/satisfied in the DAT pipeline).
    /// </param>
    /// <param name="successfulReleaseIds">
    /// Release IDs that either completed successfully in this run OR were already
    /// present before this run started.
    /// </param>
    /// <param name="incompleteReleaseIds">
    /// Releases that failed the completeness check (e.g. missing .bin companion).
    /// </param>
    /// <param name="transformFailedReleaseIds">
    /// Releases where at least one transform step failed.
    /// </param>
    /// <param name="unmatchedExtractedFiles">
    /// Full paths of extracted files that matched no DAT release.
    /// An archive that produced any such file is preserved.
    /// </param>
    internal static IReadOnlyList<ArchiveDecision> Plan(
        IEnumerable<ExtractedArchiveInfo>            archives,
        IReadOnlyDictionary<string, HashSet<string>> archiveTouchedReleaseIds,
        IReadOnlySet<string>                         successfulReleaseIds,
        IReadOnlySet<string>                         incompleteReleaseIds,
        IReadOnlySet<string>                         transformFailedReleaseIds,
        IReadOnlySet<string>                         unmatchedExtractedFiles)
    {
        var decisions = new List<ArchiveDecision>();

        foreach (var archive in archives)
        {
            // Any extracted file that didn't match the DAT → preserve for manual recovery.
            if (archive.ExtractedFiles.Any(f => unmatchedExtractedFiles.Contains(f)))
            {
                decisions.Add(new(archive, false, "unmatched extracted files"));
                continue;
            }

            // Archive produced no files that mapped to any release → preserve.
            if (!archiveTouchedReleaseIds.TryGetValue(archive.ArchivePath, out var touched)
                || touched.Count == 0)
            {
                decisions.Add(new(archive, false, "no matched release"));
                continue;
            }

            // Any touched release is incomplete → preserve so missing files can be recovered.
            if (touched.Any(id => incompleteReleaseIds.Contains(id)))
            {
                decisions.Add(new(archive, false, "release incomplete"));
                continue;
            }

            // Any touched release had a transform failure → preserve for retry.
            if (touched.Any(id => transformFailedReleaseIds.Contains(id)))
            {
                decisions.Add(new(archive, false, "transform failed"));
                continue;
            }

            // Every touched release must be confirmed successful before we delete.
            if (!touched.All(id => successfulReleaseIds.Contains(id)))
            {
                decisions.Add(new(archive, false, "release outcome unknown"));
                continue;
            }

            decisions.Add(new(archive, true, "all releases succeeded"));
        }

        return decisions;
    }
}

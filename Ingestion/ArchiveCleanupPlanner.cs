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
    /// Cleanup depends on extraction success only: every archive in <paramref name="archives"/>
    /// was successfully extracted, so the container has served its purpose and is always deleted.
    /// Child-file outcomes (unwanted, already-present, incomplete, transform failure) do not
    /// affect container cleanup.
    /// </summary>
    /// <param name="archives">Archives that were successfully extracted during pre-ingest.</param>
    /// <param name="archiveTouchedReleaseIds">Unused — retained for call-site compatibility.</param>
    /// <param name="successfulReleaseIds">Unused — retained for call-site compatibility.</param>
    /// <param name="incompleteReleaseIds">Unused — retained for call-site compatibility.</param>
    /// <param name="transformFailedReleaseIds">Unused — retained for call-site compatibility.</param>
    /// <param name="unmatchedExtractedFiles">Unused — retained for call-site compatibility.</param>
    internal static IReadOnlyList<ArchiveDecision> Plan(
        IEnumerable<ExtractedArchiveInfo>            archives,
        IReadOnlyDictionary<string, HashSet<string>> archiveTouchedReleaseIds,
        IReadOnlySet<string>                         successfulReleaseIds,
        IReadOnlySet<string>                         incompleteReleaseIds,
        IReadOnlySet<string>                         transformFailedReleaseIds,
        IReadOnlySet<string>                         unmatchedExtractedFiles)
    {
        return archives
            .Select(a => new ArchiveDecision(a, true, "extraction succeeded"))
            .ToList();
    }
}

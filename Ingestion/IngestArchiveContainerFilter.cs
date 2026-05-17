using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>
/// Tracks successfully extracted archive containers so they are excluded from
/// the normal scan → hash → match → skip pipeline.  The archive lifecycle is
/// owned entirely by Phase 9 (deferred archive cleanup).
/// </summary>
internal static class IngestArchiveContainerFilter
{
    /// <summary>
    /// Builds a full-path, case-insensitive set from the list of archive paths
    /// that were successfully extracted during pre-ingest.
    /// </summary>
    internal static HashSet<string> BuildExtractedSet(IEnumerable<ExtractedArchiveInfo> archives) =>
        new(archives.Select(a => Path.GetFullPath(a.ArchivePath)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if <paramref name="filePath"/> is a successfully extracted
    /// archive container that must not enter the scan / hash / skip pipeline.
    /// </summary>
    internal static bool IsExtractedArchive(string filePath, HashSet<string> extractedSet) =>
        extractedSet.Contains(Path.GetFullPath(filePath));
}

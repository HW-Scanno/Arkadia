using System;
using System.Collections.Generic;
using System.IO;

namespace Arkadia.Ingestion;

/// <summary>Outcome of the Group successful-container cleanup pass.</summary>
public sealed class GroupContainerCleanupResult
{
    public int Deleted { get; set; }
    public int Errors  { get; set; }
    public List<IngestionOperation> Operations { get; } = new();
}

/// <summary>
/// Deletes the ORIGINAL archive containers that were successfully extracted this run, reusing
/// <see cref="ArchiveCleanupPlanner"/> (extraction success is the only criterion) with the same best-effort/
/// logging semantics as Single Phase 9. Failed/skipped containers are absent from
/// <see cref="ExtractedArchiveInfo"/> and therefore left untouched. It never deletes an arbitrary container by
/// extension, and it is independent of member/finalization/transform outcome — retry material always survives
/// as loose extracted members or incoming-skip entries.
///
/// The CALLER must only invoke this after a NON-aborted executor run (an aborted run leaves both container and
/// members in place — see M9C2B §22).
/// </summary>
public static class GroupContainerCleanup
{
    public static GroupContainerCleanupResult Run(
        IReadOnlyList<ExtractedArchiveInfo>  extracted,
        string                               incomingDir,
        IProgress<IngestionProgress>?        progress = null)
    {
        var result = new GroupContainerCleanupResult();
        if (extracted.Count == 0) return result;

        var incomingLabel = Path.GetFileName(Path.GetDirectoryName(incomingDir)) ?? "incoming";

        var decisions = ArchiveCleanupPlanner.Plan(
            extracted,
            new Dictionary<string, HashSet<string>>(),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>());

        foreach (var decision in decisions)
        {
            if (!decision.ShouldDelete) continue;   // planner deletes every successfully-extracted container
            var name = Path.GetFileName(decision.Archive.ArchivePath);
            try
            {
                File.Delete(decision.Archive.ArchivePath);
                result.Deleted++;
                var op = new IngestionOperation(name, "archive-deleted", incomingLabel);
                result.Operations.Add(op);
                progress?.Report(new IngestionProgress { NewOperation = op });
            }
            catch
            {
                result.Errors++;
                var op = new IngestionOperation(name, "archive-delete-failed", "could not remove archive");
                result.Operations.Add(op);
                progress?.Report(new IngestionProgress { NewOperation = op });
            }
        }
        return result;
    }
}

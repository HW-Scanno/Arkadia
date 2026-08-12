using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// Per-incoming-candidate staging outcome (the "assimilation ledger"). Describes ONLY the staging result —
/// deliberately independent of release completeness / promotion / transform (those are Phase 7). A future
/// Group cleanup will use it: hashed-ok + <see cref="MatchCount"/> 0 ⇒ skip; ≥1 match with all required
/// targets secured ⇒ safely assimilated (incoming may be removed); any required target failed ⇒ keep.
/// </summary>
public sealed class IncomingCandidateResult
{
    public required string       SourcePath          { get; init; }
    public          bool         HashSucceeded       { get; init; } = true;   // staging entries are matched ⇒ hashed ok
    public          int          MatchCount          { get; set; }             // total matched targets for this source
    public          int          RequiredTargets     { get; set; }             // stageable + wanted targets attempted
    public          int          SecuredTargets      { get; set; }             // successfully staged
    public          List<string> FailedTargets       { get; } = new();         // romNames that failed to stage
    public          bool         AllTargetsSatisfied { get; set; }
    public          bool         AllTargetsUnwanted  { get; set; }
    public          bool         Moved               { get; set; }             // sole target moved out of incoming
}

/// <summary>The classification sets + ledger produced by <see cref="IngestionStagingEngine.StageTargets"/>.</summary>
public sealed class StagingResult
{
    public HashSet<string> SuccessfullyCopied      { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MovedFromIncoming       { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllTargetsSatisfied     { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllTargetsUnwanted      { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AffectedReleaseIds      { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UnwantedSkippedReleases { get; } = new(StringComparer.Ordinal);
    public List<IncomingCandidateResult> Ledger    { get; } = new();
}

/// <summary>
/// Phase 6 (incoming → staging), extracted verbatim so both the Single-DAT run and a future Group run can
/// reuse it. Behavior is preserved exactly for Single (<paramref name="allowMove"/> = true keeps the
/// sole-target same-volume move optimization). Group passes <c>allowMove: false</c> to always copy, so the
/// incoming original survives until a final cross-leaf cleanup pass. Mutates <paramref name="satisfiedTargets"/>
/// (marks each staged target) and <paramref name="result"/> (FilesCopied, Operations) and reports progress —
/// identically to the historical inline loop.
/// </summary>
public static class IngestionStagingEngine
{
    public static StagingResult StageTargets(
        IReadOnlyDictionary<string, List<(string ReleaseId, string RomName)>> copyPlan,
        IReadOnlyDictionary<string, ReleaseRecord>                            releases,
        HashSet<string>                                                       satisfiedTargets,
        string                                                                stagingRoot,
        string                                                                platformId,
        string                                                                datLineId,
        Func<string, string>                                                  safeFileName,
        IngestionResult                                                       result,
        IProgress<IngestionProgress>                                          progress,
        bool                                                                  allowMove = true)
    {
        bool IsStageable(string releaseId, string romName) => !satisfiedTargets.Contains($"{releaseId}|{romName}");

        // copyTotal counts only stageable targets (not satisfied, not volume-unavailable).
        int copyTotal = copyPlan.Values.Sum(
            dests => dests.Count(d => IsStageable(d.ReleaseId, d.RomName)));

        progress.Report(new IngestionProgress
        {
            PhaseText       = "Copying to staging…",
            IsIndeterminate = false,
            Total           = copyTotal > 0 ? copyTotal : 1,
            Processed       = 0,
        });

        var res      = new StagingResult();
        int copyCount = 0;

        foreach (var (srcPath, destinations) in copyPlan)
        {
            var srcInfo = new FileInfo(srcPath);
            var ledger  = new IncomingCandidateResult { SourcePath = srcPath, MatchCount = destinations.Count };
            res.Ledger.Add(ledger);

            // Filter to stageable targets (not already satisfied).
            var pending = destinations
                .Where(d => IsStageable(d.ReleaseId, d.RomName))
                .ToList();

            if (pending.Count == 0)
            {
                // No stageable target → every target was already satisfied → duplicate.
                res.AllTargetsSatisfied.Add(srcPath);
                ledger.AllTargetsSatisfied = true;
                continue;
            }

            // UNWANTED WINS: split pending into wanted vs unwanted targets.
            var wantedPending = pending
                .Where(d => !releases.TryGetValue(d.ReleaseId, out var r) || r.Status != "unwanted")
                .ToList();

            // Log per-release unwanted-skipped for any unwanted targets.
            var seenUnwantedLog = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (releaseId, _) in pending)
            {
                if (!seenUnwantedLog.Add(releaseId)) continue;
                if (!releases.TryGetValue(releaseId, out var rel) || rel.Status != "unwanted") continue;
                res.UnwantedSkippedReleases.Add(releaseId);
                // Phase 6 classification only — the physical move to incoming-skip
                // (and the UnwantedSkipped count) happens once in Phase 8 as "unwanted-moved".
                var skipOp = new IngestionOperation(srcInfo.Name, "unwanted-classified", rel.Name);
                result.Operations.Add(skipOp);
                progress.Report(new IngestionProgress { NewOperation = skipOp });
            }

            if (wantedPending.Count == 0)
            {
                // All matched targets are unwanted — defer to Phase 8 for incoming-skip move.
                res.AllTargetsUnwanted.Add(srcPath);
                ledger.AllTargetsUnwanted = true;
                continue;
            }

            ledger.RequiredTargets = wantedPending.Count;
            bool anyFailed           = false;
            bool wasMovedFromIncoming = false;

            foreach (var (releaseId, romName) in wantedPending)
            {
                var relName    = releases.TryGetValue(releaseId, out var rel) ? rel.Name : releaseId;
                var safeFolder = safeFileName(relName);
                var stagingDir = Path.Combine(stagingRoot, safeFolder);
                var destPath   = Path.Combine(stagingDir, romName);

                Directory.CreateDirectory(stagingDir);

                try
                {
                    // Move when this is the sole remaining target and paths share a volume
                    // (same-volume File.Move is an atomic NTFS rename — no byte copy).
                    // Copy otherwise (fan-out to multiple releases, or cross-volume).
                    StagingHelpers.StageFile(srcPath, destPath, wantedPending.Count, out var stageOp, allowMove);

                    // Size sanity check only for copies (moves are atomic and integrity-guaranteed).
                    if (stageOp != "stage-moved" && new FileInfo(destPath).Length != srcInfo.Length)
                        throw new IOException($"Size mismatch after copy for {romName}");

                    if (stageOp == "stage-moved") wasMovedFromIncoming = true;

                    // Mark this target satisfied so no later file re-copies it.
                    satisfiedTargets.Add($"{releaseId}|{romName}");
                    res.AffectedReleaseIds.Add(releaseId);
                    ledger.SecuredTargets++;
                    result.FilesCopied++;
                    copyCount++;

                    var op = new IngestionOperation(
                        srcInfo.Name, stageOp,
                        $"staging/{platformId}/{datLineId}/{safeFolder}/{romName}");
                    result.Operations.Add(op);

                    if (copyCount % 25 == 0 || copyCount == copyTotal)
                        progress.Report(new IngestionProgress
                        {
                            PhaseText       = "Copying to staging…",
                            IsIndeterminate = false,
                            Total           = copyTotal > 0 ? copyTotal : 1,
                            Processed       = copyCount,
                            Accepted        = result.FilesMatched,
                            Rejected        = 0,
                            NewOperation    = op,
                        });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    anyFailed = true;
                    ledger.FailedTargets.Add(romName);
                    var op = new IngestionOperation(srcInfo.Name, "copy-failed", ex.Message);
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
            }

            ledger.Moved = wasMovedFromIncoming;
            if (!anyFailed)
            {
                if (wasMovedFromIncoming)
                    res.MovedFromIncoming.Add(srcPath);
                else
                    res.SuccessfullyCopied.Add(srcPath);
            }
        }

        return res;
    }
}

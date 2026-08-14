using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>Progress tick for Group ingestion execution.</summary>
public sealed record GroupIngestionProgress(string PhaseText, int Index, int Total);

/// <summary>Final disposition of an incoming candidate after the single aggregate cleanup pass.</summary>
public enum GroupCandidateDisposition { Pending, Kept, Deleted, Skipped }

/// <summary>
/// Per-candidate assimilation record. RequiredTargets are the aggregate-plan materializations this candidate
/// matched — never theoretical DAT matches. Fully-secured (all required targets staged, none failed) makes
/// the incoming eligible for deletion regardless of release completeness / promotion / transform outcome.
/// </summary>
public sealed class GroupCandidateOutcome
{
    public required string       SourcePath      { get; init; }
    public required bool         HashSucceeded   { get; init; }
    public          string?      HashError       { get; set; }
    public          int          MatchCount      { get; set; }   // aggregate required-target matches
    public          int          RequiredTargets { get; set; }   // == MatchCount (aggregate = required-only)
    public          int          SecuredTargets  { get; set; }
    public          List<string> FailedTargets   { get; } = new();
    public          GroupCandidateDisposition Disposition { get; set; } = GroupCandidateDisposition.Pending;

    public bool ZeroMatch => HashSucceeded && MatchCount == 0;

    /// <summary>
    /// Authoritative safe-assimilation predicate. Requires POSITIVE proof that every required
    /// materialization succeeded — <see cref="SecuredTargets"/> must equal <see cref="RequiredTargets"/> —
    /// not merely the absence of a recorded failure. (required=3, secured=2, failed=0 ⇒ false.) Only a
    /// fully-secured candidate may have its incoming original deleted.
    /// </summary>
    public bool FullySecured =>
        RequiredTargets > 0 && SecuredTargets == RequiredTargets && FailedTargets.Count == 0;
}

/// <summary>Compact, testable result of a Group ingestion execution.</summary>
public sealed class GroupIngestionExecutionResult
{
    public int CandidatesScanned  { get; set; }
    public int HashFailures       { get; set; }
    public int ZeroMatches        { get; set; }
    public int MatchedCandidates  { get; set; }

    public int RequiredTargets    { get; set; }
    public int SecuredTargets     { get; set; }
    public int FailedTargets      { get; set; }

    public int LeavesFinalizedAttempted { get; set; }
    public int FinalizationErrorCount   { get; set; }
    public List<(string DatLineId, string Reason)> FinalizationErrors { get; } = new();

    public int IncomingDeleted { get; set; }
    public int IncomingKept    { get; set; }
    public int IncomingSkipped { get; set; }
    public int CleanupErrors   { get; set; }

    public bool    Aborted     { get; set; }
    public string? AbortReason { get; set; }

    public List<GroupCandidateOutcome> Candidates { get; } = new();
    public List<string>                Operations { get; } = new();
}

/// <summary>
/// Finalizes one leaf. In production this binds to <c>FinalizeConfiguredLeaf</c> (M9C2B); tests pass a
/// controlled fake. May throw to signal a leaf finalization failure (recorded, does not stop other leaves).
/// </summary>
public delegate void GroupLeafFinalizer(
    string                     datLineId,
    IReadOnlyCollection<string> affectedReleaseIds,
    IReadOnlyCollection<string> resumableReleaseIds);

/// <summary>
/// The deterministic, sequential Group ingestion execution core. Given a validated
/// <see cref="GroupIngestionPlan"/> and an already-ready candidate set, it: scans/hashes each candidate ONCE
/// against the plan's aggregate indexes; preflights aggregate staging space; force-copies (allowMove:false)
/// every required target across all leaves BEFORE any finalization; finalizes affected ∪ resumable leaves via
/// a delegate; then runs ONE aggregate cleanup pass. It never pre-extracts, never deletes staging/source/
/// artifacts, and — per fixed PO semantics — bases incoming disposition on safe STAGING materialization, not
/// on completeness/promotion/transform. Reuses <see cref="IngestionCandidateMatcher"/>,
/// <see cref="IngestionSpacePreflight"/>, and <see cref="IngestionStagingEngine"/> unchanged. Not wired to the
/// runtime UI (M9C2B does that).
/// </summary>
public static class GroupIngestionExecutor
{
    private sealed class NoOpProgress<T> : IProgress<T> { public void Report(T value) { } }

    public static GroupIngestionExecutionResult Execute(
        GroupIngestionPlan                   plan,
        IReadOnlyList<string>                candidatePaths,
        string                               appRoot,
        GroupLeafFinalizer                   finalizeLeaf,
        IProgress<GroupIngestionProgress>?   progress            = null,
        Func<string, long>?                  availableSpaceProbe = null)
    {
        var result     = new GroupIngestionExecutionResult();
        var platformId = plan.PlatformId;

        // (DatLineId, ReleaseId) → release name, so staging folders match the frozen plan without re-opening DBs.
        var nameByRelease = new Dictionary<(string, string), string>();
        foreach (var rt in plan.RequiredTargets)
            nameByRelease[(rt.Target.DatLineId, rt.Target.ReleaseId)] = rt.ReleaseName;

        // ── 1. Scan / hash ONCE against the Group aggregate indexes ────────────
        var candidates          = candidatePaths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var outcomes            = new List<GroupCandidateOutcome>(candidates.Count);
        var matchesByCandidate  = new Dictionary<string, List<GroupMatchTarget>>(StringComparer.Ordinal);

        for (int i = 0; i < candidates.Count; i++)
        {
            var path = candidates[i];
            progress?.Report(new GroupIngestionProgress("Hashing", i + 1, candidates.Count));

            var m = IngestionCandidateMatcher.HashAndMatch<GroupMatchTarget>(path, plan.AggregateSha1Index, plan.AggregateMd5Index);
            var oc = new GroupCandidateOutcome { SourcePath = path, HashSucceeded = m.HashSucceeded };
            result.CandidatesScanned++;

            if (!m.HashSucceeded)
            {
                oc.HashError = "hash/read failed";   // distinct from zero-match: candidate is KEPT, never skipped
                result.HashFailures++;
            }
            else if (m.Targets.Count == 0)
            {
                result.ZeroMatches++;                // true aggregate zero-match → future skip
            }
            else
            {
                matchesByCandidate[path] = m.Targets.ToList();
                oc.MatchCount = oc.RequiredTargets = m.Targets.Count;
                result.MatchedCandidates++;
            }
            outcomes.Add(oc);
        }

        // Distinct authoritative required targets (a target shared by duplicate candidates counts once).
        var uniqueRequired = new HashSet<(string, string, string)>();
        foreach (var kv in matchesByCandidate)
            foreach (var t in kv.Value)
                uniqueRequired.Add((t.DatLineId, t.ReleaseId, t.RomName));
        result.RequiredTargets = uniqueRequired.Count;

        // ── 2. Aggregate staging-space preflight (force-copy: count every required target) ─────
        long bytesNeeded = IngestionSpacePreflight.BytesNeeded(
            outcomes.Where(o => o.MatchCount > 0).Select(o =>
            {
                long len = 0; try { len = new FileInfo(o.SourcePath).Length; } catch { }
                return (len, o.MatchCount);
            }));
        var stagingBase = Path.Combine(appRoot, "staging", platformId);
        long available = availableSpaceProbe?.Invoke(stagingBase) ?? ProbeDrive(stagingBase);
        if (available < bytesNeeded)
        {
            // No staging, no finalization, no cleanup — nothing mutated.
            result.Aborted     = true;
            result.AbortReason = $"Insufficient disk space. Need {bytesNeeded:N0} bytes, have {available:N0}.";
            result.Candidates.AddRange(outcomes);
            return result;
        }

        // ── 3. Stage ALL required targets across ALL leaves (the safe-assimilation barrier) ────
        var leafOrder = matchesByCandidate.Values.SelectMany(x => x).Select(t => t.DatLineId)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var affectedByLeaf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Per-leaf post-staging satisfied set ("releaseId|romName") — positive proof of what got materialized.
        var securedByLeaf  = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        for (int li = 0; li < leafOrder.Count; li++)
        {
            var datLineId   = leafOrder[li];
            progress?.Report(new GroupIngestionProgress("Staging leaf", li + 1, leafOrder.Count));
            var stagingRoot = Path.Combine(appRoot, "staging", platformId, datLineId);

            var copyPlan = new Dictionary<string, List<(string ReleaseId, string RomName)>>(StringComparer.OrdinalIgnoreCase);
            var releases = new Dictionary<string, ReleaseRecord>(StringComparer.Ordinal);
            foreach (var (srcPath, targets) in matchesByCandidate.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var leafTargets = targets.Where(t => t.DatLineId == datLineId)
                    .OrderBy(t => t.ReleaseId, StringComparer.Ordinal).ThenBy(t => t.RomName, StringComparer.Ordinal)
                    .Select(t => (t.ReleaseId, t.RomName)).ToList();
                if (leafTargets.Count == 0) continue;
                copyPlan[srcPath] = leafTargets;
                foreach (var (rid, _) in leafTargets)
                    if (!releases.ContainsKey(rid))
                        releases[rid] = new ReleaseRecord
                        {
                            Id = rid, DatLineId = datLineId, Status = "missing",
                            Name = nameByRelease.TryGetValue((datLineId, rid), out var n) ? n : rid,
                        };
            }
            if (copyPlan.Count == 0) continue;

            // Force copy — the original candidate must survive until the final cleanup pass. StageTargets
            // adds each successfully-staged target to the satisfied set; that set is the authoritative record
            // of what was materialized in this leaf (a target satisfied by an identical duplicate is in it too).
            var satisfied = new HashSet<string>(StringComparer.Ordinal);
            var staging = IngestionStagingEngine.StageTargets(
                copyPlan, releases, satisfied,
                stagingRoot, platformId, datLineId, IngestionPaths.SafeFolderName,
                new IngestionResult(), new NoOpProgress<IngestionProgress>(), allowMove: false);

            affectedByLeaf[datLineId] = staging.AffectedReleaseIds;
            securedByLeaf[datLineId]  = satisfied;
        }

        // Positive per-candidate securing proof: a matched target is secured iff it is present in its leaf's
        // post-staging satisfied set (staged by this candidate OR an identical duplicate). Any matched target
        // absent from that set is an unproven/failed materialization — recorded so FullySecured is false and
        // the candidate's incoming is KEPT. FailedTargets ≡ RequiredTargets − SecuredTargets by construction,
        // so there is no separate weaker delete condition.
        foreach (var oc in outcomes)
        {
            if (!matchesByCandidate.TryGetValue(oc.SourcePath, out var targets)) continue;
            int secured = 0;
            foreach (var t in targets)
            {
                if (securedByLeaf.TryGetValue(t.DatLineId, out var set) && set.Contains($"{t.ReleaseId}|{t.RomName}"))
                    secured++;
                else
                    oc.FailedTargets.Add($"{t.DatLineId}/{t.ReleaseId}/{t.RomName}");
            }
            oc.SecuredTargets = secured;
        }

        result.SecuredTargets = securedByLeaf.Values.Sum(s => s.Count);   // distinct materialized targets
        result.FailedTargets  = result.RequiredTargets - result.SecuredTargets;

        // ── 4. Finalize affected ∪ resumable leaves (only AFTER all staging) ───
        var resumableByLeaf = plan.ResumableReleases
            .GroupBy(r => r.DatLineId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.ReleaseId).OrderBy(x => x, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var finalizeLeaves = affectedByLeaf.Keys.Concat(resumableByLeaf.Keys)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        for (int fi = 0; fi < finalizeLeaves.Count; fi++)
        {
            var datLineId = finalizeLeaves[fi];
            var affected  = (IReadOnlyCollection<string>)(affectedByLeaf.TryGetValue(datLineId, out var a) ? a : new HashSet<string>());
            var resumable = resumableByLeaf.TryGetValue(datLineId, out var r) ? r : Array.Empty<string>();
            if (affected.Count == 0 && resumable.Count == 0) continue;

            progress?.Report(new GroupIngestionProgress("Finalizing leaf", fi + 1, finalizeLeaves.Count));
            result.LeavesFinalizedAttempted++;
            try
            {
                finalizeLeaf(datLineId, affected, resumable);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Leaf-scoped failure — recorded, does not stop other leaves, does NOT affect candidate
                // cleanup (disposition is based on staging security, not finalization).
                result.FinalizationErrorCount++;
                result.FinalizationErrors.Add((datLineId, ex.Message));
            }
        }

        // ── 5. ONE aggregate cleanup pass (never earlier) ──────────────────────
        var skipDir = Path.Combine(appRoot, "incoming-skip", platformId);
        for (int ci = 0; ci < outcomes.Count; ci++)
        {
            var oc = outcomes[ci];
            progress?.Report(new GroupIngestionProgress("Cleaning candidate", ci + 1, outcomes.Count));

            if (!oc.HashSucceeded)                                   // A. hash failed → KEEP (never skip)
            {
                oc.Disposition = GroupCandidateDisposition.Kept; result.IncomingKept++;
            }
            else if (oc.MatchCount == 0)                             // B. aggregate zero-match → incoming-skip
            {
                if (TryMoveToSkip(oc.SourcePath, skipDir))
                { oc.Disposition = GroupCandidateDisposition.Skipped; result.IncomingSkipped++; }
                else
                { oc.Disposition = GroupCandidateDisposition.Kept; result.CleanupErrors++; result.Operations.Add($"skip-failed: {oc.SourcePath}"); }
            }
            else if (oc.FullySecured)                               // C. POSITIVE proof all required secured → DELETE
            {
                try
                {
                    if (File.Exists(oc.SourcePath)) File.Delete(oc.SourcePath);
                    oc.Disposition = GroupCandidateDisposition.Deleted; result.IncomingDeleted++;
                }
                catch
                { oc.Disposition = GroupCandidateDisposition.Kept; result.CleanupErrors++; result.Operations.Add($"delete-failed: {oc.SourcePath}"); }
            }
            else                                                    // D. not fully secured (any target unproven) → KEEP
            {
                oc.Disposition = GroupCandidateDisposition.Kept; result.IncomingKept++;
            }
        }

        result.Candidates.AddRange(outcomes);
        return result;
    }

    private static long ProbeDrive(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch { return long.MaxValue; }   // unknown → proceed (matches Single)
    }

    private static bool TryMoveToSkip(string srcPath, string skipDir)
    {
        try
        {
            Directory.CreateDirectory(skipDir);
            var dest = UniquePath(skipDir, Path.GetFileName(srcPath));
            File.Move(srcPath, dest, overwrite: false);
            return true;
        }
        catch { return false; }
    }

    // Collision-safe destination: "name.ext", then "name (2).ext", … (mirrors incoming-skip naming).
    private static string UniquePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            var c = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(c)) return c;
        }
    }
}

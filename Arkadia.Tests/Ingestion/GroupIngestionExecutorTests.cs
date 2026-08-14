using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Temp-filesystem tests for <see cref="GroupIngestionExecutor"/> — the mutating Group ingestion core.
/// Everything runs against temp incoming / staging / incoming-skip only; no real runtime, no DB, no UI.
/// The finalization step is a controlled fake delegate (no real transform processors).
/// </summary>
public sealed class GroupIngestionExecutorTests : IDisposable
{
    private readonly string _root;
    private const string Platform = "c64";

    public GroupIngestionExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ark-grpexec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Sha1(byte[] b)   => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    private string IncomingDir { get { var d = Path.Combine(_root, "incoming", Platform); Directory.CreateDirectory(d); return d; } }
    private string SkipDir     => Path.Combine(_root, "incoming-skip", Platform);

    private string WriteIncoming(string name, byte[] content)
    {
        var p = Path.Combine(IncomingDir, name);
        File.WriteAllBytes(p, content);
        return p;
    }

    private string StageDest(string leaf, string releaseName, string rom)
        => Path.Combine(_root, "staging", Platform, leaf, IngestionPaths.SafeFolderName(releaseName), rom);

    private static GroupRequiredTarget Req(string leaf, string releaseId, string releaseName, string rom, byte[] content)
        => new(new GroupMatchTarget(leaf, releaseId, rom), releaseName, content.Length, Sha1(content), "");

    private static GroupIngestionPlan BuildPlan(
        IEnumerable<GroupRequiredTarget>     required,
        IEnumerable<GroupResumableRelease>?  resumable = null,
        IEnumerable<string>?                 extraLeaves = null)
    {
        var reqArr = required.ToImmutableArray();
        var sha1 = new Dictionary<string, List<GroupMatchTarget>>();
        var md5  = new Dictionary<string, List<GroupMatchTarget>>();
        foreach (var rt in reqArr)
        {
            if (rt.Sha1.Length > 0)
            {
                if (!sha1.TryGetValue(rt.Sha1, out var l)) { l = new(); sha1[rt.Sha1] = l; }
                if (!l.Contains(rt.Target)) l.Add(rt.Target);   // exact-duplicate collapse (mirrors the planner)
            }
            if (rt.Md5.Length > 0)
            {
                if (!md5.TryGetValue(rt.Md5, out var l)) { l = new(); md5[rt.Md5] = l; }
                if (!l.Contains(rt.Target)) l.Add(rt.Target);
            }
        }

        var res      = (resumable ?? Enumerable.Empty<GroupResumableRelease>()).ToImmutableArray();
        var leaves   = reqArr.Select(r => r.Target.DatLineId)
                             .Concat(res.Select(r => r.DatLineId))
                             .Concat(extraLeaves ?? Enumerable.Empty<string>())
                             .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();

        return new GroupIngestionPlan
        {
            GroupId            = "c64-tosec",
            DisplayName        = "TOSEC",
            PlatformId         = Platform,
            ExpectedLeafIds    = leaves,
            FileHandling       = "archives_pre_extraction",
            PreparedLeaves     = ImmutableArray<GroupPreparedLeaf>.Empty,
            RequiredTargets    = reqArr,
            AggregateSha1Index = sha1,
            AggregateMd5Index  = md5,
            ResumableReleases  = res,
            IncomingInspection = new GroupIncomingInspection(0, 0),
            Readiness          = GroupIngestionReadiness.Ready,
            Diagnostics        = ImmutableArray<GroupLeafDiagnostic>.Empty,
        };
    }

    private static readonly GroupLeafFinalizer NoOpFinalizer = (_, _, _) => { };

    private GroupIngestionExecutionResult Run(
        GroupIngestionPlan plan, IReadOnlyList<string> candidates,
        GroupLeafFinalizer? finalizer = null, Func<string, long>? space = null,
        IProgress<GroupIngestionProgress>? progress = null)
        => GroupIngestionExecutor.Execute(plan, candidates, _root, finalizer ?? NoOpFinalizer, progress, space);

    // ── §30 hashing & fan-out ───────────────────────────────────────────────

    [Fact] // 1 + 27: one candidate hashed once, matches targets in two leaves (distinct)
    public void OneCandidate_MatchesTwoLeaves_BothStagedDistinct()
    {
        var c = Bytes("foo.d64 content");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "relA", "Game A", "foo.d64", c),
            Req("leaf-b", "relB", "Game B", "foo.d64", c),   // same hash → cross-leaf
        });
        var src = WriteIncoming("foo.d64", c);

        var r = Run(plan, new[] { src });

        Assert.Equal(1, r.MatchedCandidates);
        Assert.Equal(2, r.RequiredTargets);
        Assert.Equal(2, r.SecuredTargets);
        Assert.True(File.Exists(StageDest("leaf-a", "Game A", "foo.d64")));
        Assert.True(File.Exists(StageDest("leaf-b", "Game B", "foo.d64")));
    }

    [Fact] // 2: both staging destinations receive the file (content preserved)
    public void OneCandidate_TwoLeaves_BothDestinationsHaveContent()
    {
        var c = Bytes("payload-XYZ");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "r1", "A", "x.rom", c),
            Req("leaf-b", "r2", "B", "x.rom", c),
        });
        var src = WriteIncoming("x.rom", c);

        Run(plan, new[] { src });

        Assert.Equal(c, File.ReadAllBytes(StageDest("leaf-a", "A", "x.rom")));
        Assert.Equal(c, File.ReadAllBytes(StageDest("leaf-b", "B", "x.rom")));
    }

    [Fact] // 3 + 8 (allowMove=false): a single-target candidate is COPIED, so it is still present in
           //     incoming at finalization time (a move would have removed it). Deleted only in cleanup.
    public void SingleTarget_ForceCopy_OriginalPresentAtFinalization()
    {
        var c = Bytes("solo.d64");
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Solo", "solo.d64", c) });
        var src = WriteIncoming("solo.d64", c);

        bool presentAtFinalize = false;
        GroupLeafFinalizer fin = (_, _, _) => presentAtFinalize = File.Exists(src);

        var r = Run(plan, new[] { src }, fin);

        Assert.True(presentAtFinalize);                 // copied (not moved) → survived staging
        Assert.True(File.Exists(StageDest("leaf-a", "Solo", "solo.d64")));
        Assert.False(File.Exists(src));                 // cleanup deleted it (fully secured)
        Assert.Equal(1, r.IncomingDeleted);
    }

    [Fact] // 4: one candidate → multiple releases in the same leaf, all staged
    public void OneCandidate_MultipleReleasesSameLeaf_AllStaged()
    {
        var c = Bytes("shared.d64");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "r10", "Game Ten",    "shared.d64", c),
            Req("leaf-a", "r20", "Game Twenty", "shared.d64", c),
        });
        var src = WriteIncoming("shared.d64", c);

        var r = Run(plan, new[] { src });

        Assert.Equal(2, r.SecuredTargets);
        Assert.True(File.Exists(StageDest("leaf-a", "Game Ten",    "shared.d64")));
        Assert.True(File.Exists(StageDest("leaf-a", "Game Twenty", "shared.d64")));
    }

    [Fact] // 5: same candidate → same leaf (2 releases) + another leaf, each authoritative target staged once
    public void OneCandidate_SameLeafPlusAnotherLeaf_EachTargetStagedOnce()
    {
        var c = Bytes("multi.d64");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "r1", "A One", "multi.d64", c),
            Req("leaf-a", "r2", "A Two", "multi.d64", c),
            Req("leaf-b", "r3", "B One", "multi.d64", c),
        });
        var src = WriteIncoming("multi.d64", c);

        var r = Run(plan, new[] { src });

        Assert.Equal(3, r.SecuredTargets);
        Assert.True(File.Exists(StageDest("leaf-a", "A One", "multi.d64")));
        Assert.True(File.Exists(StageDest("leaf-a", "A Two", "multi.d64")));
        Assert.True(File.Exists(StageDest("leaf-b", "B One", "multi.d64")));
    }

    [Fact] // 6: zero aggregate match → moved to incoming-skip
    public void ZeroAggregateMatch_MovedToSkip()
    {
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "A", "known.d64", Bytes("known")) });
        var src  = WriteIncoming("stranger.d64", Bytes("nobody wants this"));

        var r = Run(plan, new[] { src });

        Assert.Equal(1, r.ZeroMatches);
        Assert.Equal(1, r.IncomingSkipped);
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(SkipDir, "stranger.d64")));
    }

    [Fact] // 7: hash failure → candidate remains in incoming, NOT skipped
    public void HashFailure_RemainsIncoming_NotSkipped()
    {
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "A", "k.d64", Bytes("k")) });
        var src  = WriteIncoming("locked.d64", Bytes("cannot be read"));

        // Exclusive lock → HashAndMatch's File.OpenRead throws → HashSucceeded=false.
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = Run(plan, new[] { src });

            Assert.Equal(1, r.HashFailures);
            Assert.Equal(0, r.IncomingSkipped);
            Assert.Equal(GroupCandidateDisposition.Kept, r.Candidates.Single().Disposition);
        }
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(Path.Combine(SkipDir, "locked.d64")));
    }

    // ── §31 cleanup semantics ────────────────────────────────────────────────

    [Fact] // 9: all required targets secured → incoming deleted (single leaf)
    public void AllRequiredSecured_IncomingDeleted()
    {
        var c = Bytes("done.d64");
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Done", "done.d64", c) });
        var src = WriteIncoming("done.d64", c);

        var r = Run(plan, new[] { src });

        Assert.Equal(1, r.IncomingDeleted);
        Assert.False(File.Exists(src));
    }

    [Fact] // 8 + 12: finalization does nothing / throws, yet safely-staged candidate is still deleted
    public void FinalizationFails_AfterSafeStaging_IncomingStillDeleted()
    {
        var c = Bytes("incomplete.d64");
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Incomplete", "incomplete.d64", c) });
        var src = WriteIncoming("incomplete.d64", c);

        GroupLeafFinalizer boom = (_, _, _) => throw new InvalidOperationException("transform failed");

        var r = Run(plan, new[] { src }, boom);

        Assert.Equal(1, r.FinalizationErrorCount);
        Assert.Single(r.FinalizationErrors);
        Assert.Equal(1, r.IncomingDeleted);             // staging security, not transform, decides disposition
        Assert.False(File.Exists(src));
    }

    [Fact] // 10 + 11: one of N target copies fails → incoming kept; the successful staging copy remains
    public void OneTargetFails_IncomingKept_SuccessfulCopyRemains()
    {
        var c = Bytes("partial.d64");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "rGood", "Good Release", "partial.d64", c),
            Req("leaf-b", "rBad",  "Bad Release",  "partial.d64", c),
        });
        var src = WriteIncoming("partial.d64", c);

        // Force the leaf-b copy to fail: occupy its destination path with a directory.
        Directory.CreateDirectory(StageDest("leaf-b", "Bad Release", "partial.d64"));

        var r = Run(plan, new[] { src });

        Assert.Equal(1, r.SecuredTargets);
        Assert.Equal(1, r.FailedTargets);
        Assert.Equal(GroupCandidateDisposition.Kept, r.Candidates.Single().Disposition);
        Assert.True(File.Exists(src));                                       // kept — not deleted
        Assert.True(File.Exists(StageDest("leaf-a", "Good Release", "partial.d64")));   // durable copy remains
    }

    [Fact] // 13 + 22 (aggregate): a match in leaf B is not skipped merely because leaf A had no match
    public void MatchInLeafB_NotSkipped_WhenLeafAHasNoMatch()
    {
        var cB = Bytes("only-b.d64");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "rA", "A Content", "a-only.d64", Bytes("a-only")),  // leaf-a wants different content
            Req("leaf-b", "rB", "B Content", "only-b.d64", cB),
        });
        var src = WriteIncoming("only-b.d64", cB);

        var r = Run(plan, new[] { src });

        Assert.Equal(0, r.IncomingSkipped);
        Assert.Equal(1, r.IncomingDeleted);
        Assert.True(File.Exists(StageDest("leaf-b", "B Content", "only-b.d64")));
    }

    [Fact] // 14: cleanup delete failure → error recorded, managed staging preserved, candidate kept
    public void CleanupDeleteFailure_ErrorRecorded_StagingPreserved()
    {
        var c = Bytes("lockdel.d64");
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Locked Del", "lockdel.d64", c) });
        var src = WriteIncoming("lockdel.d64", c);

        // Lock the incoming file so the cleanup File.Delete fails (still fully-secured → tries delete).
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = Run(plan, new[] { src });

            Assert.Equal(1, r.CleanupErrors);
            Assert.Equal(GroupCandidateDisposition.Kept, r.Candidates.Single().Disposition);
            Assert.True(File.Exists(StageDest("leaf-a", "Locked Del", "lockdel.d64")));  // staged copy preserved
            Assert.True(File.Exists(src));                                              // original still there
        }
    }

    [Fact] // 15: skip move collision → existing collision-safe unique path used
    public void SkipMoveCollision_UsesUniquePath()
    {
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "A", "k.d64", Bytes("k")) });
        var src  = WriteIncoming("dup.bin", Bytes("zero-match content"));

        Directory.CreateDirectory(SkipDir);
        File.WriteAllBytes(Path.Combine(SkipDir, "dup.bin"), Bytes("pre-existing"));  // occupy the target name

        var r = Run(plan, new[] { src });

        Assert.Equal(1, r.IncomingSkipped);
        Assert.True(File.Exists(Path.Combine(SkipDir, "dup.bin")));       // untouched original
        Assert.True(File.Exists(Path.Combine(SkipDir, "dup (2).bin")));   // collision-safe new name
        Assert.False(File.Exists(src));
    }

    // ── §32 barrier / order ──────────────────────────────────────────────────

    [Fact] // 16: insufficient staging space → no staging, no finalization, no cleanup
    public void InsufficientSpace_NoMutation()
    {
        var c = Bytes("space.d64");
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Space", "space.d64", c) });
        var src = WriteIncoming("space.d64", c);

        int finalizeCalls = 0;
        GroupLeafFinalizer fin = (_, _, _) => finalizeCalls++;

        var r = Run(plan, new[] { src }, fin, space: _ => 0);   // zero free bytes

        Assert.True(r.Aborted);
        Assert.Equal(0, finalizeCalls);
        Assert.Equal(0, r.LeavesFinalizedAttempted);
        Assert.Equal(0, r.IncomingDeleted);
        Assert.Equal(0, r.IncomingSkipped);
        Assert.True(File.Exists(src));                          // candidate untouched
        Assert.False(File.Exists(StageDest("leaf-a", "Space", "space.d64")));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
    }

    [Fact] // 17: all staging attempts complete before the first finalization callback
    public void AllStagingBeforeFirstFinalization()
    {
        var cA = Bytes("a-content");
        var cB = Bytes("b-content");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "rA", "A", "a.rom", cA),
            Req("leaf-b", "rB", "B", "b.rom", cB),
        });
        var srcA = WriteIncoming("a.rom", cA);
        var srcB = WriteIncoming("b.rom", cB);

        bool bothStagedAtFirstFinalize = false;
        int calls = 0;
        GroupLeafFinalizer fin = (_, _, _) =>
        {
            if (calls++ == 0)
                bothStagedAtFirstFinalize =
                    File.Exists(StageDest("leaf-a", "A", "a.rom")) &&
                    File.Exists(StageDest("leaf-b", "B", "b.rom"));
        };

        Run(plan, new[] { srcA, srcB }, fin);

        Assert.True(bothStagedAtFirstFinalize);
    }

    [Fact] // 18: cleanup begins only after all staging AND finalization (candidate still in incoming at finalize)
    public void CleanupAfterAllStagingAndFinalization()
    {
        var c = Bytes("order.d64");
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Order", "order.d64", c) });
        var src = WriteIncoming("order.d64", c);

        bool candidatePresentAtFinalize = false;
        GroupLeafFinalizer fin = (_, _, _) => candidatePresentAtFinalize = File.Exists(src);

        var r = Run(plan, new[] { src }, fin);

        Assert.True(candidatePresentAtFinalize);   // not yet cleaned during finalization
        Assert.Equal(1, r.IncomingDeleted);        // cleaned only afterwards
        Assert.False(File.Exists(src));
    }

    [Fact] // 19: finalization leaf order is deterministic (Ordinal)
    public void FinalizationLeafOrder_Deterministic()
    {
        var cc = Bytes("c"); var ca = Bytes("a"); var cb = Bytes("b");
        var plan = BuildPlan(new[]
        {
            Req("c-leaf", "rc", "C", "c.rom", cc),
            Req("a-leaf", "ra", "A", "a.rom", ca),
            Req("b-leaf", "rb", "B", "b.rom", cb),
        });
        var s1 = WriteIncoming("c.rom", cc);
        var s2 = WriteIncoming("a.rom", ca);
        var s3 = WriteIncoming("b.rom", cb);

        var order = new List<string>();
        GroupLeafFinalizer fin = (leaf, _, _) => order.Add(leaf);

        Run(plan, new[] { s1, s2, s3 }, fin);

        Assert.Equal(new[] { "a-leaf", "b-leaf", "c-leaf" }, order);
    }

    [Fact] // 20: cleanup candidate order is deterministic (SourcePath Ordinal)
    public void CleanupCandidateOrder_Deterministic()
    {
        var cz = Bytes("z"); var ca = Bytes("a"); var cm = Bytes("m");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "rz", "Z", "z.rom", cz),
            Req("leaf-a", "ra", "A", "a.rom", ca),
            Req("leaf-a", "rm", "M", "m.rom", cm),
        });
        var sz = WriteIncoming("z.rom", cz);
        var sa = WriteIncoming("a.rom", ca);
        var sm = WriteIncoming("m.rom", cm);

        var r = Run(plan, new[] { sz, sa, sm });

        var paths = r.Candidates.Select(x => x.SourcePath).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToList(), paths);
    }

    // ── §33 resumable ────────────────────────────────────────────────────────

    [Fact] // 21: leaf with no new matches but one ResumableRelease → finalization invoked
    public void ResumableLeaf_NoNewMatches_FinalizeInvoked()
    {
        var c = Bytes("elsewhere.d64");
        var plan = BuildPlan(
            required: new[] { Req("leaf-a", "rA", "A", "a.d64", c) },
            resumable: new[] { new GroupResumableRelease("leaf-r", "rResume") });
        var src = WriteIncoming("a.d64", c);   // matches leaf-a only

        var calls = new List<(string leaf, string[] affected, string[] resumable)>();
        GroupLeafFinalizer fin = (leaf, aff, res) => calls.Add((leaf, aff.ToArray(), res.ToArray()));

        Run(plan, new[] { src }, fin);

        var resumeCall = calls.Single(x => x.leaf == "leaf-r");
        Assert.Empty(resumeCall.affected);
        Assert.Equal(new[] { "rResume" }, resumeCall.resumable);
    }

    [Fact] // 22: leaf with both affected + resumable for the same release → finalized once
    public void AffectedAndResumableSameRelease_FinalizedOnce()
    {
        var c = Bytes("both.d64");
        var plan = BuildPlan(
            required:  new[] { Req("leaf-a", "rShared", "Shared", "both.d64", c) },
            resumable: new[] { new GroupResumableRelease("leaf-a", "rShared") });
        var src = WriteIncoming("both.d64", c);

        var calls = new List<string>();
        GroupLeafFinalizer fin = (leaf, _, _) => calls.Add(leaf);

        var r = Run(plan, new[] { src }, fin);

        Assert.Single(calls);
        Assert.Equal("leaf-a", calls[0]);
        Assert.Equal(1, r.LeavesFinalizedAttempted);
    }

    [Fact] // 23: a leaf with neither affected nor resumable work is never finalized
    public void SatisfiedLeaf_NoFinalization()
    {
        var c = Bytes("only-a.d64");
        var plan = BuildPlan(
            required:    new[] { Req("leaf-a", "rA", "A", "only-a.d64", c) },
            extraLeaves: new[] { "leaf-x" });                     // present but no work
        var src = WriteIncoming("only-a.d64", c);

        var calls = new List<string>();
        GroupLeafFinalizer fin = (leaf, _, _) => calls.Add(leaf);

        Run(plan, new[] { src }, fin);

        Assert.DoesNotContain("leaf-x", calls);
        Assert.Equal(new[] { "leaf-a" }, calls);
    }

    // ── §34 plan / target semantics ──────────────────────────────────────────

    [Fact] // 24: content that is already DB-satisfied is absent from RequiredTargets → executor never stages it
    public void AlreadySatisfiedContent_AbsentFromPlan_NotStaged()
    {
        var satisfied = Bytes("already-in-archive");
        // The plan (as the planner would produce) carries only the still-required target, NOT the satisfied one.
        var plan = BuildPlan(new[] { Req("leaf-a", "rNeed", "Needed", "need.d64", Bytes("still needed")) });
        var src  = WriteIncoming("redundant.d64", satisfied);   // hash of satisfied content: not in the aggregate index

        var r = Run(plan, new[] { src });

        Assert.Equal(1, r.ZeroMatches);
        Assert.Equal(0, r.SecuredTargets);
        Assert.Equal(1, r.IncomingSkipped);                     // §23 redundant incoming → skip
    }

    [Fact] // 25: content already staged is likewise absent from RequiredTargets → not re-staged
    public void AlreadyStagedContent_AbsentFromPlan_NotStaged()
    {
        var staged = Bytes("already-staged-bytes");
        var plan = BuildPlan(new[] { Req("leaf-a", "rNeed", "Needed", "need.d64", Bytes("need")) });
        var src  = WriteIncoming("stagedcopy.d64", staged);     // not represented in RequiredTargets

        var r = Run(plan, new[] { src });

        Assert.Equal(0, r.MatchedCandidates);
        Assert.Equal(0, r.SecuredTargets);
    }

    [Fact] // 26: two candidates with the same hash → single authoritative target staged exactly once
    public void ExactDuplicateAuthoritativeTarget_StagedOnce()
    {
        var c = Bytes("dupe-content");
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Only One", "one.d64", c) });
        var src1 = WriteIncoming("copy1.d64", c);
        var src2 = WriteIncoming("copy2.d64", c);   // identical content

        var r = Run(plan, new[] { src1, src2 });

        Assert.Equal(1, r.SecuredTargets);                      // staged once, not twice
        Assert.True(File.Exists(StageDest("leaf-a", "Only One", "one.d64")));
        Assert.Equal(2, r.IncomingDeleted);                     // both redundant incomings assimilated → deleted
    }

    [Fact] // 27: cross-leaf same-hash targets remain distinct materializations
    public void CrossLeafSameHash_TargetsRemainDistinct()
    {
        var c = Bytes("cross.d64");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "rA", "A", "cross.d64", c),
            Req("leaf-b", "rB", "B", "cross.d64", c),
        });
        var src = WriteIncoming("cross.d64", c);

        var r = Run(plan, new[] { src });

        Assert.Equal(2, r.SecuredTargets);
        Assert.True(File.Exists(StageDest("leaf-a", "A", "cross.d64")));
        Assert.True(File.Exists(StageDest("leaf-b", "B", "cross.d64")));
    }

    // ── code-review correction: authoritative FullySecured predicate ─────────
    // Positive proof — SecuredTargets == RequiredTargets — not mere absence of a recorded failure.

    private static GroupCandidateOutcome Outcome(bool hash, int match, int required, int secured, int failed)
    {
        var o = new GroupCandidateOutcome
        {
            SourcePath = "x", HashSucceeded = hash,
            MatchCount = match, RequiredTargets = required, SecuredTargets = secured,
        };
        for (int i = 0; i < failed; i++) o.FailedTargets.Add($"f{i}");
        return o;
    }

    [Fact] // 1: required=3, secured=3, failed=0 → FullySecured
    public void Predicate_Required3_Secured3_Failed0_IsFullySecured()
        => Assert.True(Outcome(hash: true, match: 3, required: 3, secured: 3, failed: 0).FullySecured);

    [Fact] // 2: required=3, secured=2, failed=1 → NOT FullySecured
    public void Predicate_Required3_Secured2_Failed1_NotFullySecured()
        => Assert.False(Outcome(hash: true, match: 3, required: 3, secured: 2, failed: 1).FullySecured);

    [Fact] // 3: required=3, secured=2, failed=0 → NOT FullySecured (absence of failure is not proof)
    public void Predicate_Required3_Secured2_Failed0_NotFullySecured()
        => Assert.False(Outcome(hash: true, match: 3, required: 3, secured: 2, failed: 0).FullySecured);

    [Fact] // 4: required=0 / successful hash → zero-match, never FullySecured
    public void Predicate_Required0_ZeroMatch_NeverFullySecured()
    {
        var o = Outcome(hash: true, match: 0, required: 0, secured: 0, failed: 0);
        Assert.False(o.FullySecured);
        Assert.True(o.ZeroMatch);
    }

    [Fact] // 5: hash failure → never FullySecured
    public void Predicate_HashFailure_NeverFullySecured()
        => Assert.False(Outcome(hash: false, match: 0, required: 0, secured: 0, failed: 0).FullySecured);

    [Fact] // integration 1: three required targets all secured → incoming DELETED
    public void ThreeTargets_AllSecured_IncomingDeleted()
    {
        var c = Bytes("three.d64");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "r1", "One",   "three.d64", c),
            Req("leaf-b", "r2", "Two",   "three.d64", c),
            Req("leaf-c", "r3", "Three", "three.d64", c),
        });
        var src = WriteIncoming("three.d64", c);

        var r = Run(plan, new[] { src });

        Assert.Equal(3, r.RequiredTargets);
        Assert.Equal(3, r.SecuredTargets);
        Assert.True(r.Candidates.Single().FullySecured);
        Assert.Equal(1, r.IncomingDeleted);
        Assert.False(File.Exists(src));
    }

    [Fact] // integration 2: three required targets, one fails → NOT fully secured → KEPT
    public void ThreeTargets_OneFails_IncomingKept()
    {
        var c = Bytes("three-partial.d64");
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "r1", "One",   "t.d64", c),
            Req("leaf-b", "r2", "Two",   "t.d64", c),
            Req("leaf-c", "r3", "Three", "t.d64", c),
        });
        var src = WriteIncoming("t.d64", c);
        Directory.CreateDirectory(StageDest("leaf-b", "Two", "t.d64"));   // force the leaf-b copy to fail

        var r = Run(plan, new[] { src });

        var oc = r.Candidates.Single();
        Assert.Equal(3, oc.RequiredTargets);
        Assert.Equal(2, oc.SecuredTargets);
        Assert.False(oc.FullySecured);
        Assert.Equal(GroupCandidateDisposition.Kept, oc.Disposition);
        Assert.Equal(0, r.IncomingDeleted);
        Assert.True(File.Exists(src));
    }
}

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
/// Bridge-level tests wiring <see cref="GroupIngestionCandidateSource"/> → <see cref="GroupIngestionExecutor"/>
/// with a controlled fake finalizer (no MainWindow, no real FinalizeConfiguredLeaf, no real runtime). Proves the
/// runtime contract: excluded containers survive, the finalizer is invoked only for work leaves with the
/// supplied DatLineId, results are captured, and hard finalizer exceptions are recorded while other leaves
/// continue — all with staging-based deletion unaffected. Temp roots only.
/// </summary>
public sealed class GroupIngestionBridgeTests : IDisposable
{
    private readonly string _root;
    private const string Platform = "c64";

    public GroupIngestionBridgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ark-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Sha1(byte[] b)  => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    private string IncomingDir { get { var d = Path.Combine(_root, "incoming-roms", Platform); Directory.CreateDirectory(d); return d; } }
    private string WriteIncoming(string rel, byte[] c)
    {
        var p = Path.Combine(IncomingDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, c);
        return p;
    }

    private static GroupRequiredTarget Req(string leaf, string rel, string relName, string rom, byte[] c)
        => new(new GroupMatchTarget(leaf, rel, rom), relName, c.Length, Sha1(c), "");

    private static GroupIngestionPlan BuildPlan(
        IEnumerable<GroupRequiredTarget> required, IEnumerable<string>? extraLeaves = null)
    {
        var reqArr = required.ToImmutableArray();
        var sha1 = new Dictionary<string, List<GroupMatchTarget>>();
        foreach (var rt in reqArr)
        {
            if (!sha1.TryGetValue(rt.Sha1, out var l)) { l = new(); sha1[rt.Sha1] = l; }
            if (!l.Contains(rt.Target)) l.Add(rt.Target);
        }
        var leaves = reqArr.Select(r => r.Target.DatLineId)
            .Concat(extraLeaves ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();

        return new GroupIngestionPlan
        {
            GroupId = "c64-tosec", DisplayName = "TOSEC", PlatformId = Platform,
            ExpectedLeafIds = leaves, FileHandling = "archives_pre_extraction",
            PreparedLeaves = ImmutableArray<GroupPreparedLeaf>.Empty,
            RequiredTargets = reqArr,
            AggregateSha1Index = sha1,
            AggregateMd5Index = new Dictionary<string, List<GroupMatchTarget>>(),
            ResumableReleases = ImmutableArray<GroupResumableRelease>.Empty,
            IncomingInspection = new GroupIncomingInspection(0, 0),
            Readiness = GroupIngestionReadiness.Ready,
            Diagnostics = ImmutableArray<GroupLeafDiagnostic>.Empty,
        };
    }

    private static readonly GroupLeafFinalizer NoOp = (_, _, _) => { };

    [Fact] // 15 + 9/10/11 integration: post-extraction candidates (member) flow to executor; excluded .zip survives
    public void Candidates_FromIncoming_FlowToExecutor_ContainerUntouched()
    {
        var c = Bytes("extracted-rom");
        WriteIncoming("game.zip", Bytes("archive-bytes"));      // container: excluded, must survive
        var member = WriteIncoming("game/rom.d64", c);          // extracted member: candidate

        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "Game", "rom.d64", c) });
        var candidates = GroupIngestionCandidateSource.EnumerateCandidates(IncomingDir, plan.FileHandling);

        var finalized = new List<string>();
        var r = GroupIngestionExecutor.Execute(plan, candidates, _root, (id, _, _) => finalized.Add(id));

        Assert.Contains(member, candidates);
        Assert.DoesNotContain(candidates, p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, r.SecuredTargets);
        Assert.Equal(1, r.IncomingDeleted);                    // member fully secured → deleted
        Assert.False(File.Exists(member));
        Assert.True(File.Exists(Path.Combine(IncomingDir, "game.zip")));   // container never touched by executor
        Assert.Equal(new[] { "leaf-a" }, finalized);
    }

    [Fact] // 16 + 17 + 18: finalizer invoked only for work leaves, with the supplied DatLineId (no UI selection)
    public void Finalizer_InvokedOnlyForWorkLeaves_WithSuppliedId()
    {
        var c = Bytes("rom-a");
        var member = WriteIncoming("rom.d64", c);
        // leaf-a has a required target; leaf-x has NO work (extra leaf) → must never get a finalizer call
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "A", "rom.d64", c) }, extraLeaves: new[] { "leaf-x" });
        var candidates = GroupIngestionCandidateSource.EnumerateCandidates(IncomingDir, plan.FileHandling);

        var finalized = new List<string>();
        GroupLeafFinalizer fin = (id, affected, _) =>
        {
            Assert.Equal("leaf-a", id);              // resolves the supplied id, not any UI selection
            Assert.NotEmpty(affected);
            finalized.Add(id);
        };
        GroupIngestionExecutor.Execute(plan, candidates, _root, fin);

        Assert.Equal(new[] { "leaf-a" }, finalized);  // leaf-x never finalized → no writable store would open
        Assert.Contains(member, candidates);
    }

    [Fact] // 19: LeafFinalizationResult captured into a runtime summary
    public void Finalizer_CapturesLeafResults_IntoSummary()
    {
        var c = Bytes("rom-cap");
        WriteIncoming("rom.d64", c);
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "A", "rom.d64", c) });
        var candidates = GroupIngestionCandidateSource.EnumerateCandidates(IncomingDir, plan.FileHandling);

        var captured = new List<(string, LeafFinalizationResult)>();
        GroupLeafFinalizer fin = (id, affected, _) =>
            captured.Add((id, new LeafFinalizationResult
            {
                SuccessfulReleaseIds      = affected.ToList(),
                IncompleteReleaseIds      = Array.Empty<string>(),
                TransformFailedReleaseIds = Array.Empty<string>(),
            }));

        var exec = GroupIngestionExecutor.Execute(plan, candidates, _root, fin);
        var summary = GroupIngestionRunSummary.Build(exec, captured, 0, 0);

        Assert.Equal(1, summary.SuccessfulReleases);
        Assert.Equal(GroupRunStatus.Completed, summary.Status);
    }

    [Fact] // 20: a transform-failed leaf (non-throwing) does not change the fully-secured incoming deletion
    public void TransformFailedLeaf_DoesNotChangeDeletion()
    {
        var c = Bytes("rom-tf");
        var member = WriteIncoming("rom.d64", c);
        var plan = BuildPlan(new[] { Req("leaf-a", "r1", "A", "rom.d64", c) });
        var candidates = GroupIngestionCandidateSource.EnumerateCandidates(IncomingDir, plan.FileHandling);

        var captured = new List<(string, LeafFinalizationResult)>();
        GroupLeafFinalizer fin = (id, _, _) =>
            captured.Add((id, new LeafFinalizationResult
            {
                SuccessfulReleaseIds      = Array.Empty<string>(),
                IncompleteReleaseIds      = Array.Empty<string>(),
                TransformFailedReleaseIds = new[] { "r1" },   // transform failed for this release
            }));

        var exec = GroupIngestionExecutor.Execute(plan, candidates, _root, fin);
        var summary = GroupIngestionRunSummary.Build(exec, captured, 0, 0);

        Assert.Equal(1, exec.IncomingDeleted);                 // still deleted — staging was fully secured
        Assert.False(File.Exists(member));
        Assert.Equal(1, summary.TransformFailedReleases);      // reported only
    }

    [Fact] // 21: a hard finalizer exception is recorded and other leaves still finalize; deletions unaffected
    public void HardFinalizerException_Recorded_OtherLeavesContinue()
    {
        var c = Bytes("rom-multi");
        var mA = WriteIncoming("a.d64", c);
        var mB = WriteIncoming("b.d64", Bytes("rom-b"));
        var plan = BuildPlan(new[]
        {
            Req("leaf-a", "r1", "A", "a.d64", c),
            Req("leaf-b", "r2", "B", "b.d64", Bytes("rom-b")),
        });
        var candidates = GroupIngestionCandidateSource.EnumerateCandidates(IncomingDir, plan.FileHandling);

        var finalized = new List<string>();
        GroupLeafFinalizer fin = (id, _, _) =>
        {
            if (id == "leaf-a") throw new InvalidOperationException("hard failure");
            finalized.Add(id);
        };

        var exec = GroupIngestionExecutor.Execute(plan, candidates, _root, fin);

        Assert.Equal(1, exec.FinalizationErrorCount);
        Assert.Contains(exec.FinalizationErrors, e => e.DatLineId == "leaf-a");
        Assert.Contains("leaf-b", finalized);                  // other leaf continued
        Assert.Equal(2, exec.IncomingDeleted);                 // both fully secured → deleted regardless
        Assert.False(File.Exists(mA));
        Assert.False(File.Exists(mB));
    }

    [Fact] // 8/14: all_files keeps .zip as an ordinary candidate (no pre-extraction semantics)
    public void AllFiles_ContainerIsOrdinaryCandidate()
    {
        var zip = WriteIncoming("thing.zip", Bytes("z"));
        var cand = GroupIngestionCandidateSource.EnumerateCandidates(IncomingDir, "all_files");
        Assert.Contains(zip, cand);
    }
}

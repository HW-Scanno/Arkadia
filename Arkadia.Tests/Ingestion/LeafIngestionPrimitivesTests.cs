using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// M9B1 tests for the behavior-preserving ingestion primitives extracted from RunIngestionWork:
/// <see cref="LeafIngestionNeeds"/>, <see cref="IngestionCandidateMatcher"/>,
/// <see cref="IngestionSpacePreflight"/>, and <see cref="IngestionStagingEngine"/>. Temp dirs only —
/// no real incoming/staging/source/catalog is touched.
/// </summary>
public sealed class LeafIngestionPrimitivesTests : IDisposable
{
    private readonly string _dir;
    public LeafIngestionPrimitivesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkIngPrim_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ReleaseRecord Rel(string id, string name, string status = "missing")
        => new() { Id = id, DatLineId = "leaf", Name = name, Status = status };
    private static ReleaseFileRecord File_(string rom, string sha1 = "", string md5 = "", string size = "10")
        => new() { RomName = rom, Sha1 = sha1, Md5 = md5, Size = size };

    // ── LeafIngestionNeeds ──────────────────────────────────────────────────

    [Fact]  // (4)(6) missing wanted target included; one hash → multiple targets preserved
    public void Needs_IndexesTargets_MultiTargetPreserved()
    {
        var sha = new string('a', 40);
        var releases = new List<ReleaseRecord> { Rel("r1", "Game 1"), Rel("r2", "Game 2") };
        var files = new Dictionary<string, List<ReleaseFileRecord>>
        {
            ["r1"] = new() { File_("g1.d64", sha1: sha) },
            ["r2"] = new() { File_("g2.d64", sha1: sha) },   // same hash → two targets
        };
        var needs = LeafIngestionNeeds.Build("leaf", releases, files, Array.Empty<string>());

        Assert.Equal(2, needs.Sha1Index[sha].Count);
        Assert.Contains(needs.Sha1Index[sha], t => t.ReleaseId == "r1" && t.RomName == "g1.d64");
        Assert.Contains(needs.Sha1Index[sha], t => t.ReleaseId == "r2" && t.RomName == "g2.d64");
        Assert.Equal(10, needs.ExpectedSizeIndex["r1|g1.d64"]);
    }

    [Fact]  // (5) outdated releases are excluded from the hash index (unchanged filtering)
    public void Needs_OutdatedReleasesExcluded()
    {
        var sha = new string('b', 40);
        var releases = new List<ReleaseRecord> { Rel("r1", "Live"), Rel("r2", "Old", status: "outdated") };
        var files = new Dictionary<string, List<ReleaseFileRecord>>
        {
            ["r1"] = new() { File_("a.d64", sha1: sha) },
            ["r2"] = new() { File_("b.d64", sha1: sha) },
        };
        var needs = LeafIngestionNeeds.Build("leaf", releases, files, Array.Empty<string>());

        Assert.Single(needs.Sha1Index[sha]);                       // only the non-outdated release
        Assert.Equal("r1", needs.Sha1Index[sha][0].ReleaseId);
        Assert.False(needs.Releases.ContainsKey("r2"));
    }

    [Fact]  // (7) resumable release ids surfaced for later finalization
    public void Needs_ResumableExposed()
    {
        var needs = LeafIngestionNeeds.Build("leaf",
            new List<ReleaseRecord> { Rel("r1", "G") },
            new Dictionary<string, List<ReleaseFileRecord>>(),
            new[] { "r1" });
        Assert.Equal(new[] { "r1" }, needs.ResumableReleaseIds);
    }

    // ── HashAndMatch ────────────────────────────────────────────────────────

    private string WriteFile(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static (Dictionary<string, List<(string, string)>> sha1, Dictionary<string, List<(string, string)>> md5)
        Indexes(string sha1Hex, string md5Hex, (string, string) target)
    {
        var s = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        var m = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        if (sha1Hex.Length > 0) s[sha1Hex] = new() { target };
        if (md5Hex.Length  > 0) m[md5Hex]  = new() { target };
        return (s, m);
    }

    private static string Sha1Hex(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b)).ToLowerInvariant();
    private static string Md5Hex(byte[] b)  => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(b)).ToLowerInvariant();

    [Fact]  // (8) matches via SHA1
    public void Match_Sha1Hit()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var p = WriteFile("a.bin", bytes);
        var (s, m) = Indexes(Sha1Hex(bytes), "", ("r1", "a.bin"));

        var r = IngestionCandidateMatcher.HashAndMatch(p, s, m);
        Assert.True(r.HashSucceeded);
        Assert.Single(r.Targets);
        Assert.Equal(("r1", "a.bin"), r.Targets[0]);
    }

    [Fact]  // (9) MD5 fallback when SHA1 misses
    public void Match_Md5Fallback()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var p = WriteFile("b.bin", bytes);
        var (s, m) = Indexes("", Md5Hex(bytes), ("r2", "b.bin"));   // no SHA1 entry

        var r = IngestionCandidateMatcher.HashAndMatch(p, s, m);
        Assert.True(r.HashSucceeded);
        Assert.Single(r.Targets);
        Assert.Equal(("r2", "b.bin"), r.Targets[0]);
    }

    [Fact]  // (10) hashed-ok zero-match is DISTINCT from hash failure
    public void Match_ZeroMatch_Vs_HashFailure()
    {
        var bytes = new byte[] { 5, 5, 5 };
        var p = WriteFile("c.bin", bytes);
        var empty = new Dictionary<string, List<(string, string)>>();

        var ok = IngestionCandidateMatcher.HashAndMatch(p, empty, empty);   // hashed fine, no target
        Assert.True(ok.HashSucceeded);
        Assert.Equal(0, ok.MatchCount);

        var fail = IngestionCandidateMatcher.HashAndMatch(Path.Combine(_dir, "does-not-exist.bin"), empty, empty);
        Assert.False(fail.HashSucceeded);   // read/hash failed → distinct from zero-match
        Assert.Equal(0, fail.MatchCount);
    }

    [Fact]  // (11) one hash → multiple target matches
    public void Match_MultipleTargets()
    {
        var bytes = new byte[] { 42 };
        var p = WriteFile("d.bin", bytes);
        var s = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        { [Sha1Hex(bytes)] = new() { ("r1", "d.bin"), ("r2", "d.bin") } };
        var m = new Dictionary<string, List<(string, string)>>();

        var r = IngestionCandidateMatcher.HashAndMatch(p, s, m);
        Assert.Equal(2, r.MatchCount);
    }

    // ── Space preflight ─────────────────────────────────────────────────────

    [Fact]  // (12)(13) formula identical; multi-target multiplies source size
    public void Preflight_FormulaMatchesHistorical()
    {
        var items = new (long, int)[] { (100, 1), (200, 3) };   // 100*1 + 200*3 = 700
        Assert.Equal(700 + 256L * 1024 * 1024, IngestionSpacePreflight.BytesNeeded(items));
    }

    // ── Staging engine ──────────────────────────────────────────────────────

    private static (Dictionary<string, List<(string ReleaseId, string RomName)>> plan,
                    Dictionary<string, ReleaseRecord> releases) Plan(
        string srcPath, string releaseId, string rom, string status = "missing")
    {
        var plan = new Dictionary<string, List<(string ReleaseId, string RomName)>>(StringComparer.OrdinalIgnoreCase)
        { [srcPath] = new() { (releaseId, rom) } };
        var releases = new Dictionary<string, ReleaseRecord>(StringComparer.Ordinal)
        { [releaseId] = Rel(releaseId, "Game", status) };
        return (plan, releases);
    }

    private StagingResult RunStage(string srcPath, Dictionary<string, List<(string, string)>> plan,
        Dictionary<string, ReleaseRecord> releases, HashSet<string> satisfied, string stagingRoot, bool allowMove)
        => IngestionStagingEngine.StageTargets(plan, releases, satisfied, stagingRoot, "c64", "leaf",
            s => s, new IngestionResult(), new Progress<IngestionProgress>(_ => { }), allowMove);

    [Fact]  // (14) sole target, same volume, allowMove=true → MOVE (source gone)
    public void Stage_SingleTarget_SameVolume_Moves()
    {
        var stagingRoot = Path.Combine(_dir, "staging");
        var src = WriteFile("m.bin", new byte[] { 1 });
        var (plan, releases) = Plan(src, "r1", "m.bin");

        var res = RunStage(src, plan, releases, new(StringComparer.Ordinal), stagingRoot, allowMove: true);

        Assert.Contains(src, res.MovedFromIncoming);
        Assert.False(File.Exists(src));   // moved out of incoming
        Assert.True(File.Exists(Path.Combine(stagingRoot, "Game", "m.bin")));
    }

    [Fact]  // (16)(19) allowMove=false → COPY even for one target; incoming survives
    public void Stage_ForceCopy_IncomingSurvives()
    {
        var stagingRoot = Path.Combine(_dir, "staging2");
        var src = WriteFile("c.bin", new byte[] { 2, 2 });
        var (plan, releases) = Plan(src, "r1", "c.bin");

        var res = RunStage(src, plan, releases, new(StringComparer.Ordinal), stagingRoot, allowMove: false);

        Assert.Contains(src, res.SuccessfullyCopied);
        Assert.True(File.Exists(src), "forced copy must leave the incoming original");
        Assert.True(File.Exists(Path.Combine(stagingRoot, "Game", "c.bin")));
        var led = Assert.Single(res.Ledger);
        Assert.Equal(1, led.RequiredTargets);
        Assert.Equal(1, led.SecuredTargets);
        Assert.Empty(led.FailedTargets);
    }

    [Fact]  // (15)(17) multi-target → copies to all; ledger Secured == Required
    public void Stage_MultiTarget_CopiesAll()
    {
        var stagingRoot = Path.Combine(_dir, "staging3");
        var src = WriteFile("multi.bin", new byte[] { 3, 3, 3 });
        var plan = new Dictionary<string, List<(string ReleaseId, string RomName)>>(StringComparer.OrdinalIgnoreCase)
        { [src] = new() { ("r1", "multi.bin"), ("r2", "multi.bin") } };
        var releases = new Dictionary<string, ReleaseRecord>(StringComparer.Ordinal)
        { ["r1"] = Rel("r1", "A"), ["r2"] = Rel("r2", "B") };

        var res = RunStage(src, plan, releases, new(StringComparer.Ordinal), stagingRoot, allowMove: true);

        Assert.True(File.Exists(src), "multi-target must copy, not move → incoming survives");
        Assert.True(File.Exists(Path.Combine(stagingRoot, "A", "multi.bin")));
        Assert.True(File.Exists(Path.Combine(stagingRoot, "B", "multi.bin")));
        var led = Assert.Single(res.Ledger);
        Assert.Equal(2, led.RequiredTargets);
        Assert.Equal(2, led.SecuredTargets);
    }

    [Fact]  // (18) one target failure → ledger records the failed target
    public void Stage_TargetFailure_RecordedInLedger()
    {
        var stagingRoot = Path.Combine(_dir, "staging4");
        var src = WriteFile("f.bin", new byte[] { 4 });
        // Make the copy itself fail (not the folder create): pre-create the destination path AS A DIRECTORY,
        // so File.Copy(src, dest) throws and is caught by the engine's per-target handler.
        Directory.CreateDirectory(Path.Combine(stagingRoot, "Game", "f.bin"));
        var (plan, releases) = Plan(src, "r1", "f.bin");

        var res = RunStage(src, plan, releases, new(StringComparer.Ordinal), stagingRoot, allowMove: false);

        var led = Assert.Single(res.Ledger);
        Assert.Contains("f.bin", led.FailedTargets);
        Assert.DoesNotContain(src, res.SuccessfullyCopied);   // any-failed → not marked assimilated
    }

    [Fact]  // (20) pre-existing satisfied target is not staged again
    public void Stage_AlreadySatisfied_NotStaged()
    {
        var stagingRoot = Path.Combine(_dir, "staging5");
        var src = WriteFile("s.bin", new byte[] { 5 });
        var (plan, releases) = Plan(src, "r1", "s.bin");
        var satisfied = new HashSet<string>(StringComparer.Ordinal) { "r1|s.bin" };   // already satisfied

        var res = RunStage(src, plan, releases, satisfied, stagingRoot, allowMove: false);

        Assert.Contains(src, res.AllTargetsSatisfied);
        Assert.False(File.Exists(Path.Combine(stagingRoot, "Game", "s.bin")));   // not re-copied
        Assert.True(File.Exists(src));
    }
}

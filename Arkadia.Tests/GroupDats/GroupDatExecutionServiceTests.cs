using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.GroupDats;
using Xunit;

namespace Arkadia.Tests.GroupDats;

/// <summary>
/// Tests for <see cref="GroupDatExecutionService.ExecuteCreateAsync"/> — the UI-free Group-Create executor.
/// Real <see cref="CatalogService"/> over a temp catalog.db, real leaf databases built via
/// <see cref="LeafDatDatabaseBuilder"/>, real source .dat files parsed by <see cref="DatParser"/> and
/// discovered by <see cref="DatGroupSourceDiscoveryService"/>. Failure injection uses the executor's
/// minimal internal, instance-scoped seams (and the catalog's own commit seam); no generalized framework.
/// </summary>
public sealed class GroupDatExecutionServiceTests : IDisposable
{
    private readonly string _root;      // temp base
    private readonly string _dataDir;   // catalog.db + systems/<sys>/<leaf>.db live here
    private readonly string _srcRoot;   // external source DAT tree

    public GroupDatExecutionServiceTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "ArkGrpExec_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_root, "data");
        _srcRoot = Path.Combine(_root, "src");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_srcRoot);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogService NewCatalog(string systemId = "c64")
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = systemId, Name = systemId.ToUpperInvariant(), Manufacturer = "Commodore", HardwareTypeId = "" },
        });
        return c;
    }

    /// <summary>Writes a minimal Logiqx datafile under the source root; returns its '/'-normalized relative path.</summary>
    private string WriteDat(string relPath, string headerName, string version = "1", int games = 1,
        string? workingState = null, string gamePrefix = "Game")
    {
        var abs = Path.Combine(_srcRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<datafile>");
        sb.AppendLine($"  <header><name>{headerName}</name><version>{version}</version></header>");
        for (int i = 0; i < games; i++)
        {
            var ws = workingState is null ? "" : $"<info name=\"arkadia:working_state\" value=\"{workingState}\"/>";
            sb.AppendLine($"  <game name=\"{gamePrefix} {i} (USA)\">{ws}<rom name=\"r{i}.bin\" size=\"{100 + i}\" crc=\"0000000{i % 10}\" sha1=\"{new string((char)('a' + i % 6), 40)}\"/></game>");
        }
        sb.AppendLine("</datafile>");
        File.WriteAllText(abs, sb.ToString());
        return relPath;
    }

    private GroupDatReconciliationPlan BuildPlan(
        (string rel, string leaf, string media)[] leaves,
        string systemId = "c64", string authority = "tosec")
    {
        var disc     = new DatGroupSourceDiscoveryService().Discover(_srcRoot);
        var snapshot = disc.Leaves.ToImmutableArray();
        var newLeaves = leaves.Select(l =>
        {
            var snap = disc.Leaves.First(x => string.Equals(x.RelativePath, l.rel, StringComparison.Ordinal));
            return new GroupDatNewLeafPlan(l.leaf, l.media, l.rel, snap.SourcePath, snap.DatName, snap.DatVersion, snap.GameCount);
        }).ToImmutableArray();

        return new GroupDatReconciliationPlan(
            GroupDatReconciliationMode.NewGroup, _srcRoot, systemId, systemId.ToUpperInvariant(), authority,
            $"{systemId}-{authority}", $"{systemId} {authority}", systemId,
            ImmutableArray<GroupDatUpdateActionPlan>.Empty, newLeaves,
            ImmutableArray<GroupDatAbsentLeafPlan>.Empty, snapshot);
    }

    private GroupDatExecutionResult Run(
        CatalogService c, GroupDatReconciliationPlan plan,
        Action<GroupDatExecutionService>? configure = null, CancellationToken ct = default)
    {
        var svc = new GroupDatExecutionService(c, _dataDir);
        configure?.Invoke(svc);
        return svc.ExecuteCreateAsync(plan, null, ct).GetAwaiter().GetResult();
    }

    private string FinalPath(string leafId, string system = "c64") => Path.Combine(_dataDir, "systems", system, leafId + ".db");
    private bool LeafDbExists(string leafId, string system = "c64") => File.Exists(FinalPath(leafId, system));
    private bool AnyTempFiles() => Directory.Exists(_dataDir)
        && Directory.EnumerateFiles(_dataDir, "*", SearchOption.AllDirectories).Any(f => f.Contains(".tmp-", StringComparison.Ordinal));

    private static string Sha256Of(string path)
    {
        using var s = SHA256.Create();
        using var f = File.OpenRead(path);
        return Convert.ToHexString(s.ComputeHash(f)).ToLowerInvariant();
    }

    private static void SeedExistingGroup(CatalogService c, string groupId = "c64-tosec", string leafId = "c64-tosec-seed")
        => c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = groupId, DisplayName = groupId, HardwareFamilyId = "c64", Authority = "tosec",
            Leaves = new[]
            {
                new GroupDatCatalogLeafCreate
                {
                    DatLine = new DatLineRecord
                    {
                        Id = leafId, HardwareFamilyId = "c64", Name = "other", Authority = "tosec", MediaTypeId = "other",
                        Version = "1", StorageStrategyId = "", DataStorePath = $"systems/c64/{leafId}.db",
                        ReleaseCount = 0, ImportedAtUtc = DateTime.UtcNow,
                    },
                    RelativeDatPath = "seed/x.dat", SourceDatName = "x.dat",
                    SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
                },
            },
        });

    // ── Success ─────────────────────────────────────────────────────────────────

    [Fact]  // (1) one leaf; (4) group created; (7) revision 0; (9) final DB openable; (10) counts; (11) no temp
    public void Create_OneLeaf_PersistsGroupLeafAndDb()
    {
        var c    = NewCatalog();
        var rel  = WriteDat("A/anims.dat", "Anims", games: 3);
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionStatus.Committed, r.OverallStatus);
        Assert.Equal(0, r.Revision);
        Assert.Equal(1, r.PublishedCount);
        Assert.True(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        var leaf = Assert.Single(c.GetLeavesForGroup("c64-tosec"));
        Assert.Equal("c64-tosec-a", leaf.DatLine.Id);
        Assert.Equal(3, leaf.DatLine.ReleaseCount);

        Assert.True(LeafDbExists("c64-tosec-a"));
        var store = new DatLineStore(FinalPath("c64-tosec-a"));
        Assert.Equal(3, store.LoadReleases().Count);
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (2) multiple leaves; (5) all dat_lines; (6) metadata; (8) SHA-256; (12) working states
    public void Create_ManyLeaves_PersistsAllMetadataAndWorkingStates()
    {
        var c   = NewCatalog();
        var rA  = WriteDat("A/a.dat", "AA", games: 2, workingState: WorkingState.Working);
        var rB  = WriteDat("B/b.dat", "BB", games: 1);
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other") });

        var r = Run(c, plan);
        Assert.Equal(GroupDatExecutionStatus.Committed, r.OverallStatus);

        var leaves = c.GetLeavesForGroup("c64-tosec").ToDictionary(l => l.DatLine.Id, l => l);
        Assert.Equal(2, leaves.Count);

        var mA = leaves["c64-tosec-a"].GroupMetadata;
        Assert.Equal("c64-tosec", mA.GroupId);
        Assert.Equal("A/a.dat", mA.RelativeDatPath);
        Assert.Equal("a.dat", mA.SourceDatName);
        Assert.Equal(Sha256Of(Path.Combine(_srcRoot, "A", "a.dat")), mA.SourceDatSha256);
        Assert.Equal(0, mA.LastSeenGroupRevision);

        // Working state declared by the DAT was applied (Single-DAT Import semantics).
        Assert.Equal(WorkingState.Working, c.GetWorkingState("Game 0 (USA)")?.State);
        Assert.Equal(0, c.GetDatGroup(DatGroupId.FromPersisted("c64-tosec"))!.CurrentRevision);
        Assert.Equal("c64 tosec", c.GetDatGroup(DatGroupId.FromPersisted("c64-tosec"))!.DisplayName);
    }

    [Fact]  // (3) 410 leaves — one Group, all committed, all DBs published, no temp left
    public void Create_410Leaves_AllPublishedAndCommitted()
    {
        var c = NewCatalog();
        var specs = Enumerable.Range(0, 410)
            .Select(i => (WriteDat($"Set{i:D3}/g.dat", $"S{i}", games: 1), $"c64-tosec-{i:D3}", "other"))
            .ToArray();
        var plan = BuildPlan(specs);

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionStatus.Committed, r.OverallStatus);
        Assert.Equal(410, r.PublishedCount);
        Assert.Equal(410, c.GetLeavesForGroup("c64-tosec").Count);
        Assert.True(LeafDbExists("c64-tosec-000"));
        Assert.True(LeafDbExists("c64-tosec-409"));
        Assert.False(AnyTempFiles());
    }

    // ── Revalidation ──────────────────────────────────────────────────────────

    [Fact]  // (13) group id collision, before any filesystem mutation
    public void Revalidation_GroupIdCollision_AbortsWithoutMutation()
    {
        var c    = NewCatalog();
        SeedExistingGroup(c);
        var rel  = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.GroupIdCollision, r.ErrorCode);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, r.OverallStatus);
        Assert.False(LeafDbExists("c64-tosec-a"));
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (14) leaf id collision
    public void Revalidation_LeafIdCollision_Aborts()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "c64-tosec-a", HardwareFamilyId = "c64", Name = "other", Authority = "tosec",
                    MediaTypeId = "other", Version = "1", DataStorePath = "systems/c64/legacy.db", ReleaseCount = 0 },
        });
        var rel  = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.LeafIdCollision, r.ErrorCode);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, r.OverallStatus);
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (15) source DAT disappeared after the plan was frozen
    public void Revalidation_SourceMissing_Aborts()
    {
        var c    = NewCatalog();
        var rel  = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });
        File.Delete(Path.Combine(_srcRoot, "A", "a.dat"));

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.SourceMissing, r.ErrorCode);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    [Fact]  // (16) source relative path traversal
    public void Revalidation_SourceTraversal_Rejected()
    {
        var c = NewCatalog();
        var leaf = new DiscoveredDatLeaf
        {
            RelativePath = "../evil.dat", FileName = "evil.dat", SourcePath = "ignored",
            Status = DiscoveredDatLeafStatus.Parsed, DatName = "Evil", DatVersion = "1",
            Games = ImmutableArray.Create(new DiscoveredDatGame("G", "", "", "", "",
                ImmutableArray.Create(new DiscoveredDatRom("r", "1", "0", "", "")))),
        };
        var plan = new GroupDatReconciliationPlan(
            GroupDatReconciliationMode.NewGroup, _srcRoot, "c64", "C64", "tosec", "c64-tosec", "c64 tosec", "c64",
            ImmutableArray<GroupDatUpdateActionPlan>.Empty,
            ImmutableArray.Create(new GroupDatNewLeafPlan("c64-tosec-a", "other", "../evil.dat", "ignored", "Evil", "1", 1)),
            ImmutableArray<GroupDatAbsentLeafPlan>.Empty, ImmutableArray.Create(leaf));

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.SourcePathInvalid, r.ErrorCode);
        Assert.False(LeafDbExists("c64-tosec-a"));
    }

    [Fact]  // (17) reparse failure (corrupted source)
    public void Revalidation_ReparseFailure_Aborts()
    {
        var c    = NewCatalog();
        var rel  = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });
        File.WriteAllText(Path.Combine(_srcRoot, "A", "a.dat"), "<not-valid-xml");

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.ReparseFailed, r.ErrorCode);
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (18) parsed content differs from the frozen snapshot → stale plan
    public void Revalidation_ContentChanged_StalePlan()
    {
        var c    = NewCatalog();
        var rel  = WriteDat("A/a.dat", "AA", games: 2);
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });
        WriteDat("A/a.dat", "AA", games: 5);   // same header, different game count

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.StalePlan, r.ErrorCode);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (19) media type no longer exists
    public void Revalidation_MediaTypeMissing_Aborts()
    {
        var c    = NewCatalog();
        var rel  = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "no-such-media") });

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.MediaTypeMissing, r.ErrorCode);
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (20) pre-existing final DB without a catalog dat_line → block, never delete
    public void Revalidation_OrphanFinalDb_BlockedNotDeleted()
    {
        var c    = NewCatalog();
        var rel  = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });

        var final = FinalPath("c64-tosec-a");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.WriteAllText(final, "SENTINEL");

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.DestinationOccupied, r.ErrorCode);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, r.OverallStatus);
        Assert.Equal("SENTINEL", File.ReadAllText(final));   // neither overwritten nor deleted
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    [Fact]  // (21) one of many leaves fails revalidation → zero mutation for ALL leaves
    public void Revalidation_OneOfManyFails_ZeroMutation()
    {
        var c   = NewCatalog();
        var rA  = WriteDat("A/a.dat", "AA");
        var rB  = WriteDat("B/b.dat", "BB");
        var rC  = WriteDat("C/c.dat", "CC");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other"), (rC, "c64-tosec-c", "other") });
        File.Delete(Path.Combine(_srcRoot, "B", "b.dat"));   // middle leaf source gone

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.SourceMissing, r.ErrorCode);
        Assert.False(LeafDbExists("c64-tosec-a"));
        Assert.False(LeafDbExists("c64-tosec-c"));
        Assert.False(AnyTempFiles());
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    // ── Prepare ─────────────────────────────────────────────────────────────────

    [Fact]  // (22) prepare failure of leaf N → catalog untouched; (23) earlier temps removed
    public void Prepare_FailureOfLeafN_CleansTempsCatalogUntouched()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var rB   = WriteDat("B/b.dat", "BB");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other") });

        var r = Run(c, plan, svc => svc.OnLeafPreparedForTests = i => { if (i == 2) throw new InvalidOperationException("boom"); });

        Assert.Equal(GroupDatExecutionErrorCode.PrepareFailed, r.ErrorCode);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, r.OverallStatus);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.False(AnyTempFiles());   // both the failed and the already-prepared temp are gone
        Assert.False(LeafDbExists("c64-tosec-a"));
    }

    [Fact]  // (24) cancellation during prepare → Cancelled + temps cleaned
    public void Prepare_Cancellation_CleansUp()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var rB   = WriteDat("B/b.dat", "BB");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other") });

        using var cts = new CancellationTokenSource();
        var r = Run(c, plan, svc => svc.OnLeafPreparedForTests = i => { if (i == 1) cts.Cancel(); }, cts.Token);

        Assert.Equal(GroupDatExecutionStatus.Cancelled, r.OverallStatus);
        Assert.False(AnyTempFiles());
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    [Fact]  // (25) prepare failure + cleanup failure → CleanupRequired, no catalog writes
    public void Prepare_CleanupFailure_ReportsCleanupRequired()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var rB   = WriteDat("B/b.dat", "BB");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other") });

        var r = Run(c, plan, svc =>
        {
            svc.OnLeafPreparedForTests   = i => { if (i == 2) throw new InvalidOperationException("boom"); };
            svc.TryDeleteOverrideForTests = _ => false;   // simulate undeletable temp
        });

        Assert.Equal(GroupDatExecutionStatus.CleanupRequired, r.OverallStatus);
        Assert.Equal(GroupDatExecutionErrorCode.PrepareFailed, r.ErrorCode);
        Assert.NotEmpty(r.CleanupPaths);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    [Fact]  // (26) no rename starts before ALL builds complete (verify-all barrier)
    public void Publish_BarrierHolds_AllPreparedBeforeAnyPublished()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var rB   = WriteDat("B/b.dat", "BB");
        var rC   = WriteDat("C/c.dat", "CC");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other"), (rC, "c64-tosec-c", "other") });

        var order = new List<string>();
        var r = Run(c, plan, svc =>
        {
            svc.OnLeafPreparedForTests  = i => order.Add("P" + i);
            svc.OnLeafPublishedForTests = i => order.Add("U" + i);
        });

        Assert.Equal(GroupDatExecutionStatus.Committed, r.OverallStatus);
        int lastPrepared  = order.FindLastIndex(s => s[0] == 'P');
        int firstPublished = order.FindIndex(s => s[0] == 'U');
        Assert.True(lastPrepared < firstPublished, $"prepare/publish interleaved: {string.Join(",", order)}");
    }

    [Fact]  // (27) publish failure after some leaves → catalog untouched; (28)(29) finals+temps removed
    public void Publish_Failure_RemovesPublishedFinalsAndTemps()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var rB   = WriteDat("B/b.dat", "BB");
        var rC   = WriteDat("C/c.dat", "CC");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other"), (rC, "c64-tosec-c", "other") });

        var r = Run(c, plan, svc => svc.OnLeafPublishedForTests = i => { if (i == 2) throw new IOException("rename fail"); });

        Assert.Equal(GroupDatExecutionErrorCode.PublishFailed, r.ErrorCode);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, r.OverallStatus);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.False(LeafDbExists("c64-tosec-a"));   // published final removed
        Assert.False(LeafDbExists("c64-tosec-b"));
        Assert.False(AnyTempFiles());                // pending temp removed
    }

    [Fact]  // (30) publish failure + cleanup failure → CleanupRequired with exact paths
    public void Publish_CleanupFailure_ReportsCleanupRequired()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var rB   = WriteDat("B/b.dat", "BB");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other") });

        var r = Run(c, plan, svc =>
        {
            svc.OnLeafPublishedForTests   = i => { if (i == 2) throw new IOException("rename fail"); };
            svc.TryDeleteOverrideForTests = _ => false;
        });

        Assert.Equal(GroupDatExecutionStatus.CleanupRequired, r.OverallStatus);
        Assert.Equal(GroupDatExecutionErrorCode.PublishFailed, r.ErrorCode);
        Assert.NotEmpty(r.CleanupPaths);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    // ── Catalog commit ────────────────────────────────────────────────────────

    [Fact]  // (31)(32) catalog failure after publish → catalog empty, finals cleaned → AbortedNoWrites
    public void Catalog_FailureAfterPublish_CleansFinals_AbortedNoWrites()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var rB   = WriteDat("B/b.dat", "BB");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other"), (rB, "c64-tosec-b", "other") });

        c.OnBeforeCommitForTests = () => throw new InvalidOperationException("catalog boom");
        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.CatalogFailed, r.ErrorCode);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, r.OverallStatus);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));   // M2 rolled back
        Assert.False(LeafDbExists("c64-tosec-a"));                               // finals cleaned up
        Assert.False(LeafDbExists("c64-tosec-b"));
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (33) catalog rollback + cleanup failure → CleanupRequired + exact final paths
    public void Catalog_FailureWithCleanupFailure_ReportsCleanupRequired()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other") });

        c.OnBeforeCommitForTests = () => throw new InvalidOperationException("catalog boom");
        var r = Run(c, plan, svc => svc.TryDeleteOverrideForTests = _ => false);

        Assert.Equal(GroupDatExecutionStatus.CleanupRequired, r.OverallStatus);
        Assert.Equal(GroupDatExecutionErrorCode.CatalogFailed, r.ErrorCode);
        Assert.Contains(r.CleanupPaths, p => p.EndsWith("c64-tosec-a.db", StringComparison.Ordinal));
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    [Fact]  // (34) cancellation just before the catalog commit → no catalog + cleanup
    public void Catalog_CancelledBeforeCommit_NoCatalogAndCleanup()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other") });

        using var cts = new CancellationTokenSource();
        var r = Run(c, plan, svc => svc.OnLeafPublishedForTests = _ => cts.Cancel(), cts.Token);

        Assert.Equal(GroupDatExecutionStatus.Cancelled, r.OverallStatus);
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.False(LeafDbExists("c64-tosec-a"));
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (35) success → executor does NOT delete the final DBs
    public void Catalog_Success_KeepsFinalDbs()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other") });

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionStatus.Committed, r.OverallStatus);
        Assert.True(LeafDbExists("c64-tosec-a"));
    }

    // ── Retry / idempotence ─────────────────────────────────────────────────────

    [Fact]  // (36) retry after a controlled failure + successful cleanup → success
    public void Retry_AfterControlledFailureAndCleanup_Succeeds()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other") });

        c.OnBeforeCommitForTests = () => throw new InvalidOperationException("catalog boom");
        var first = Run(c, plan);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, first.OverallStatus);
        Assert.False(LeafDbExists("c64-tosec-a"));

        c.OnBeforeCommitForTests = null;               // failure cleared; source unchanged
        var second = Run(c, plan);

        Assert.Equal(GroupDatExecutionStatus.Committed, second.OverallStatus);
        Assert.True(LeafDbExists("c64-tosec-a"));
        Assert.Single(c.GetLeavesForGroup("c64-tosec"));
    }

    [Fact]  // (37) rerun after a successful Create → blocked without mutation
    public void Retry_AfterSuccess_BlockedWithoutMutation()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other") });

        Assert.Equal(GroupDatExecutionStatus.Committed, Run(c, plan).OverallStatus);

        var again = Run(c, plan);
        Assert.Equal(GroupDatExecutionErrorCode.GroupIdCollision, again.ErrorCode);
        Assert.Equal(GroupDatExecutionStatus.AbortedNoWrites, again.OverallStatus);
        Assert.Single(c.GetLeavesForGroup("c64-tosec"));   // existing group unchanged
        Assert.False(AnyTempFiles());
    }

    [Fact]  // (38) orphan final DB from a simulated prior crash → blocked, never overwritten/deleted
    public void Retry_OrphanFinalFromCrash_BlockedNeverTouched()
    {
        var c    = NewCatalog();
        var rA   = WriteDat("A/a.dat", "AA");
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other") });

        var final = FinalPath("c64-tosec-a");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.WriteAllText(final, "CRASH-RESIDUE");

        var r = Run(c, plan);

        Assert.Equal(GroupDatExecutionErrorCode.DestinationOccupied, r.ErrorCode);
        Assert.Equal("CRASH-RESIDUE", File.ReadAllText(final));
        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
    }

    // ── Structural preconditions (Part 2) ───────────────────────────────────────

    [Fact]
    public void InvalidPlan_WrongMode_Rejected()
    {
        var c   = NewCatalog();
        var rel = WriteDat("A/a.dat", "AA");
        var basePlan = BuildPlan(new[] { (rel, "c64-tosec-a", "other") });
        var plan = basePlan with { Mode = GroupDatReconciliationMode.UpdateGroup };

        var r = Run(c, plan);
        Assert.Equal(GroupDatExecutionErrorCode.InvalidPlan, r.ErrorCode);
    }

    [Fact]
    public void InvalidPlan_DiscoveredDatNotInPlan_Rejected()
    {
        var c   = NewCatalog();
        var rA  = WriteDat("A/a.dat", "AA");
        WriteDat("B/b.dat", "BB");                       // discovered but intentionally omitted from the plan
        var plan = BuildPlan(new[] { (rA, "c64-tosec-a", "other") });

        var r = Run(c, plan);
        Assert.Equal(GroupDatExecutionErrorCode.InvalidPlan, r.ErrorCode);
        Assert.False(AnyTempFiles());
    }
}

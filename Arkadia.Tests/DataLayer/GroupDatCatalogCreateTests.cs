using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Tests for the atomic Group-Create catalog foundation: <see cref="CatalogService.CreateDatGroupWithLeaves"/>
/// (single connection + single transaction, group + leaves + Group metadata + working states, all-or-nothing)
/// and <see cref="CatalogService.GetLeavesForGroup"/>. Real CatalogService over a temp catalog.db; no
/// filesystem leaf databases are built (that is the executor's job, out of scope here).
/// </summary>
public sealed class GroupDatCatalogCreateTests : IDisposable
{
    private readonly string _dir;

    public GroupDatCatalogCreateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkGrpCreate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogService NewCatalog(string systemId = "c64")
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = systemId, Name = systemId.ToUpperInvariant(), Manufacturer = "Commodore", HardwareTypeId = "" },
        });
        return c;
    }

    private static string Sha() => new string('a', 64);

    private static GroupDatCatalogLeafCreate Leaf(
        string id, string relPath, string media = "other", string authority = "tosec", string system = "c64",
        string? dataStorePath = null, string? sha256 = null, IReadOnlyList<GroupDatInitialWorkingState>? ws = null)
        => new()
        {
            DatLine = new DatLineRecord
            {
                Id = id, HardwareFamilyId = system, Name = id, Authority = authority, MediaTypeId = media,
                Version = "1", StorageStrategyId = "", DataStorePath = dataStorePath ?? $"systems/{system}/{id}.db",
                ReleaseCount = 0, ImportedAtUtc = DateTime.UtcNow,
            },
            RelativeDatPath      = relPath,
            SourceDatName        = relPath.Split('/').Last(),
            SourceDatSha256      = sha256 ?? Sha(),
            LastSeenGroupRevision = 0,
            InitialWorkingStates = ws ?? Array.Empty<GroupDatInitialWorkingState>(),
        };

    // Default request (System c64 / authority tosec / group c64-tosec); custom cases build inline.
    private static GroupDatCatalogCreateRequest R(params GroupDatCatalogLeafCreate[] leaves)
        => new() { GroupId = "c64-tosec", DisplayName = "C64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec", Leaves = leaves };

    private static GroupDatCatalogValidationException AssertRejected(Action a)
        => Assert.Throws<GroupDatCatalogValidationException>(a);

    // ── Success ─────────────────────────────────────────────────────────────────

    [Fact]  // (1) one leaf
    public void CreateWithOneLeaf_PersistsGroupAndLeaf()
    {
        var c = NewCatalog();
        c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/[D64].dat")));

        Assert.True(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        var leaves = c.GetLeavesForGroup("c64-tosec");
        var leaf   = Assert.Single(leaves);
        Assert.Equal("c64-tosec-a", leaf.DatLine.Id);
        Assert.Equal("c64-tosec", leaf.GroupMetadata.GroupId);
    }

    [Fact]  // (2) multiple leaves
    public void CreateWithManyLeaves_PersistsAll()
    {
        var c = NewCatalog();
        c.CreateDatGroupWithLeaves(R(
            Leaf("c64-tosec-a", "A/x.dat"), Leaf("c64-tosec-b", "B/y.dat"), Leaf("c64-tosec-c", "C/z.dat")));

        Assert.Equal(3, c.GetLeavesForGroup("c64-tosec").Count);
    }

    [Fact]  // (3) 410 leaves in one transaction
    public void CreateWith410Leaves_PersistsAll()
    {
        var c = NewCatalog();
        var leaves = Enumerable.Range(0, 410)
            .Select(i => Leaf($"c64-tosec-{i:D3}", $"Set{i:D3}/game.dat")).ToArray();
        c.CreateDatGroupWithLeaves(R(leaves));

        Assert.Equal(410, c.GetLeavesForGroup("c64-tosec").Count);
    }

    [Fact]  // (4)(5) revision 0 for group and every leaf
    public void Create_SetsRevisionZero_Everywhere()
    {
        var c = NewCatalog();
        c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat"), Leaf("c64-tosec-b", "B/y.dat")));

        Assert.Equal(0, c.GetDatGroup(DatGroupId.FromPersisted("c64-tosec"))!.CurrentRevision);
        Assert.All(c.GetLeavesForGroup("c64-tosec"), l => Assert.Equal(0, l.GroupMetadata.LastSeenGroupRevision));
    }

    [Fact]  // (6)(7)(8) metadata + SHA-256 + media/authority preserved
    public void Create_PersistsAllMetadata()
    {
        var c = NewCatalog();
        var sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        c.CreateDatGroupWithLeaves(R(
            Leaf("c64-tosec-games", "Applications/Test Disks/[NBZ]/games.dat", media: "floppy", sha256: sha)));

        var leaf = Assert.Single(c.GetLeavesForGroup("c64-tosec"));
        Assert.Equal("Applications/Test Disks/[NBZ]/games.dat", leaf.GroupMetadata.RelativeDatPath);
        Assert.Equal("games.dat", leaf.GroupMetadata.SourceDatName);
        Assert.Equal(sha, leaf.GroupMetadata.SourceDatSha256);
        Assert.Null(leaf.GroupMetadata.SemanticFingerprint);
        Assert.Null(leaf.GroupMetadata.SemanticFingerprintVersion);
        Assert.Equal("floppy", leaf.DatLine.MediaTypeId);
        Assert.Equal("tosec", leaf.DatLine.Authority);
    }

    [Fact]  // (9)(10) GetLeavesForGroup returns all, ordered by relative path then id
    public void GetLeavesForGroup_DeterministicOrder()
    {
        var c = NewCatalog();
        c.CreateDatGroupWithLeaves(R(
            Leaf("c64-tosec-z", "Zeta/z.dat"),
            Leaf("c64-tosec-a", "Alpha/a.dat"),
            Leaf("c64-tosec-m", "Mid/m.dat")));

        var paths = c.GetLeavesForGroup("c64-tosec").Select(l => l.GroupMetadata.RelativeDatPath).ToList();
        Assert.Equal(new[] { "Alpha/a.dat", "Mid/m.dat", "Zeta/z.dat" }, paths);
    }

    [Fact]  // (11) GetLeavesForGroup is a single query (no N+1) — 410 leaves load correctly and in order
    public void GetLeavesForGroup_LoadsManyInSingleQuery()
    {
        var c = NewCatalog();
        var leaves = Enumerable.Range(0, 410).Select(i => Leaf($"c64-tosec-{i:D3}", $"Set{i:D3}/g.dat")).ToArray();
        c.CreateDatGroupWithLeaves(R(leaves));

        var got = c.GetLeavesForGroup("c64-tosec");
        Assert.Equal(410, got.Count);
        Assert.Equal(got.Select(l => l.GroupMetadata.RelativeDatPath).OrderBy(x => x, StringComparer.Ordinal),
                     got.Select(l => l.GroupMetadata.RelativeDatPath));   // already ordered
    }

    [Fact]  // case-insensitive group_id lookup, empty for a group with no leaves
    public void GetLeavesForGroup_CaseInsensitive_AndEmpty()
    {
        var c = NewCatalog();
        c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat")));
        Assert.Single(c.GetLeavesForGroup("C64-TOSEC"));       // case-insensitive
        Assert.Empty(c.GetLeavesForGroup("no-such-group"));    // empty, not error
    }

    // ── Collisions & validation ─────────────────────────────────────────────────

    [Fact]  // (12)
    public void GroupIdCollision_CaseInsensitive_Rejected()
    {
        var c = NewCatalog();
        c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat")));
        // Group ids are stored COLLATE NOCASE; a new id must be lowercase-canonical, so re-creating the
        // same id is the observable collision (case-insensitive at the DB level).
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-b", "B/y.dat"))));
        Assert.Equal(GroupDatCatalogCreateError.GroupIdCollision, ex.Error);
    }

    [Fact]  // (13)
    public void LeafIdCollisionWithCatalog_CaseInsensitive_Rejected()
    {
        var c = NewCatalog();
        // A legacy mixed-case leaf id (SaveDatLines does not enforce the new-id policy) collides
        // case-insensitively with the valid lowercase leaf being created.
        c.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "C64-TOSEC-A", HardwareFamilyId = "c64", Name = "n", Authority = "tosec",
                    MediaTypeId = "other", DataStorePath = "systems/c64/existing.db", ImportedAtUtc = DateTime.UtcNow },
        });
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat"))));
        Assert.Equal(GroupDatCatalogCreateError.LeafIdCollision, ex.Error);
    }

    [Fact]  // (14)
    public void DuplicateLeafIdInPayload_CaseInsensitive_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(
            Leaf("c64-tosec-a", "A/x.dat"), Leaf("c64-tosec-a", "B/y.dat", dataStorePath: "systems/c64/other.db"))));
        Assert.Equal(GroupDatCatalogCreateError.DuplicateLeafIdInPayload, ex.Error);
    }

    [Fact]  // (15)
    public void InvalidHardwareFamily_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "C64 TOSEC", HardwareFamilyId = "nope", Authority = "tosec",
            Leaves = new[] { Leaf("c64-tosec-a", "A/x.dat", system: "nope") },
        }));
        Assert.Equal(GroupDatCatalogCreateError.HardwareFamilyMissing, ex.Error);
    }

    [Fact]  // (16)
    public void InvalidMediaType_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat", media: "nope"))));
        Assert.Equal(GroupDatCatalogCreateError.MediaTypeMissing, ex.Error);
    }

    [Fact]  // (17)
    public void MalformedSha256_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat", sha256: "xyz"))));
        Assert.Equal(GroupDatCatalogCreateError.InvalidSourceSha256, ex.Error);
    }

    [Fact]  // (18)
    public void RootedRelativeDatPath_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "/rooted/x.dat"))));
        Assert.Equal(GroupDatCatalogCreateError.InvalidRelativeDatPath, ex.Error);
    }

    [Fact]  // (19)
    public void TraversalRelativeDatPath_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/../x.dat"))));
        Assert.Equal(GroupDatCatalogCreateError.InvalidRelativeDatPath, ex.Error);
    }

    [Fact]  // (20)
    public void DuplicateDataStorePathInPayload_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(
            Leaf("c64-tosec-a", "A/x.dat", dataStorePath: "systems/c64/same.db"),
            Leaf("c64-tosec-b", "B/y.dat", dataStorePath: "systems/c64/same.db"))));
        Assert.Equal(GroupDatCatalogCreateError.DuplicateDataStorePathInPayload, ex.Error);
    }

    [Fact]  // (21)
    public void DataStorePathAlreadyInCatalog_Rejected()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "single-a", HardwareFamilyId = "c64", Name = "n", Authority = "tosec",
                    MediaTypeId = "other", DataStorePath = "systems/c64/taken.db", ImportedAtUtc = DateTime.UtcNow },
        });
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(
            R(Leaf("c64-tosec-a", "A/x.dat", dataStorePath: "systems/c64/taken.db"))));
        Assert.Equal(GroupDatCatalogCreateError.DataStorePathCollision, ex.Error);
    }

    [Fact]  // (22)
    public void LeafSystemMismatch_Rejected()
    {
        var c = NewCatalog();
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord> { new() { Id = "nes", Name = "NES", Manufacturer = "N", HardwareTypeId = "" } });
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat", system: "nes"))));
        Assert.Equal(GroupDatCatalogCreateError.LeafSystemMismatch, ex.Error);
    }

    [Fact]  // (23)
    public void LeafAuthorityMismatch_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat", authority: "nointro"))));
        Assert.Equal(GroupDatCatalogCreateError.LeafAuthorityMismatch, ex.Error);
    }

    [Fact]  // (24)
    public void ZeroLeaves_Rejected()
    {
        var c = NewCatalog();
        var ex = AssertRejected(() => c.CreateDatGroupWithLeaves(R()));
        Assert.Equal(GroupDatCatalogCreateError.NoLeaves, ex.Error);
    }

    // ── Foreign keys active during the transaction ──────────────────────────────

    [Fact]  // PRAGMA foreign_keys must be ON (=1) on the live connection inside the transaction
    public void ForeignKeysAreEnabled_DuringTransaction()
    {
        var c = NewCatalog();
        long fk = -1;
        c.OnTransactionOpenedForTests = conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys;";   // reading a pragma mid-transaction returns its live value
            fk = (long)cmd.ExecuteScalar()!;
        };

        c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat")));

        Assert.Equal(1, fk);   // FK enforcement was active for the create transaction
    }

    // ── Atomicity / rollback ─────────────────────────────────────────────────────

    [Fact]  // (25) failure on the 2nd leaf → group and all leaves absent
    public void FailureOnSecondLeaf_RollsBackEverything()
    {
        var c = NewCatalog();
        c.OnLeafInsertedForTests = i => { if (i == 2) throw new InvalidOperationException("boom-2nd-leaf"); };

        Assert.Throws<InvalidOperationException>(() => c.CreateDatGroupWithLeaves(R(
            Leaf("c64-tosec-a", "A/x.dat"), Leaf("c64-tosec-b", "B/y.dat"), Leaf("c64-tosec-c", "C/z.dat"))));

        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.Empty(c.GetLeavesForGroup("c64-tosec"));
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]  // (26) failure right after the group insert → total rollback
    public void FailureAfterGroupInsert_RollsBackGroup()
    {
        var c = NewCatalog();
        c.OnLeafInsertedForTests = i => { if (i == 1) throw new InvalidOperationException("boom-1st-leaf"); };

        Assert.Throws<InvalidOperationException>(() => c.CreateDatGroupWithLeaves(
            R(Leaf("c64-tosec-a", "A/x.dat"), Leaf("c64-tosec-b", "B/y.dat"))));

        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]  // (27) working-state phase failure → total rollback (incl. the working states)
    public void FailureBeforeCommit_RollsBackWorkingStates()
    {
        var c = NewCatalog();
        c.OnBeforeCommitForTests = () => throw new InvalidOperationException("boom-before-commit");

        var leaf = Leaf("c64-tosec-a", "A/x.dat", ws: new[] { new GroupDatInitialWorkingState("Game A", "working") });
        Assert.Throws<InvalidOperationException>(() => c.CreateDatGroupWithLeaves(R(leaf)));

        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.Empty(c.LoadDatLines());
        Assert.Null(c.GetWorkingState("Game A"));   // working state rolled back too
    }

    [Fact]  // (28) cancellation before commit → total rollback
    public void CancellationBeforeCommit_RollsBackEverything()
    {
        var c = NewCatalog();
        using var cts = new CancellationTokenSource();
        c.OnLeafInsertedForTests = i => { if (i == 1) cts.Cancel(); };   // cancel after the first leaf

        Assert.Throws<OperationCanceledException>(() => c.CreateDatGroupWithLeaves(
            R(Leaf("c64-tosec-a", "A/x.dat"), Leaf("c64-tosec-b", "B/y.dat")), cts.Token));

        Assert.False(c.DatGroupExists(DatGroupId.FromPersisted("c64-tosec")));
        Assert.Empty(c.LoadDatLines());
    }

    // ── No filesystem / no Single-DAT regression ────────────────────────────────

    [Fact]  // (29) the catalog method performs no filesystem work (no leaf DBs created)
    public void Create_PerformsNoFilesystemWork()
    {
        var c = NewCatalog();
        c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat")));

        // The method never builds leaf databases — only catalog.db exists under the data dir.
        Assert.False(Directory.Exists(Path.Combine(_dir, "systems")));
        Assert.False(File.Exists(Path.Combine(_dir, "systems", "c64", "c64-tosec-a.db")));
    }

    [Fact]  // (30) Single-DAT dat_lines are unaffected and coexist (group_id NULL, excluded from the group)
    public void SingleDatLines_Unaffected_AndExcluded()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord>
        {
            new() { Id = "single-a", HardwareFamilyId = "c64", Name = "Single", Authority = "tosec",
                    MediaTypeId = "other", DataStorePath = "systems/c64/single-a.db", ImportedAtUtc = DateTime.UtcNow },
        });
        c.CreateDatGroupWithLeaves(R(Leaf("c64-tosec-a", "A/x.dat")));

        // Single line still loads and is NOT part of the group.
        Assert.Contains(c.LoadDatLines(), dl => dl.Id == "single-a");
        Assert.DoesNotContain(c.GetLeavesForGroup("c64-tosec"), l => l.DatLine.Id == "single-a");
        Assert.Null(c.GetDatLineGroupMetadata("single-a")!.GroupId);   // Single leaf has no group_id
    }
}

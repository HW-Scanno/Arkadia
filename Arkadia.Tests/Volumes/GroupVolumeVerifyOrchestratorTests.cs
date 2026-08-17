using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Group Verify Volume orchestration (V-G5): the Group is a sequential orchestrator that reuses the exact Single
/// engine (<see cref="VolumeVerifyService.VerifyCore"/>) leaf-by-leaf. Proves per-leaf root/store/assignment
/// scoping, same-filename-across-leaves independence, in-leaf recovery/managed-folder placement, group-root
/// reachability reconciliation, aggregate health, pre-flight blocking, and stop-on-first-exception. Real temp
/// catalog + leaf DBs + physical group root; no UI.
/// </summary>
public sealed class GroupVolumeVerifyOrchestratorTests : IDisposable
{
    private const string Group = "c64-tosec";
    private readonly string _dir;        // dataDir (catalog + leaf DBs)
    private readonly string _groupRoot;  // physical group volume root

    public GroupVolumeVerifyOrchestratorTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ArkGVVerify_" + Guid.NewGuid().ToString("N")[..8]);
        _dir       = Path.Combine(baseDir, "data");
        _groupRoot = Path.Combine(baseDir, "volumes", "c64-tosec-001");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_groupRoot);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, true); } catch { }
    }

    private static string Sha1Hex(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    private CatalogService NewCatalog()
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "c64", Name = "C64", Manufacturer = "Commodore", HardwareTypeId = "console" } });
        c.CreateDatGroup(
            DatGroupId.TryCreateNew(Group, out var id, out _, out _) ? id : throw new InvalidOperationException(),
            "Commodore 64 TOSEC", "c64", "tosec");
        return c;
    }

    private VolumeRecord GroupVolume(CatalogService c, string status = "present")
    {
        var v = new VolumeRecord
        {
            Id = "gv", Label = "c64-tosec-001", PlatformId = "c64", DatLineId = null, GroupId = Group,
            Status = status, Health = status == "lost" ? "crit" : "ok",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        return v;
    }

    // Seeds a leaf DB (release missing + CI + link + derived artifact hashed to the content) and assigns it to
    // the group volume. Returns (leaf record, daId, sha1). Does NOT write the physical file.
    private (GroupLeafRecord Leaf, string DaId, string Sha1) SeedLeaf(
        CatalogService c, VolumeRecord v, string leafId, string relId, string fileName, byte[] content)
    {
        var dsp = $"systems/c64/{leafId}.db";
        var abs = Path.Combine(_dir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var sha1 = Sha1Hex(content);
        var cik  = "sha1:" + sha1 + ":" + leafId;

        var store = new DatLineStore(abs);
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = leafId, Name = "Game " + relId, Status = "missing", ReleaseContentKey = cik });
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });

        var daId = store.IngestDerivedArtifact(cik, "", "no_compression", fileName,
            $"archive/c64/{leafId}/{relId}/{fileName}", content.Length, sha1);
        store.BatchUpdateDerivedArtifactStatus(new[] { daId }, "missing");   // pre-verification baseline state

        c.SaveVolumeArtifact(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = v.Id, DatLineId = leafId,
            DerivedArtifactId = daId, ContentIdentityKey = cik, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        });

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId + " (display)", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = leafId, GroupId = Group });
        return (leaf, daId, sha1);
    }

    private string WriteAt(string segment, string fileName, byte[] content, string? subfolder = null)
    {
        var dir  = subfolder is null ? Path.Combine(_groupRoot, segment) : Path.Combine(_groupRoot, segment, subfolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private List<string> AllDbPaths(CatalogService c)
        => c.LoadDatLines().Where(dl => dl.DataStorePath.Length > 0)
            .Select(dl => Path.Combine(_dir, dl.DataStorePath)).Where(File.Exists).ToList();

    private static string DaStatus(string dbPath, string daId)
        => new DatLineStore(dbPath).GetDerivedArtifacts().Single(d => d.Id == daId).Status;

    // ── Happy path: multi-leaf, nested layout preserved, GroupId never substituted ──

    [Fact]
    public void Verify_MultiLeaf_AllPresentAtNestedPaths_Healthy()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var ca = System.Text.Encoding.UTF8.GetBytes("firmware-content");
        var cb = System.Text.Encoding.UTF8.GetBytes("games-content-different");
        var (a, daA, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, daB, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);   // SAME filename
        var pathA = WriteAt("firmware-crt", "game.rom", ca);
        var pathB = WriteAt("games-action-d64", "game.rom", cb);

        var outcome = GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, new[] { a, b }, AllDbPaths(c));

        Assert.Null(outcome.BlockReason);
        Assert.True(outcome.Result!.IsHealthy);
        Assert.Equal(2, outcome.Result.Verified);
        // Files remain at their nested paths (no flattening to group root).
        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));
        Assert.False(File.Exists(Path.Combine(_groupRoot, "game.rom")));
        // DA status updated in each EXACT leaf store (GroupId never used as owner).
        Assert.Equal("present", DaStatus(Path.Combine(_dir, a.DatLine.DataStorePath), daA));
        Assert.Equal("present", DaStatus(Path.Combine(_dir, b.DatLine.DataStorePath), daB));
        // The group volume's assignment rows keep the exact leaf owner.
        var vas = c.GetVolumeArtifacts(v.Id);
        Assert.Contains(vas, r => r.DatLineId == "c64-tosec-firmware-crt");
        Assert.Contains(vas, r => r.DatLineId == "c64-tosec-games-action-d64");
        Assert.DoesNotContain(vas, r => r.DatLineId == Group);
    }

    // ── Misplaced file is restored INSIDE its own leaf (not the group root) ──

    [Fact]
    public void Verify_MisplacedWithinLeaf_RestoredInsideLeaf()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var content = System.Text.Encoding.UTF8.GetBytes("fw");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt", "r1", "game.rom", content);
        WriteAt("firmware-crt", "game.rom", content, subfolder: "Release Name");   // misplaced under a subfolder

        var outcome = GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, new[] { a }, AllDbPaths(c));

        Assert.Null(outcome.BlockReason);
        Assert.Equal(1, outcome.Result!.MisplacedRestored);
        Assert.True(File.Exists(Path.Combine(_groupRoot, "firmware-crt", "game.rom")));       // restored inside leaf
        Assert.False(File.Exists(Path.Combine(_groupRoot, "game.rom")));                       // NOT at group root
    }

    // ── Unknown file is quarantined inside the LEAF's own unknown\ ──

    [Fact]
    public void Verify_UnknownFile_MovedToLeafUnknownFolder()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var content = System.Text.Encoding.UTF8.GetBytes("fw");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt", "r1", "game.rom", content);
        WriteAt("firmware-crt", "game.rom", content);
        WriteAt("firmware-crt", "stray.bin", System.Text.Encoding.UTF8.GetBytes("not-in-any-dat"));

        var outcome = GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, new[] { a }, AllDbPaths(c));

        Assert.Equal(1, outcome.Result!.UnknownMoved);
        Assert.True(File.Exists(Path.Combine(_groupRoot, "firmware-crt", "unknown", "stray.bin")));
        Assert.False(File.Exists(Path.Combine(_groupRoot, "unknown", "stray.bin")));           // not at group root
    }

    // ── Missing leaf folder + reachable group root → reconciled as missing ──

    [Fact]
    public void Verify_MissingLeafFolder_ReachableGroupRoot_ReconcilesMissing()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var ca = System.Text.Encoding.UTF8.GetBytes("fw");
        var cb = System.Text.Encoding.UTF8.GetBytes("games");
        var (a, _, _)   = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, daB, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("firmware-crt", "game.rom", ca);
        // leaf-b's segment folder is never created — its content is genuinely missing.

        Assert.Equal(2, c.GetVolumeArtifacts(v.Id).Count);
        var outcome = GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, new[] { a, b }, AllDbPaths(c));

        Assert.False(outcome.Result!.IsHealthy);                          // aggregate not healthy
        Assert.Equal(1, outcome.Result.Missing);
        Assert.Equal(1, outcome.Result.StaleAssignmentsRemoved);          // group-root reachable → reconciled
        Assert.Single(c.GetVolumeArtifacts(v.Id));                        // leaf-b assignment removed
        Assert.Equal("missing", DaStatus(Path.Combine(_dir, b.DatLine.DataStorePath), daB));
    }

    // ── One unhealthy leaf → aggregate crit ──

    [Fact]
    public void Verify_OneUnhealthyLeaf_AggregateNotHealthy()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var ca = System.Text.Encoding.UTF8.GetBytes("fw");
        var cb = System.Text.Encoding.UTF8.GetBytes("games");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, _, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("firmware-crt", "game.rom", ca);
        WriteAt("games-action-d64", "game.rom", System.Text.Encoding.UTF8.GetBytes("WRONG-CONTENT"));  // hash mismatch

        var outcome = GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, new[] { a, b }, AllDbPaths(c));

        Assert.False(outcome.Result!.IsHealthy);
        Assert.Equal(1, outcome.Result.Verified);   // only leaf-a
    }

    // ── Pre-flight blocks BEFORE any leaf verify (no mutation) ──

    [Fact]
    public void Verify_AssignedLeafNotInMembership_BlocksBeforeAnyVerify()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var content = System.Text.Encoding.UTF8.GetBytes("fw");
        var (a, daA, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt", "r1", "game.rom", content);
        WriteAt("firmware-crt", "game.rom", content);

        var outcome = GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, Array.Empty<GroupLeafRecord>(), AllDbPaths(c));

        Assert.NotNull(outcome.BlockReason);
        Assert.Contains("c64-tosec-firmware-crt", outcome.BlockReason!);
        Assert.Null(outcome.Result);
        // Nothing mutated: DA still 'missing', assignment intact.
        Assert.Equal("missing", DaStatus(Path.Combine(_dir, a.DatLine.DataStorePath), daA));
        Assert.Single(c.GetVolumeArtifacts(v.Id));
    }

    [Fact]
    public void Verify_MissingLeafDb_Blocks()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt", "r1", "game.rom", System.Text.Encoding.UTF8.GetBytes("fw"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(_dir, a.DatLine.DataStorePath));

        var outcome = GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, new[] { a }, AllDbPaths(c));

        Assert.NotNull(outcome.BlockReason);
        Assert.Contains("c64-tosec-firmware-crt", outcome.BlockReason!);
    }

    // ── Stop on first leaf exception; earlier leaf progress remains committed ──

    [Fact]
    public void Verify_LeafException_StopsAndKeepsEarlierProgress()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var ca = System.Text.Encoding.UTF8.GetBytes("fw-content");
        var cb = System.Text.Encoding.UTF8.GetBytes("games-content");
        // Ordinal order: "c64-tosec-firmware-crt" < "c64-tosec-games-action-d64" → firmware runs first.
        var (a, daA, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, daB, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("firmware-crt", "game.rom", ca);
        var lockedPath = WriteAt("games-action-d64", "game.rom", cb);

        // Exclusively lock leaf-b's file so its SHA1 read throws mid-orchestration.
        using (var _lock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() =>
                GroupVolumeVerifyOrchestrator.Verify(c, _dir, v, _groupRoot, new[] { a, b }, AllDbPaths(c)));
        }

        // leaf-a (which ran first) committed its present status; leaf-b never committed.
        Assert.Equal("present", DaStatus(Path.Combine(_dir, a.DatLine.DataStorePath), daA));
        Assert.Equal("missing", DaStatus(Path.Combine(_dir, b.DatLine.DataStorePath), daB));
    }

    // ── VerifyCore leaf scoping: a sibling leaf directory is invisible ──

    [Fact]
    public void VerifyCore_ScopedToLeafRoot_SiblingLeafInvisible()
    {
        var c = NewCatalog();
        var v = GroupVolume(c);
        var ca = System.Text.Encoding.UTF8.GetBytes("fw");
        var cb = System.Text.Encoding.UTF8.GetBytes("games");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (_, _, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("firmware-crt", "game.rom", ca);
        WriteAt("games-action-d64", "game.rom", cb);

        var svc      = new VolumeVerifyService(c);
        var scopedA  = c.GetVolumeArtifacts(v.Id).Where(x => x.DatLineId == "c64-tosec-firmware-crt").ToList();
        var leafRoot = Path.Combine(_groupRoot, "firmware-crt");
        var res = svc.VerifyCore(v.Id, scopedA, leafRoot,
            new DatLineStore(Path.Combine(_dir, a.DatLine.DataStorePath)), AllDbPaths(c), reachableOverride: true);

        Assert.Equal(1, res.TotalScanned);   // only firmware-crt/game.rom — the sibling leaf is outside scopedRoot
        Assert.Equal(1, res.Verified);
    }
}

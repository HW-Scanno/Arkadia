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
/// Recovery of a Group volume whose Apply &amp; Build finalization was interrupted (the real c64-tosec-001 case:
/// INIT + committed assignments + materialized physical files, but no init→present / location commit).
/// <see cref="GroupVolumeRecoveryService"/> REUSES the existing Group Verify engine
/// (<see cref="GroupVolumeVerifyOrchestrator"/> → <see cref="VolumeVerifyService.VerifyCore"/>) to hash-verify every
/// leaf, and finalizes ONLY when the whole volume is healthy. Real temp catalog + leaf DBs + physical group root.
/// </summary>
public sealed class GroupVolumeRecoveryServiceTests : IDisposable
{
    private const string Group = "c64-tosec";
    private readonly string _baseDir;    // appRoot (contains data\ and volumes\)
    private readonly string _dir;        // dataDir (catalog + leaf DBs)
    private readonly string _groupRoot;  // physical group volume root: <base>\volumes\c64-tosec-001

    public GroupVolumeRecoveryServiceTests()
    {
        _baseDir   = Path.Combine(Path.GetTempPath(), "ArkGVRecover_" + Guid.NewGuid().ToString("N")[..8]);
        _dir       = Path.Combine(_baseDir, "data");
        _groupRoot = Path.Combine(_baseDir, "volumes", "c64-tosec-001");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_groupRoot);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_baseDir, true); } catch { }
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

    private VolumeRecord GroupVolume(CatalogService c, string volumeId, string label, string status = "init")
    {
        var v = new VolumeRecord
        {
            Id = volumeId, Label = label, PlatformId = "c64", DatLineId = null, GroupId = Group,
            Status = status, Health = "ok",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        return v;
    }

    // Seeds a leaf DB (release + CI + link + derived artifact) and assigns it to the given volume. Returns
    // (leaf record, daId, sha1). Does NOT write the physical file — the caller materializes it (or not).
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
        // Interrupted-build baseline: the DA/release status was NOT finalized to 'present' before the crash.
        store.BatchUpdateDerivedArtifactStatus(new[] { daId }, "missing");

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

    // The leaf physical segment is the DatLineId with the "c64-tosec-" prefix stripped (GroupRelativeLeafPath).
    private static string Segment(string leafId) => GroupRelativeLeafPath.Segment(Group, leafId);

    private string WriteAt(string leafId, string fileName, byte[] content)
    {
        var dir = Path.Combine(_groupRoot, Segment(leafId));
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

    private GroupVolumeRecoveryResult Recover(CatalogService c, VolumeRecord v, params GroupLeafRecord[] leaves)
        => GroupVolumeRecoveryService.Recover(
            c, _dir, _baseDir, c.GetVolumeById(v.Id)!, _groupRoot, diskId: null, leaves, AllDbPaths(c));

    private int PhysicalFileCount()
        => Directory.Exists(_groupRoot) ? Directory.EnumerateFiles(_groupRoot, "*", SearchOption.AllDirectories).Count() : 0;

    // ── 1. Interrupted finalization → verified, finalized, no recopy, no duplicate assignments ──

    [Fact]
    public void Recover_InterruptedButComplete_VerifiesAndFinalizes_NoRecopy()
    {
        var c  = NewCatalog();
        var v  = GroupVolume(c, "gv", "c64-tosec-001");
        var ca = System.Text.Encoding.UTF8.GetBytes("firmware-content");
        var cb = System.Text.Encoding.UTF8.GetBytes("games-content-different");
        var (a, daA, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, daB, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("c64-tosec-firmware-crt",     "game.rom", ca);
        WriteAt("c64-tosec-games-action-d64", "game.rom", cb);
        int filesBefore = PhysicalFileCount();

        var rec = Recover(c, v, a, b);

        Assert.Equal(GroupVolumeRecoveryStatus.Finalized, rec.Status);
        Assert.True(rec.Verify!.IsHealthy);
        Assert.Equal(2, rec.Verify.Verified);

        // Lifecycle finalized: init → present + current workspace location.
        var fv = c.GetVolumeById(v.Id)!;
        Assert.Equal("present", fv.Status);
        var loc = c.GetCurrentLocation(v.Id);
        Assert.NotNull(loc);
        Assert.Equal("workspace", loc!.LocationType);
        Assert.Equal(_groupRoot, loc.Path);

        // Per-leaf status finalized to present in each exact leaf store.
        Assert.Equal("present", DaStatus(Path.Combine(_dir, a.DatLine.DataStorePath), daA));
        Assert.Equal("present", DaStatus(Path.Combine(_dir, b.DatLine.DataStorePath), daB));

        // No recopy, no duplicate assignments.
        Assert.Equal(filesBefore, PhysicalFileCount());
        Assert.Equal(2, c.GetVolumeArtifacts(v.Id).Count);
    }

    // ── 2. A missing expected artifact blocks finalization (no false PRESENT) ──

    [Fact]
    public void Recover_MissingArtifact_DoesNotFinalize()
    {
        var c  = NewCatalog();
        var v  = GroupVolume(c, "gv", "c64-tosec-001");
        var ca = System.Text.Encoding.UTF8.GetBytes("fw");
        var cb = System.Text.Encoding.UTF8.GetBytes("games");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, _, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("c64-tosec-firmware-crt", "game.rom", ca);
        // leaf-b's file is never written → genuinely missing.

        var rec = Recover(c, v, a, b);

        Assert.Equal(GroupVolumeRecoveryStatus.NotFinalized, rec.Status);
        Assert.False(rec.Verify!.IsHealthy);
        Assert.True(rec.Verify.Missing >= 1);

        var fv = c.GetVolumeById(v.Id)!;
        Assert.Equal("init", fv.Status);            // NOT promoted
        Assert.Null(c.GetCurrentLocation(v.Id));    // no location created
    }

    // ── 3. A hash mismatch blocks finalization ──

    [Fact]
    public void Recover_HashMismatch_DoesNotFinalize()
    {
        var c  = NewCatalog();
        var v  = GroupVolume(c, "gv", "c64-tosec-001");
        var ca = System.Text.Encoding.UTF8.GetBytes("fw");
        var cb = System.Text.Encoding.UTF8.GetBytes("games");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, _, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("c64-tosec-firmware-crt",     "game.rom", ca);
        WriteAt("c64-tosec-games-action-d64", "game.rom", System.Text.Encoding.UTF8.GetBytes("WRONG-CONTENT"));

        var rec = Recover(c, v, a, b);

        Assert.Equal(GroupVolumeRecoveryStatus.NotFinalized, rec.Status);
        Assert.False(rec.Verify!.IsHealthy);
        Assert.Equal("init", c.GetVolumeById(v.Id)!.Status);   // no lifecycle transition
    }

    // ── 4. Fully valid recovery → exact post-build lifecycle (present + workspace location) ──

    [Fact]
    public void Recover_FullyValid_ProducesNormalPostBuildLifecycle()
    {
        var c  = NewCatalog();
        var v  = GroupVolume(c, "gv", "c64-tosec-001");
        var ca = System.Text.Encoding.UTF8.GetBytes("only-content");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt", "r1", "game.rom", ca);
        WriteAt("c64-tosec-firmware-crt", "game.rom", ca);

        var rec = Recover(c, v, a);

        Assert.Equal(GroupVolumeRecoveryStatus.Finalized, rec.Status);
        var fv = c.GetVolumeById(v.Id)!;
        Assert.Equal("present", fv.Status);
        Assert.Equal("ok", fv.Health);
        var loc = c.GetCurrentLocation(v.Id)!;
        Assert.True(loc.IsCurrent);
        Assert.Equal("workspace", loc.LocationType);
        Assert.Null(loc.DiskId);
    }

    // ── 5. Idempotent: a second recovery is a stable no-op (no dupes, no double count, no recopy) ──

    [Fact]
    public void Recover_RunTwice_IsIdempotent()
    {
        var c  = NewCatalog();
        var v  = GroupVolume(c, "gv", "c64-tosec-001");
        var ca = System.Text.Encoding.UTF8.GetBytes("fw");
        var cb = System.Text.Encoding.UTF8.GetBytes("games");
        var (a, _, _) = SeedLeaf(c, v, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, _, _) = SeedLeaf(c, v, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("c64-tosec-firmware-crt",     "game.rom", ca);
        WriteAt("c64-tosec-games-action-d64", "game.rom", cb);

        var first = Recover(c, v, a, b);
        Assert.Equal(GroupVolumeRecoveryStatus.Finalized, first.Status);

        int vaAfterFirst    = c.GetVolumeArtifacts(v.Id).Count;
        long sizeAfterFirst = c.GetVolumeById(v.Id)!.ActualSizeBytes;
        int filesAfterFirst = PhysicalFileCount();

        var second = Recover(c, v, a, b);

        Assert.Equal(GroupVolumeRecoveryStatus.NotInterrupted, second.Status);   // already present — not re-run
        Assert.Equal("present", c.GetVolumeById(v.Id)!.Status);                  // stable
        Assert.Equal(vaAfterFirst,    c.GetVolumeArtifacts(v.Id).Count);         // no duplicate assignments
        Assert.Equal(sizeAfterFirst,  c.GetVolumeById(v.Id)!.ActualSizeBytes);   // no size double-count
        Assert.Equal(filesAfterFirst, PhysicalFileCount());                      // no recopy
        Assert.Equal(_groupRoot, c.GetCurrentLocation(v.Id)!.Path);              // stable current location
    }

    // ── 6. Recovery of one volume never touches another volume ──

    [Fact]
    public void Recover_DoesNotAffectAnotherVolume()
    {
        var c  = NewCatalog();
        var v1 = GroupVolume(c, "gv1", "c64-tosec-001");
        var v2 = GroupVolume(c, "gv2", "c64-tosec-002");   // separate INIT group volume, its own assignment
        var ca = System.Text.Encoding.UTF8.GetBytes("fw");
        var cb = System.Text.Encoding.UTF8.GetBytes("games");
        var (a, _, _)   = SeedLeaf(c, v1, "c64-tosec-firmware-crt",     "r1", "game.rom", ca);
        var (b, daB, _) = SeedLeaf(c, v2, "c64-tosec-games-action-d64", "r2", "game.rom", cb);
        WriteAt("c64-tosec-firmware-crt", "game.rom", ca);   // only v1's material exists under _groupRoot

        var rec = Recover(c, v1, a, b);

        Assert.Equal(GroupVolumeRecoveryStatus.Finalized, rec.Status);
        // v2 is completely untouched: still INIT, its assignment and DA baseline intact, no location.
        Assert.Equal("init", c.GetVolumeById(v2.Id)!.Status);
        Assert.Single(c.GetVolumeArtifacts(v2.Id));
        Assert.Null(c.GetCurrentLocation(v2.Id));
        Assert.Equal("missing", DaStatus(Path.Combine(_dir, b.DatLine.DataStorePath), daB));
    }

    // ── Guard: an empty INIT volume is a fresh (un-built) volume, not an interrupted build ──

    [Fact]
    public void Recover_EmptyInitVolume_NothingToRecover()
    {
        var c = NewCatalog();
        var v = GroupVolume(c, "gv", "c64-tosec-001");

        var rec = Recover(c, v);

        Assert.Equal(GroupVolumeRecoveryStatus.NothingToRecover, rec.Status);
        Assert.Equal("init", c.GetVolumeById(v.Id)!.Status);
    }
}

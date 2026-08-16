using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Read-only Group volume planning front-end: reuses per-leaf Single eligibility over real temp leaf DBs,
/// blocks on missing/unreadable leaves, and mutates nothing (no volume_artifacts, no size change, no locations,
/// no volume directory).
/// </summary>
public sealed class GroupVolumePlanningServiceTests : IDisposable
{
    private readonly string _dir;

    public GroupVolumePlanningServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkGVPlan_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch { } }

    private CatalogService NewCatalogWithGroup(string family = "c64", string gid = "c64-tosec")
    {
        var c = new CatalogService(_dir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = family, Name = "C64", Manufacturer = "Commodore", HardwareTypeId = "console" } });
        c.CreateDatGroup(DatGroupId.TryCreateNew(gid, out var id, out _, out _) ? id
            : throw new InvalidOperationException(), "Commodore 64 TOSEC", family, "tosec");
        return c;
    }

    private VolumeRecord GroupVolume(CatalogService c, string gid = "c64-tosec", long capacity = 1_000_000)
    {
        var v = new VolumeRecord
        {
            Id = "gv", Label = "c64-tosec-001", PlatformId = "c64", DatLineId = null, GroupId = gid,
            Status = "init", PlannedSizeBytes = capacity, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        return v;
    }

    // Seeds a leaf DB (release + content link + derived artifact) and returns its GroupLeafRecord + daId.
    private (GroupLeafRecord Leaf, string DaId) SeedLeaf(
        string leafId, string relId, long size, bool writeArchive)
    {
        var dsp = $"systems/c64/{leafId}.db";
        var abs = Path.Combine(_dir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        var store = new DatLineStore(abs);   // TEST setup uses the writable store; the SERVICE uses OpenReadOnly
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = leafId, Name = "Game " + relId, Status = "present" });
        var cik = "ck-" + leafId + "-" + relId;
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = "sha", DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });

        var relPath = $"archive/c64/{leafId}/{relId}.chd";
        var daId    = store.IngestDerivedArtifact(cik, "", "chd", $"{relId}.chd", relPath, size, "sha");

        if (writeArchive)
        {
            var arch = Path.Combine(_dir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(arch)!);
            File.WriteAllBytes(arch, new byte[size]);
        }

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId + " (display)", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = leafId, GroupId = "c64-tosec" });
        return (leaf, daId);
    }

    private static GroupLeafRecord MissingLeaf(string leafId) => new(
        new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId, DataStorePath = $"systems/c64/{leafId}.db" },
        new DatLineGroupMetadataRecord { DatLineId = leafId });

    [Fact] // read-only plan includes an eligible candidate and mutates nothing
    public void Plan_IncludesEligible_NoMutation()
    {
        var c = NewCatalogWithGroup();
        var v = GroupVolume(c);
        var (leaf, _) = SeedLeaf("leaf-a", "r1", 100, writeArchive: true);

        int vaBefore   = c.GetVolumeArtifacts(v.Id).Count;
        long sizeBefore = c.GetVolumeById(v.Id)!.ActualSizeBytes;

        var r = GroupVolumePlanningService.Plan(c, _dir, _dir, v, new[] { leaf });

        Assert.Equal(GroupVolumeReadiness.Ready, r.Readiness);
        Assert.Equal(1, r.IncludeCount);
        Assert.Equal("include", r.Leaves.Single().Items.Single().Decision.Decision);

        // No mutation: no volume_artifacts, unchanged size, no location, no volume directory.
        Assert.Equal(vaBefore, c.GetVolumeArtifacts(v.Id).Count);
        Assert.Equal(sizeBefore, c.GetVolumeById(v.Id)!.ActualSizeBytes);
        Assert.Null(c.GetCurrentLocation(v.Id));
        Assert.False(Directory.Exists(Path.Combine(_dir, "volumes")));
    }

    [Fact] // a missing leaf DB is a blocking diagnostic, never a silent omission or a created DB
    public void Plan_MissingLeafDb_Blocked()
    {
        var c = NewCatalogWithGroup();
        var v = GroupVolume(c);

        var r = GroupVolumePlanningService.Plan(c, _dir, _dir, v, new[] { MissingLeaf("leaf-x") });

        Assert.Equal(GroupVolumeReadiness.Blocked, r.Readiness);
        Assert.Contains(r.Diagnostics, d => d.Contains("leaf-x"));
        Assert.False(File.Exists(Path.Combine(_dir, "systems/c64/leaf-x.db")));   // never created
    }

    [Fact] // an archive-incomplete candidate is skipped exactly as Single
    public void Plan_ArchiveIncomplete_Skipped()
    {
        var c = NewCatalogWithGroup();
        var v = GroupVolume(c);
        var (leaf, _) = SeedLeaf("leaf-a", "r1", 100, writeArchive: false);   // no archive file

        var r = GroupVolumePlanningService.Plan(c, _dir, _dir, v, new[] { leaf });

        var it = r.Leaves.Single().Items.Single();
        Assert.Equal("skip", it.Decision.Decision);
        Assert.Equal("archive incomplete", it.Decision.Reason);
    }

    [Fact] // a candidate already assigned to any volume (across all volumes) is skipped exactly as Single
    public void Plan_AlreadyAssigned_Skipped()
    {
        var c = NewCatalogWithGroup();
        var v = GroupVolume(c);
        var (leaf, daId) = SeedLeaf("leaf-a", "r1", 100, writeArchive: true);

        // Assign the DA to some volume (per-artifact dat_line_id is authoritative for the exclusion set).
        c.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = "other-vol", DatLineId = "leaf-a",
                    DerivedArtifactId = daId, ContentIdentityKey = "ck-leaf-a-r1",
                    Status = "present_in_final", AddedAtUtc = DateTime.UtcNow },
        });

        var r = GroupVolumePlanningService.Plan(c, _dir, _dir, v, new[] { leaf });

        var it = r.Leaves.Single().Items.Single();
        Assert.Equal("skip", it.Decision.Decision);
        Assert.Equal("already assigned", it.Decision.Reason);
    }
}

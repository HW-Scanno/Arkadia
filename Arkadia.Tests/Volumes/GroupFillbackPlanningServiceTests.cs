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
/// Group Fillback (V-G7): same-GroupId Group → Group, one globally-ordered plan across leaves reusing the exact
/// Single planner core + executor. Proves same-collection gating, global ReleaseName/FileName ordering, one global
/// target budget, exact leaf dat_line_id preservation (never GroupId), nested source→target paths, same-filename
/// across leaves, target parent creation, same-disk move semantics, partial fillback, and source-empty behavior.
/// Real temp catalog + leaf DBs + physical roots; no UI. Both volume roots live under one temp base ⇒ same-disk.
/// </summary>
public sealed class GroupFillbackPlanningServiceTests : IDisposable
{
    private const string Group  = "c64-tosec";
    private const string Group2 = "c64-other";
    private readonly string _appRoot;
    private readonly string _dataDir;

    public GroupFillbackPlanningServiceTests()
    {
        _appRoot = Path.Combine(Path.GetTempPath(), "ArkGVFill_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_appRoot, "data");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_appRoot, true); } catch { }
    }

    private static string Sha1Hex(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();
    private string VolRoot(string label) => Path.Combine(_appRoot, "volumes", label);

    private CatalogService NewCatalog()
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "c64", Name = "C64", Manufacturer = "Commodore", HardwareTypeId = "console" } });
        foreach (var (g, disp) in new[] { (Group, "Commodore 64 TOSEC"), (Group2, "Commodore 64 Other") })
            c.CreateDatGroup(DatGroupId.TryCreateNew(g, out var id, out _, out _) ? id : throw new InvalidOperationException(),
                disp, "c64", "tosec");
        return c;
    }

    private VolumeRecord GroupVolume(CatalogService c, string id, string label, string groupId, long planned, long actual = 0)
    {
        var v = new VolumeRecord
        {
            Id = id, Label = label, PlatformId = "c64", DatLineId = null, GroupId = groupId,
            Status = "present", Health = "ok", PlannedSizeBytes = planned, ActualSizeBytes = actual,
            CreatedAt = DateTime.UtcNow,
        };
        c.SaveVolume(v);
        Directory.CreateDirectory(VolRoot(label));
        return v;
    }

    // Seeds a leaf DB (release present + CI + link + derived artifact) + a source-volume assignment, and writes the
    // physical file at the source volume's nested leaf path. Returns (leaf record, daId, sha1).
    private (GroupLeafRecord Leaf, string DaId, string Sha1) SeedAssigned(
        CatalogService c, VolumeRecord sourceVol, string leafId, string relId, string releaseName,
        string fileName, byte[] content)
    {
        var dsp = $"systems/c64/{leafId}.db";
        var abs = Path.Combine(_dataDir, dsp);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var sha1 = Sha1Hex(content);
        var cik  = "sha1:" + sha1 + ":" + leafId + ":" + relId;

        var store = new DatLineStore(abs);
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = leafId, Name = releaseName, Status = "present", ReleaseContentKey = cik });
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });

        var daId = store.IngestDerivedArtifact(cik, "", "no_compression", fileName,
            $"archive/c64/{leafId}/{fileName}", content.Length, sha1);

        // Atomic add + increment so the source actual_size accumulates across seeds (realistic budgets/decrements).
        c.AddVolumeArtifactAndIncrementSize(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = sourceVol.Id, DatLineId = leafId,
            DerivedArtifactId = daId, ContentIdentityKey = cik, Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        }, content.Length);

        var segment  = GroupRelativeLeafPath.Segment(sourceVol.GroupId!, leafId);
        var srcDir   = Path.Combine(VolRoot(sourceVol.Label), segment);
        Directory.CreateDirectory(srcDir);
        File.WriteAllBytes(Path.Combine(srcDir, fileName), content);

        var leaf = new GroupLeafRecord(
            new DatLineRecord { Id = leafId, HardwareFamilyId = "c64", Name = leafId + " (display)", DataStorePath = dsp },
            new DatLineGroupMetadataRecord { DatLineId = leafId, GroupId = sourceVol.GroupId! });
        return (leaf, daId, sha1);
    }

    private VolumeFillbackPlan Plan(CatalogService c, VolumeRecord src, VolumeRecord tgt, params GroupLeafRecord[] leaves)
        => GroupFillbackPlanningService.Plan(
            c, _dataDir, src, tgt, VolRoot(src.Label), VolRoot(tgt.Label), "Local Archive", "Local Archive", leaves);

    // ── Same-collection gating ──────────────────────────────────────────────

    [Fact]
    public void Plan_DifferentGroup_Blocked()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-other-001", Group2, 1_000_000);   // different collection
        var (a, _, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt", "r1", "Alpha", "a.rom", new byte[]{1,2,3});

        var plan = Plan(c, src, tgt, a);

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Issues, i => i.Contains("same collection"));
    }

    // ── Global ordering across leaves (ReleaseName then FileName, not DatLineId) ──

    [Fact]
    public void Plan_GlobalOrdering_AcrossLeaves_ByReleaseName()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-tosec-002", Group, 1_000_000);
        // "Alpha" lives in games-action-d64 (later DatLineId), "Beta" in firmware-crt (earlier DatLineId).
        var (a, _, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt",     "r1", "Beta",  "b.rom", new byte[]{1});
        var (b, _, _) = SeedAssigned(c, src, "c64-tosec-games-action-d64", "r2", "Alpha", "a.rom", new byte[]{2,2});

        var plan = Plan(c, src, tgt, a, b);

        Assert.Equal(2, plan.PlannedCount);
        // Global order is by ReleaseName → "Alpha" first, even though its leaf sorts later than firmware-crt.
        Assert.Equal("Alpha", plan.Entries[0].ReleaseName);
        Assert.Equal("Beta",  plan.Entries[1].ReleaseName);
    }

    // ── One global target budget across leaves ──

    [Fact]
    public void Plan_OneGlobalBudget_AcrossLeaves()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-tosec-002", Group, planned: 6);   // fits only one 6-byte artifact
        var (a, _, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt",     "r1", "Alpha", "a.rom", new byte[6]);
        var (b, _, _) = SeedAssigned(c, src, "c64-tosec-games-action-d64", "r2", "Beta",  "b.rom", new byte[6]);

        var plan = Plan(c, src, tgt, a, b);

        Assert.Equal(1, plan.PlannedCount);   // ONE global budget, not per-leaf
        Assert.Equal(1, plan.SkipReasonCounts.GetValueOrDefault(VolumeFillbackPlanner.SkipReason.TooLargeForRemainingTargetSpace));
    }

    // ── Execution: same-disk move, exact leaf owner, nested paths, dual size accounting ──

    [Fact]
    public void Execute_SameDisk_MovesNested_PreservesLeafOwner_AdjustsSizes()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-tosec-002", Group, 1_000_000);
        var ca = new byte[]{10,20,30}; var cb = new byte[]{40,50,60,70};
        var (a, daA, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt",     "r1", "Alpha", "game.rom", ca);   // same filename
        var (b, daB, _) = SeedAssigned(c, src, "c64-tosec-games-action-d64", "r2", "Beta",  "game.rom", cb);   // in two leaves
        long srcBefore = c.GetVolumeById(src.Id)!.ActualSizeBytes;

        var plan = Plan(c, src, tgt, a, b);
        Assert.True(plan.CanExecute);
        Assert.Equal(FillbackOperationMode.MoveSameDisk, plan.OperationMode);

        var res = new VolumeFillbackService(c).Execute(plan);
        Assert.Equal(0, res.ErrorCount);
        Assert.Equal(2, res.MovedCount);

        // Files moved to nested TARGET paths; source nested paths gone (same filename in two leaves stays distinct).
        Assert.True(File.Exists(Path.Combine(VolRoot("c64-tosec-002"), "firmware-crt", "game.rom")));
        Assert.True(File.Exists(Path.Combine(VolRoot("c64-tosec-002"), "games-action-d64", "game.rom")));
        Assert.False(File.Exists(Path.Combine(VolRoot("c64-tosec-001"), "firmware-crt", "game.rom")));
        Assert.False(File.Exists(Path.Combine(VolRoot("c64-tosec-001"), "games-action-d64", "game.rom")));

        // VA rows now on target, with the EXACT leaf owner preserved (never the GroupId).
        var tgtVAs = c.GetVolumeArtifacts(tgt.Id);
        Assert.Equal(2, tgtVAs.Count);
        Assert.Contains(tgtVAs, x => x.DatLineId == "c64-tosec-firmware-crt"     && x.DerivedArtifactId == daA);
        Assert.Contains(tgtVAs, x => x.DatLineId == "c64-tosec-games-action-d64" && x.DerivedArtifactId == daB);
        Assert.DoesNotContain(tgtVAs, x => x.DatLineId == Group);
        Assert.Empty(c.GetVolumeArtifacts(src.Id));

        // Dual size accounting.
        Assert.Equal(srcBefore - ca.Length - cb.Length, c.GetVolumeById(src.Id)!.ActualSizeBytes);
        Assert.Equal(ca.Length + cb.Length,              c.GetVolumeById(tgt.Id)!.ActualSizeBytes);
    }

    // ── Target parent creation: the target leaf folder does not exist before execution ──

    [Fact]
    public void Execute_CreatesTargetLeafFolder_WhenAbsent()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-tosec-002", Group, 1_000_000);
        var (a, _, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt", "r1", "Alpha", "a.rom", new byte[]{1,2,3});
        Assert.False(Directory.Exists(Path.Combine(VolRoot("c64-tosec-002"), "firmware-crt")));

        var plan = Plan(c, src, tgt, a);
        var res  = new VolumeFillbackService(c).Execute(plan);

        Assert.Equal(0, res.ErrorCount);
        Assert.True(File.Exists(Path.Combine(VolRoot("c64-tosec-002"), "firmware-crt", "a.rom")));
    }

    // ── Partial fillback: only what fits moves; rest stays on source; no health/status change ──

    [Fact]
    public void Execute_Partial_LeavesRemainderOnSource_NoStatusChange()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-tosec-002", Group, planned: 3);   // fits only the 3-byte one
        var (a, _, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt",     "r1", "Alpha", "a.rom", new byte[]{1,2,3});
        var (b, _, _) = SeedAssigned(c, src, "c64-tosec-games-action-d64", "r2", "Beta",  "b.rom", new byte[]{4,5,6,7});

        var plan = Plan(c, src, tgt, a, b);
        var res  = new VolumeFillbackService(c).Execute(plan);

        Assert.Equal(0, res.ErrorCount);
        Assert.Equal(1, res.MovedCount);
        Assert.False(res.SourceEmpty);
        Assert.Single(c.GetVolumeArtifacts(src.Id));                    // the too-large one remains
        Assert.Single(c.GetVolumeArtifacts(tgt.Id));
        Assert.Equal("present", c.GetVolumeById(src.Id)!.Status);       // no status change
        Assert.Equal("present", c.GetVolumeById(tgt.Id)!.Status);
    }

    // ── Source-empty: all fit → source drained but remains present ──

    [Fact]
    public void Execute_AllFit_SourceEmptyButPresent()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-tosec-002", Group, 1_000_000);
        var (a, _, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt", "r1", "Alpha", "a.rom", new byte[]{1,2,3});

        var plan = Plan(c, src, tgt, a);
        var res  = new VolumeFillbackService(c).Execute(plan);

        Assert.True(res.SourceEmpty);
        Assert.Empty(c.GetVolumeArtifacts(src.Id));
        Assert.Equal(0, c.GetVolumeById(src.Id)!.ActualSizeBytes);
        Assert.Equal("present", c.GetVolumeById(src.Id)!.Status);        // NOT auto-retired
        Assert.Null(c.GetCurrentLocation(src.Id));                       // location unchanged (none set here)
    }

    // ── Cross-disk nested path via the shared executor (mode forced through a CopyVerifyDelete plan) ──

    [Fact]
    public void Execute_CrossDiskNestedPaths_CopyVerifyDelete()
    {
        var c   = NewCatalog();
        var src = GroupVolume(c, "s", "c64-tosec-001", Group, 1_000_000);
        var tgt = GroupVolume(c, "t", "c64-tosec-002", Group, 1_000_000);
        var (a, _, _) = SeedAssigned(c, src, "c64-tosec-firmware-crt", "r1", "Alpha", "a.rom", new byte[]{9,8,7,6});

        var basePlan = Plan(c, src, tgt, a);
        // Re-tag the (same-disk) plan as cross-disk to exercise ExecuteCopyVerifyDelete with nested paths.
        var crossEntries = basePlan.Entries
            .Select(e => e with { Action = FillbackEntryAction.CopyVerifyDelete }).ToList();
        var crossPlan = ClonePlanWith(basePlan, crossEntries, FillbackOperationMode.CopyVerifyDeleteCrossDisk);

        var res = new VolumeFillbackService(c).Execute(crossPlan);

        Assert.Equal(0, res.ErrorCount);
        Assert.Equal(1, res.CopiedCount);
        Assert.True(File.Exists(Path.Combine(VolRoot("c64-tosec-002"), "firmware-crt", "a.rom")));
        Assert.False(File.Exists(Path.Combine(VolRoot("c64-tosec-001"), "firmware-crt", "a.rom")));
    }

    private static VolumeFillbackPlan ClonePlanWith(
        VolumeFillbackPlan p, List<FillbackEntry> entries, FillbackOperationMode mode) => new()
    {
        SourceVolumeId = p.SourceVolumeId, SourceVolumeLabel = p.SourceVolumeLabel, SourceDiskLabel = p.SourceDiskLabel,
        SourceRootPath = p.SourceRootPath, TargetVolumeId = p.TargetVolumeId, TargetVolumeLabel = p.TargetVolumeLabel,
        TargetDiskLabel = p.TargetDiskLabel, TargetRootPath = p.TargetRootPath, OperationMode = mode,
        TargetCapacityBytes = p.TargetCapacityBytes, TargetUsedBytes = p.TargetUsedBytes, TargetFreeBytes = p.TargetFreeBytes,
        PlannedBytes = p.PlannedBytes, PlannedCount = p.PlannedCount, SkippedCount = p.SkippedCount,
        RemainingTargetFreeBytes = p.RemainingTargetFreeBytes, SourceBytesBefore = p.SourceBytesBefore,
        SourceBytesAfter = p.SourceBytesAfter, TargetBytesAfter = p.TargetBytesAfter, Entries = entries,
        Warnings = p.Warnings, Issues = p.Issues, SkipReasonCounts = p.SkipReasonCounts, CanExecute = p.CanExecute,
    };
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Activities;
using Arkadia.Data;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Activities;

/// <summary>
/// GLOBAL-ACTIVITY-INTEGRATION-1 — physical-volume services (Append, Group Append, Fillback, Reabsorb): the right
/// kind while they work, released on every outcome, and the SAME operational result with and without the monitor.
/// </summary>
public sealed class VolumeServiceActivityTests : IDisposable
{
    private readonly List<string> _roots = new();

    public void Dispose()
    {
        foreach (var r in _roots) { try { Directory.Delete(r, true); } catch { } }
    }

    private string NewRoot()
    {
        var p = Path.Combine(Path.GetTempPath(), "ArkActSvc_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(p);
        _roots.Add(p);
        return p;
    }

    // ── Append ───────────────────────────────────────────────────────────────────

    private sealed record AppendEnv(string Root, CatalogService Catalog, AppendVolumePlan Plan, string VolRoot, string DaId, VolumeRecord Vol);

    private AppendEnv ProvisionAppend(string datLineId, string fileName, byte[] content)
    {
        var root = NewRoot();
        var catalog = new CatalogService(Path.Combine(root, "catalog.db"));
        var store   = new DatLineStore(Path.Combine(root, "dat.db"));
        var sha1  = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var cik   = "sha1:" + sha1;
        var relId = Guid.NewGuid().ToString("N");

        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = datLineId, Name = "R", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });
        var daId = store.IngestDerivedArtifact(cik, "", "chd", fileName, $"archive/snes/{datLineId}/{fileName}", content.Length, sha1);

        var vol = new VolumeRecord
        {
            Id = Guid.NewGuid().ToString("N"), Label = "vol-" + datLineId, PlatformId = "snes", DatLineId = datLineId,
            Status = "present", PlannedSizeBytes = 10_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow, Health = "ok",
        };
        catalog.SaveVolume(vol);
        var archiveDir = Path.Combine(root, "archive", "snes", datLineId);
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, fileName), content);

        var volRoot = Path.Combine(root, "volumes", vol.Label);
        Directory.CreateDirectory(volRoot);
        var plan = new AppendVolumePlanner(catalog).Plan(vol, volRoot, root, store);
        Assert.True(plan.CanExecute);
        return new AppendEnv(root, catalog, plan, volRoot, daId, vol);
    }

    [Fact]
    public void Append_IsObservedAsAppending_ReleasedAfter_AndResultMatchesTheUnobservedRun()
    {
        var content = new byte[] { 1, 2, 3, 4, 5, 6 };
        var a = ProvisionAppend("dl1", "Game.chd", content);
        var b = ProvisionAppend("dl1", "Game.chd", content);
        var rec = new ActivityRecorder();

        ActivityKind? during = null;
        var observed   = new AppendVolumeService(a.Catalog, rec.Monitor).Execute(a.Plan, new SyncProgress<AppendVolumeProgress>(_ => during ??= rec.Now));
        var unobserved = new AppendVolumeService(b.Catalog).Execute(b.Plan);

        Assert.Equal(ActivityKind.Appending, during);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Appending, null }, rec.Seen);
        Assert.Equal(unobserved.CopiedCount, observed.CopiedCount);
        Assert.Equal(unobserved.BytesCopied, observed.BytesCopied);
        Assert.Equal(unobserved.ErrorCount, observed.ErrorCount);
        Assert.Equal(unobserved.SourcesDeletedCount, observed.SourcesDeletedCount);
        Assert.Equal(unobserved.LogLines.Select(l => l.Split("  ")[0]), observed.LogLines.Select(l => l.Split("  ")[0]));
        Assert.Equal(File.ReadAllBytes(Path.Combine(b.VolRoot, "Game.chd")), File.ReadAllBytes(Path.Combine(a.VolRoot, "Game.chd")));
        Assert.True(a.Catalog.VolumeArtifactExists(a.Vol.Id, a.DaId));
    }

    [Fact]
    public void Append_ReleasesTheScope_WhenTheWorkThrows()
    {
        var a = ProvisionAppend("dl1", "Game.chd", new byte[] { 9, 9, 9 });
        var rec = new ActivityRecorder();
        Assert.Throws<InvalidOperationException>(() =>
            new AppendVolumeService(a.Catalog, rec.Monitor).Execute(a.Plan, new SyncProgress<AppendVolumeProgress>(_ => throw new InvalidOperationException("boom"))));
        Assert.True(rec.Idle);
    }

    [Fact]
    public void GroupAppend_IsOneContinuousAppendingPeriod_NoReadyBetweenLeaves()
    {
        var l1 = ProvisionAppend("dl1", "A.chd", new byte[] { 1, 1, 1 });
        var l2 = ProvisionAppend("dl2", "B.chd", new byte[] { 2, 2, 2 });
        var group = new GroupAppendVolumePlan
        {
            VolumeId = "v", GroupId = "g", VolumeLabel = "grp", CollectionDisplay = "Group",
            LeafPlans = new[] { l1.Plan, l2.Plan }, Aggregate = l1.Plan,
        };
        var rec = new ActivityRecorder();

        var result = new AppendVolumeService(l1.Catalog, rec.Monitor).ExecuteGroup(group, new SyncProgress<AppendVolumeProgress>(_ => { }));

        Assert.Equal(2, result.CopiedCount);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Appending, null }, rec.Seen);   // ONE period across both leaves
        Assert.True(File.Exists(Path.Combine(l1.VolRoot, "A.chd")));
        Assert.True(File.Exists(Path.Combine(l2.VolRoot, "B.chd")));
    }

    [Fact]
    public void GroupAppend_ReleasesTheScope_WhenALaterLeafThrows()
    {
        var l1 = ProvisionAppend("dl1", "A.chd", new byte[] { 1, 1, 1 });
        var l2 = ProvisionAppend("dl2", "B.chd", new byte[] { 2, 2, 2 });
        var group = new GroupAppendVolumePlan
        {
            VolumeId = "v", GroupId = "g", VolumeLabel = "grp", CollectionDisplay = "Group",
            LeafPlans = new[] { l1.Plan, l2.Plan }, Aggregate = l1.Plan,
        };
        var rec = new ActivityRecorder();
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => new AppendVolumeService(l1.Catalog, rec.Monitor)
            .ExecuteGroup(group, new SyncProgress<AppendVolumeProgress>(_ => { if (++calls > 1) throw new InvalidOperationException("leaf 2"); })));
        Assert.True(rec.Idle);
    }

    // ── Fillback and Reabsorb ────────────────────────────────────────────────────

    [Fact]
    public void Fillback_IsObservedAsAppending_AndReleased()
    {
        var root = NewRoot();
        var catalog = new CatalogService(Path.Combine(root, "catalog.db"));
        var src = new VolumeRecord { Id = "s", Label = "src", PlatformId = "snes", DatLineId = "dl1", Status = "present", Health = "ok", PlannedSizeBytes = 1000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow };
        var dst = new VolumeRecord { Id = "d", Label = "dst", PlatformId = "snes", DatLineId = "dl1", Status = "present", Health = "ok", PlannedSizeBytes = 1000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow };
        catalog.SaveVolume(src); catalog.SaveVolume(dst);
        var plan = new VolumeFillbackPlan
        {
            SourceVolumeId = "s", SourceVolumeLabel = "src", SourceDiskLabel = "", SourceRootPath = Path.Combine(root, "src"),
            TargetVolumeId = "d", TargetVolumeLabel = "dst", TargetDiskLabel = "", TargetRootPath = Path.Combine(root, "dst"),
            OperationMode = FillbackOperationMode.MoveSameDisk, TargetCapacityBytes = 0, TargetUsedBytes = 0, TargetFreeBytes = 0,
            PlannedBytes = 0, PlannedCount = 0, SkippedCount = 0, RemainingTargetFreeBytes = 0,
            SourceBytesBefore = 0, SourceBytesAfter = 0, TargetBytesAfter = 0,
            CanExecute = true, Entries = Array.Empty<FillbackEntry>(), Warnings = Array.Empty<string>(), Issues = Array.Empty<string>(),
            SkipReasonCounts = new Dictionary<string, int>(),
        };
        Directory.CreateDirectory(plan.SourceRootPath);
        Directory.CreateDirectory(plan.TargetRootPath);
        var rec = new ActivityRecorder();

        var observed   = new VolumeFillbackService(catalog, rec.Monitor).Execute(plan);
        var unobserved = new VolumeFillbackService(catalog).Execute(plan);

        Assert.Equal(new ActivityKind?[] { ActivityKind.Appending, null }, rec.Seen);
        Assert.Equal(unobserved.MovedCount, observed.MovedCount);
        Assert.Equal(unobserved.ErrorCount, observed.ErrorCount);
        Assert.Equal(unobserved.SourceEmpty, observed.SourceEmpty);
    }

    [Fact]
    public void Reabsorb_IsObservedAsReabsorbing_AndReleased_AlsoOnAnUnknownVolume()
    {
        var root = NewRoot();
        var catalog = new CatalogService(Path.Combine(root, "catalog.db"));
        var rec = new ActivityRecorder();
        var svc = new VolumeReabsorbService(catalog, rec.Monitor);
        try { svc.Execute("missing-volume", Path.Combine(root, "v"), Array.Empty<ReabsorbCandidate>()); }
        catch (Exception) { /* an unknown volume may be rejected; the scope must be released either way */ }

        Assert.Equal(ActivityKind.Reabsorbing, rec.Seen.First());
        Assert.True(rec.Idle);
        Assert.Null(rec.Seen.Last());
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}

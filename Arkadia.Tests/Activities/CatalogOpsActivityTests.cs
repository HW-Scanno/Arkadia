using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Activities;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.LocalArchive;
using Arkadia.Purge;
using Xunit;

namespace Arkadia.Tests.Activities;

/// <summary>
/// GLOBAL-ACTIVITY-INTEGRATION-1 — Verify/Repair Archive, Purge Release, Library bulk operations, AMP registry and the
/// monitor's generic <c>Run&lt;T&gt;</c>: right kind, released on every outcome, same operational result with and
/// without the monitor, and no extra file I/O beyond the observed operation itself.
/// </summary>
public sealed class CatalogOpsActivityTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ArkActOps_" + Guid.NewGuid().ToString("N")[..10]);

    public CatalogOpsActivityTests() => Directory.CreateDirectory(_tmp);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    // ── Verify Archive / Repair Archive ──────────────────────────────────────────

    private const string Platform = "ps2", DatLine = "ps2-redump";

    private (DatLineStore Store, string AppRoot) ProvisionArchive()
    {
        var appRoot = Path.Combine(_tmp, "approot");
        var store   = new DatLineStore(Path.Combine(_tmp, "dat.db"));
        var content = System.Text.Encoding.UTF8.GetBytes("good-content");
        var sha1    = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var cik     = "sha1:" + sha1;
        var relId   = Guid.NewGuid().ToString("N");
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = DatLine, Name = "Good", Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });
        store.IngestDerivedArtifact(cik, "", "chd", "Good.chd", $"archive/{Platform}/{DatLine}/Good.chd", content.Length, sha1);

        var dir = Path.Combine(appRoot, "archive", Platform, DatLine);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Good.chd"), content);
        File.WriteAllBytes(Path.Combine(dir, "Stray.bin"), new byte[] { 7, 7, 7 });     // unknown file → repairable
        return (store, appRoot);
    }

    [Fact]
    public void VerifyArchive_IsObservedAsVerifying_ReleasedAfter_ResultIdenticalToTheUnobservedScan()
    {
        var (store, root) = ProvisionArchive();
        var rec = new ActivityRecorder();

        var observed   = new LocalArchiveVerifyService(root, rec.Monitor).Verify(Platform, DatLine, store);
        var unobserved = new LocalArchiveVerifyService(root).Verify(Platform, DatLine, store);

        Assert.Equal(new ActivityKind?[] { ActivityKind.Verifying, null }, rec.Seen);
        Assert.Equal(unobserved.Entries.Select(e => (e.FileName, e.Classification)).OrderBy(x => x.FileName),
                     observed.Entries.Select(e => (e.FileName, e.Classification)).OrderBy(x => x.FileName));
        Assert.Equal(unobserved.IsClean, observed.IsClean);
    }

    [Fact]
    public void RepairArchive_IsObservedAsRepairing_MovesNotDeletes_AndReleases()
    {
        var (store, root) = ProvisionArchive();
        var rec = new ActivityRecorder();
        var svc = new LocalArchiveVerifyService(root, rec.Monitor);
        var plan = svc.Verify(Platform, DatLine, store);
        rec.Seen.Clear();

        ActivityKind? during = null;
        var result = svc.Repair(plan, store, new SyncProgress<LocalArchiveVerifyProgress>(_ => during ??= rec.Now));

        Assert.Equal(ActivityKind.Repairing, during);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Repairing, null }, rec.Seen);
        Assert.True(File.Exists(Path.Combine(root, "incoming-skip", Platform, "Stray.bin")));    // moved, never deleted
        Assert.False(File.Exists(Path.Combine(root, "archive", Platform, DatLine, "Stray.bin")));
        Assert.True(File.Exists(Path.Combine(root, "archive", Platform, DatLine, "Good.chd")));
        Assert.True(rec.Idle);
        Assert.NotNull(result);
    }

    [Fact]
    public void VerifyAndRepairArchive_ReleaseTheScope_WhenTheWorkThrows()
    {
        var (store, root) = ProvisionArchive();
        var rec = new ActivityRecorder();
        var svc = new LocalArchiveVerifyService(root, rec.Monitor);
        var plan = svc.Verify(Platform, DatLine, store);

        Assert.ThrowsAny<Exception>(() => svc.Repair(plan, store, new SyncProgress<LocalArchiveVerifyProgress>(_ => throw new InvalidOperationException("boom"))));
        Assert.True(rec.Idle);
        Assert.ThrowsAny<Exception>(() => svc.Verify(Platform, DatLine, store, new SyncProgress<LocalArchiveVerifyProgress>(_ => throw new InvalidOperationException("boom"))));
        Assert.True(rec.Idle);
    }

    // ── Purge Release ────────────────────────────────────────────────────────────

    private PurgeReleasePlan PurgePlan(string dbPath, string absFile, bool canExecute = true) => new()
    {
        ReleaseId = "r1", ReleaseName = "Game", CurrentStatus = "present", DatLineId = "dl1", DbPath = dbPath,
        LocalArtifacts = [new PurgeLocalArtifact("da1", "Game.chd", absFile, 3, true)],
        VolumeArtifacts = [], TotalLocalBytes = 3, TotalVolumeBytes = 0,
        RequiredDiskLabels = [], OfflineDiskLabels = [], Warnings = [], Issues = [], CanExecute = canExecute,
    };

    [Fact]
    public void PurgeRelease_IsObservedAsPurging_OnlyWhenItActuallyExecutes_AndReleases()
    {
        var dbPath = Path.Combine(_tmp, "dat", "p.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var store = new DatLineStore(dbPath);
        store.SaveReleases(new List<ReleaseRecord> { new() { Id = "r1", DatLineId = "dl1", Name = "Game", Status = "present" } });
        var file = Path.Combine(_tmp, "Game.chd");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        var catalog = new CatalogService(Path.Combine(_tmp, "catalog.db"));
        var rec = new ActivityRecorder();

        // a plan that cannot execute is refused WITHOUT a PURGING state
        var refused = new PurgeReleaseService(_tmp, catalog, rec.Monitor).Execute(PurgePlan(dbPath, file, canExecute: false));
        Assert.False(refused.Success);
        Assert.Empty(rec.Seen);
        Assert.True(File.Exists(file));

        var result = new PurgeReleaseService(_tmp, catalog, rec.Monitor).Execute(PurgePlan(dbPath, file));
        Assert.True(result.Success);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Purging, null }, rec.Seen);
        Assert.False(File.Exists(file));
        Assert.Equal("unwanted", store.LoadReleases().Single(r => r.Id == "r1").Status);
    }

    [Fact]
    public void PurgeRelease_FailureStillReleasesTheScope_AndKeepsTheReleaseStatus()
    {
        var dbPath = Path.Combine(_tmp, "dat", "p2.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var store = new DatLineStore(dbPath);
        store.SaveReleases(new List<ReleaseRecord> { new() { Id = "r1", DatLineId = "dl1", Name = "Game", Status = "present" } });
        var catalog = new CatalogService(Path.Combine(_tmp, "catalog2.db"));
        var rec = new ActivityRecorder();

        var result = new PurgeReleaseService(_tmp, catalog, rec.Monitor).Execute(PurgePlan(dbPath, Path.Combine(_tmp, "no-such-dir", "X.chd")));
        Assert.True(rec.Idle);
        Assert.Equal(ActivityKind.Purging, rec.Seen.First());
        Assert.Equal("present", store.LoadReleases().Single(r => r.Id == "r1").Status);
        Assert.NotNull(result);
    }

    // ── Library bulk operations ──────────────────────────────────────────────────

    private (LibraryBulkOperationPlan Plan, string DbPath, DatLineStore Store) BulkPlan(LibraryBulkOperationType op, string tag)
    {
        var dbPath = Path.Combine(_tmp, "bulk-" + tag + ".db");
        var store  = new DatLineStore(dbPath);
        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = "r1", DatLineId = "dl1", Name = "Alpha", Status = "present" },
            new() { Id = "r2", DatLineId = "dl1", Name = "Alpha 2", Status = "present" },
        });
        var rows = new[] { "r1", "r2" }.Select(id => new BulkOperationReleaseRow
        {
            ReleaseId = id, ReleaseName = id, Status = "present", ShowInCatalog = true, IsNoOp = false, Note = "",
        }).ToList();
        return (new LibraryBulkOperationPlan { DatLineId = "dl1", DatLineLabel = "dl1", MatchText = "Alpha", Operation = op, Rows = rows }, dbPath, store);
    }

    [Theory]
    [InlineData(LibraryBulkOperationType.HideFromCatalog, ActivityKind.Processing)]
    [InlineData(LibraryBulkOperationType.ShowInCatalog, ActivityKind.Processing)]
    [InlineData(LibraryBulkOperationType.RestoreWanted, ActivityKind.Processing)]
    [InlineData(LibraryBulkOperationType.PurgeAndMarkUnwanted, ActivityKind.Purging)]
    public void BulkOperation_UsesProcessingForNonDestructive_PurgingOnlyForPurge_OneScopePerBatch(LibraryBulkOperationType op, ActivityKind expected)
    {
        var (plan, dbPath, _) = BulkPlan(op, op.ToString());
        var catalog = new CatalogService(Path.Combine(_tmp, "bulk-catalog-" + op + ".db"));
        var rec = new ActivityRecorder();
        var progressKinds = new List<ActivityKind?>();

        var result = new LibraryBulkOperationService(_tmp, catalog, rec.Monitor)
            .Execute(plan, dbPath, new SyncProgress<LibraryBulkOperationProgress>(_ => progressKinds.Add(rec.Now)));

        Assert.Equal(new ActivityKind?[] { expected, null }, rec.Seen);          // ONE period for the whole batch
        Assert.All(progressKinds, k => Assert.Equal(expected, k));
        Assert.Equal(2, progressKinds.Count);
        Assert.Equal(2, result.Succeeded + result.Failed);
    }

    [Fact]
    public void BulkHide_ChangesTheSameCatalogFlags_WithAndWithoutTheMonitor()
    {
        var (planA, dbA, storeA) = BulkPlan(LibraryBulkOperationType.HideFromCatalog, "a");
        var (planB, dbB, storeB) = BulkPlan(LibraryBulkOperationType.HideFromCatalog, "b");
        var catalog = new CatalogService(Path.Combine(_tmp, "bulk-catalog-eq.db"));

        var observed   = new LibraryBulkOperationService(_tmp, catalog, new ActivityRecorder().Monitor).Execute(planA, dbA);
        var unobserved = new LibraryBulkOperationService(_tmp, catalog).Execute(planB, dbB);

        Assert.Equal(unobserved.Succeeded, observed.Succeeded);
        Assert.Equal(unobserved.Failed, observed.Failed);
        Assert.Equal(storeB.LoadReleases().Select(r => (r.Id, r.ShowInCatalog)), storeA.LoadReleases().Select(r => (r.Id, r.ShowInCatalog)));
    }

    // ── AMP registry ─────────────────────────────────────────────────────────────

    [Fact]
    public void AmpRegistry_VerifyIsVerifying_RegisterIsImporting_AndBothReleaseAfterAProblem()
    {
        var rec = new ActivityRecorder();
        var reg = new AmpLocalRegistryService(Path.Combine(_tmp, "data"), rec.Monitor);

        var info = reg.VerifyPackage(Path.Combine(_tmp, "missing.amp"));          // reports the problem, does not throw
        Assert.True(info.HasErrors);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Verifying, null }, rec.Seen);

        rec.Seen.Clear();
        Assert.Throws<FileNotFoundException>(() => reg.RegisterLocalPackage(Path.Combine(_tmp, "missing.amp")));
        Assert.True(rec.Idle);
        Assert.Equal(ActivityKind.Importing, rec.Seen.First());
        Assert.DoesNotContain(ActivityKind.Verifying, rec.Seen.Where(k => k is not null).Select(k => k!.Value));   // no nested kind
    }

    [Fact]
    public void AmpRegistry_ListingStaysUnobserved()
    {
        var rec = new ActivityRecorder();
        new AmpLocalRegistryService(Path.Combine(_tmp, "data2"), rec.Monitor).ListPackages();
        Assert.Empty(rec.Seen);
    }

    // ── Monitor: generic Run<T> ──────────────────────────────────────────────────

    [Fact]
    public void Run_OfT_ReturnsTheValue_AndAlwaysEndsTheScope()
    {
        var rec = new ActivityRecorder();
        Assert.Equal(42, rec.Monitor.Run(ActivityKind.Verifying, () => { Assert.Equal(ActivityKind.Verifying, rec.Now); return 42; }));
        Assert.True(rec.Idle);
        Assert.Throws<InvalidOperationException>(() => rec.Monitor.Run<int>(ActivityKind.Verifying, () => throw new InvalidOperationException("x")));
        Assert.True(rec.Idle);
        Assert.Equal(new ActivityKind?[] { ActivityKind.Verifying, null, ActivityKind.Verifying, null }, rec.Seen);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}

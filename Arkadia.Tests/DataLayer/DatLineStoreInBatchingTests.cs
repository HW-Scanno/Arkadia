using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Regression for the "too many SQL variables" scalability bug on the Group volume Apply/Build path: the
/// <c>WHERE … IN (…)</c> id lists in <see cref="DatLineStore.GetDerivedArtifactIdsForReleases"/> and
/// <see cref="DatLineStore.GetArtifactBuildInfos"/> are now chunked. Tests push the id count PAST SQLite's
/// variable limit (a single c64-tosec "Games" leaf really can have tens of thousands of releases) and assert:
/// no exception, complete/identical logical results, cross-batch dedup, correct per-release attribution, and
/// no mutation. Real leaf DB over a temp dir.
/// </summary>
public sealed class DatLineStoreInBatchingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public DatLineStoreInBatchingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkInBatch_" + Guid.NewGuid().ToString("N")[..8]);
        _db  = Path.Combine(_dir, "leaf.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // Seeds releases r0..r{count-1}, each with a content identity, a derived artifact, and their link.
    // Returns the (releaseId, daId, ck) triples in id order. NOTE: SaveReleases does DELETE-then-insert, so ALL
    // releases must go in a single call — seeding them one at a time would wipe every prior release.
    private static List<(string ReleaseId, string DaId, string Ck)> Seed(DatLineStore store, int count)
    {
        var releases = Enumerable.Range(0, count)
            .Select(i => new ReleaseRecord { Id = $"r{i:D6}", DatLineId = "leaf", Name = $"Game {i:D6}", Status = "present" })
            .ToList();
        store.SaveReleases(releases);   // single wipe-then-insert-all

        var result = new List<(string, string, string)>(count);
        foreach (var i in Enumerable.Range(0, count))
        {
            var rid = $"r{i:D6}";
            var ck  = $"sha1:{i:D40}";
            store.EnsureContentIdentity(new ContentIdentityRecord
                { ContentIdentityKey = ck, DatSha1 = null, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
            var daId = store.IngestDerivedArtifact(
                contentIdentityKey: ck, sourceArtifactId: "", storageStrategyId: "zip_compression",
                fileName: $"g{i:D6}.zip", relativePath: $"archive/c64/leaf/g{i:D6}.zip", derivedSizeBytes: 100,
                hashedDerivedSha1: new string('a', 40));
            store.SaveReleaseContentLink(new ReleaseContentLinkRecord
                { Id = Guid.NewGuid().ToString("N"), ReleaseId = rid, ContentIdentityKey = ck, CreatedAtUtc = DateTime.UtcNow });
            result.Add((rid, daId, ck));
        }
        return result;
    }

    private const int OverLimit = 40_000;   // > SQLite's 32766 default → pre-fix single statement would throw

    // ── 1. Over-limit bulk lookup — no "too many SQL variables" ───────────────

    [Fact]
    public void GetDerivedArtifactIdsForReleases_OverLimit_NoException_RealRowsReturned()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 5);

        // Real release ids + tens of thousands of non-existent ids → a single IN(...) would exceed the limit.
        var ids = real.Select(x => x.ReleaseId)
            .Concat(Enumerable.Range(1_000_000, OverLimit).Select(i => $"r{i}"))
            .ToList();

        var map = store.GetDerivedArtifactIdsForReleases(ids);   // must not throw

        foreach (var (rid, daId, ck) in real)
        {
            Assert.True(map.TryGetValue(rid, out var list));
            Assert.Equal(new[] { (daId, ck) }, list);
        }
        Assert.Equal(5, map.Count);   // only the real releases resolve
    }

    [Fact]
    public void GetArtifactBuildInfos_OverLimit_NoException_RealRowsReturned()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 5);

        var ids = real.Select(x => x.DaId)
            .Concat(Enumerable.Range(1_000_000, OverLimit).Select(i => $"da{i}"))
            .ToList();

        var infos = store.GetArtifactBuildInfos(ids);   // must not throw

        Assert.Equal(5, infos.Count);
        foreach (var (_, daId, _) in real)
            Assert.Contains(infos, x => x.DerivedArtifactId == daId);
    }

    // ── 2. Result equivalence: single-batch vs multi-batch ───────────────────

    [Fact]
    public void MultiBatch_EquivalentTo_SingleBatch()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 5);
        var realReleaseIds = real.Select(x => x.ReleaseId).ToList();
        var realDaIds      = real.Select(x => x.DaId).ToList();

        // Single-batch (5 ids) vs forced multi-batch (5 real + 2000 padding spanning several 900-id chunks).
        var padded = realReleaseIds.Concat(Enumerable.Range(2_000_000, 2_000).Select(i => $"r{i}")).ToList();
        var single = store.GetDerivedArtifactIdsForReleases(realReleaseIds);
        var multi  = store.GetDerivedArtifactIdsForReleases(padded);

        Assert.Equal(single.Count, multi.Count);
        Assert.Equal(5, single.Count);   // guards against a seed that silently collapses to one release
        foreach (var rid in realReleaseIds)
            Assert.Equal(single[rid], multi[rid]);

        var paddedDa = realDaIds.Concat(Enumerable.Range(2_000_000, 2_000).Select(i => $"da{i}")).ToList();
        var singleInfos = store.GetArtifactBuildInfos(realDaIds).Select(x => x.DerivedArtifactId).ToList();
        var multiInfos  = store.GetArtifactBuildInfos(paddedDa).Select(x => x.DerivedArtifactId).ToList();
        Assert.Equal(5, singleInfos.Count);      // full set present, not collapsed
        Assert.Equal(singleInfos, multiInfos);   // same set AND same (ReleaseName, FileName) order
    }

    // ── 3. Dedup across batch boundaries ─────────────────────────────────────

    [Fact]
    public void DuplicateIds_AcrossBatches_NoDuplicateInResult()
    {
        var store = new DatLineStore(_db);
        var (rid, daId, ck) = Seed(store, 1)[0];

        // The same id repeated 3000 times crosses several 900-id chunk boundaries.
        var relDup = Enumerable.Repeat(rid, 3_000).ToList();
        var map = store.GetDerivedArtifactIdsForReleases(relDup);
        Assert.Single(map);
        Assert.Equal(new[] { (daId, ck) }, map[rid]);   // exactly one, not 3000

        var daDup = Enumerable.Repeat(daId, 3_000).ToList();
        var infos = store.GetArtifactBuildInfos(daDup);
        Assert.Single(infos);                            // deduped globally across chunks
        Assert.Equal(daId, infos[0].DerivedArtifactId);
    }

    // ── 4. Per-release attribution preserved across chunk boundaries ─────────

    [Fact]
    public void ManyReleases_AcrossChunks_EachMapsToItsOwnArtifact()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 2_500);   // > 900 → multiple chunks

        var map = store.GetDerivedArtifactIdsForReleases(real.Select(x => x.ReleaseId).ToList());

        Assert.Equal(2_500, map.Count);
        foreach (var (rid, daId, ck) in real)
            Assert.Equal(new[] { (daId, ck) }, map[rid]);   // no cross-release contamination at chunk edges
    }

    // ── 5. Read-only: the lookups mutate nothing ─────────────────────────────

    [Fact]
    public void BatchedLookups_AreReadOnly()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 1_500);
        int beforeDa = store.GetDerivedArtifacts().Count;

        _ = store.GetDerivedArtifactIdsForReleases(real.Select(x => x.ReleaseId).ToList());
        _ = store.GetArtifactBuildInfos(real.Select(x => x.DaId).ToList());

        Assert.Equal(beforeDa, store.GetDerivedArtifacts().Count);   // no rows added/removed by the reads
    }

    // ── 6. Empty input short-circuits (unchanged) ────────────────────────────

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var store = new DatLineStore(_db);
        Assert.Empty(store.GetDerivedArtifactIdsForReleases(Array.Empty<string>()));
        Assert.Empty(store.GetArtifactBuildInfos(Array.Empty<string>()));
        Assert.Empty(store.GetArtifactVerifyInfos(Array.Empty<string>()));
        Assert.Equal(0, store.BatchUpdateDerivedArtifactStatus(Array.Empty<string>(), "missing"));
        Assert.Equal(0, store.RecalculateReleaseStatusForArtifacts(Array.Empty<string>()));
    }

    // ── 7. BatchUpdateDerivedArtifactStatus — over-limit UPDATE (Build finalize / Verify) ─

    [Fact]
    public void BatchUpdateDerivedArtifactStatus_OverLimit_NoException_UpdatesRealRows()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 5);

        // 5 real da ids + tens of thousands of non-existent ids → a single UPDATE … IN(...) would exceed the limit.
        var ids = real.Select(x => x.DaId)
            .Concat(Enumerable.Range(1_000_000, OverLimit).Select(i => $"da{i}"))
            .ToList();

        var changed = store.BatchUpdateDerivedArtifactStatus(ids, "missing");   // must not throw

        Assert.Equal(5, changed);   // only the 5 real rows exist to update
        var byId = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        foreach (var (_, daId, _) in real)
            Assert.Equal("missing", byId[daId]);
    }

    // ── 8. RecalculateReleaseStatusForArtifacts — both IN(...) clauses batched ─

    [Fact]
    public void RecalculateReleaseStatus_OverLimitDaIds_NoException()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 5);

        // First IN(...) (da.id) pushed past the limit with mostly-fake ids. All 5 das are 'present' → releases stay 'present'.
        var ids = real.Select(x => x.DaId)
            .Concat(Enumerable.Range(1_000_000, OverLimit).Select(i => $"da{i}"))
            .ToList();

        var ex = Record.Exception(() => store.RecalculateReleaseStatusForArtifacts(ids));
        Assert.Null(ex);
    }

    [Fact]
    public void RecalculateReleaseStatus_MultiChunkReleases_EveryReleaseUpdated()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 2_100);   // > 900 → the release-id IN(...) spans several chunks

        // Flip every derived artifact to 'missing', then recalc: each of the 2,100 releases must transition
        // to 'missing' (proves the second, chunked IN(...) covers every release across chunk boundaries).
        store.BatchUpdateDerivedArtifactStatus(real.Select(x => x.DaId).ToList(), "missing");
        var updated = store.RecalculateReleaseStatusForArtifacts(real.Select(x => x.DaId).ToList());

        Assert.Equal(2_100, updated);
        Assert.Equal(2_100, store.LoadReleasesByDatLine("leaf").Count(r => r.Status == "missing"));
    }

    // ── 9. GetArtifactVerifyInfos — over-limit lookup + batch equivalence (Verify) ─

    [Fact]
    public void GetArtifactVerifyInfos_OverLimit_NoException_RealRowsReturned()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 5);

        var ids = real.Select(x => x.DaId)
            .Concat(Enumerable.Range(1_000_000, OverLimit).Select(i => $"da{i}"))
            .ToList();

        var infos = store.GetArtifactVerifyInfos(ids);   // must not throw

        Assert.Equal(5, infos.Count);
        foreach (var (_, daId, _) in real)
            Assert.Contains(infos, x => x.DerivedArtifactId == daId);
    }

    [Fact]
    public void GetArtifactVerifyInfos_MultiBatch_EquivalentTo_SingleBatch()
    {
        var store = new DatLineStore(_db);
        var real  = Seed(store, 5);
        var realDaIds = real.Select(x => x.DaId).ToList();

        var padded      = realDaIds.Concat(Enumerable.Range(2_000_000, 2_000).Select(i => $"da{i}")).ToList();
        var singleInfos = store.GetArtifactVerifyInfos(realDaIds).Select(x => x.DerivedArtifactId).ToList();
        var multiInfos  = store.GetArtifactVerifyInfos(padded).Select(x => x.DerivedArtifactId).ToList();

        Assert.Equal(5, singleInfos.Count);      // full set present, not collapsed
        Assert.Equal(singleInfos, multiInfos);   // same set AND same (ReleaseName, FileName) order across chunks
    }
}

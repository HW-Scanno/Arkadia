using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// M9B2 tests for the extracted <see cref="LeafFinalizationEngine"/>: completeness, staging→source
/// promotion, transform dispatch (via a fake delegate — the real processors are covered elsewhere), the
/// Phase-8-consumed result sets, and the safe-copy invariant. Real temp filesystem + temp leaf DB; no
/// runtime data is touched. The transform delegate is a controllable fake, so these tests prove the
/// orchestration and the returned state, not processor internals.
/// </summary>
public sealed class LeafFinalizationEngineTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stagingRoot;
    private readonly string _sourceRoot;
    private readonly string _dbPath;

    public LeafFinalizationEngineTests()
    {
        _dir         = Path.Combine(Path.GetTempPath(), "ArkFinalize_" + Guid.NewGuid().ToString("N")[..8]);
        _stagingRoot = Path.Combine(_dir, "staging");
        _sourceRoot  = Path.Combine(_dir, "source");
        _dbPath      = Path.Combine(_dir, "leaf.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // Builds a leaf DB with one release + its expected files (via the real builder path).
    private DatLineStore SeedLeaf(string releaseName, params string[] roms)
    {
        var games = new List<DatParser.ParsedGame>
        {
            new()
            {
                Name = releaseName,
                Roms = roms.Select(r => new DatParser.ParsedRom { Name = r, Size = "3", Sha1 = new string('a', 40) }).ToList(),
            },
        };
        LeafDatDatabaseBuilder.Build(_dbPath, "leaf", games, null, CancellationToken.None);
        return new DatLineStore(_dbPath);
    }

    private static Dictionary<string, ReleaseRecord> Releases(DatLineStore store)
        => store.LoadReleases().ToDictionary(r => r.Id, StringComparer.Ordinal);

    private void WriteStaging(string safeFolder, string rom, byte[] bytes)
    {
        var dir = Path.Combine(_stagingRoot, safeFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, rom), bytes);
    }

    private LeafFinalizationResult Run(
        DatLineStore store, Dictionary<string, ReleaseRecord> releases, HashSet<string> affected,
        Action<LeafReleaseFinalizeArgs> transform,
        HashSet<string>? transformFailed = null, HashSet<string>? successful = null)
        => LeafFinalizationEngine.FinalizeLeaf(
            affected, Array.Empty<string>(), releases, store,
            _stagingRoot, _sourceRoot, "c64", "leaf", s => s,
            new IngestionResult(), new Progress<IngestionProgress>(_ => { }),
            transformFailed ?? new(StringComparer.Ordinal),
            new(StringComparer.Ordinal),
            successful ?? new(StringComparer.Ordinal),
            transform);

    // ── Completeness ────────────────────────────────────────────────────────

    [Fact]  // (1)(12) incomplete release stays staging and is returned Incomplete
    public void Incomplete_StaysStaging_ReturnedIncomplete()
    {
        var store = SeedLeaf("Game A", "a.d64", "b.d64");
        var releases = Releases(store);
        var rid = releases.Keys.First();
        WriteStaging("Game A", "a.d64", new byte[] { 1, 2, 3 });   // b.d64 missing → incomplete

        bool transformCalled = false;
        var res = Run(store, releases, new(StringComparer.Ordinal) { rid }, _ => transformCalled = true);

        Assert.Contains(rid, res.IncompleteReleaseIds);
        Assert.False(transformCalled);                                  // no promotion/transform
        Assert.True(File.Exists(Path.Combine(_stagingRoot, "Game A", "a.d64")));  // material remains staging
        Assert.False(Directory.Exists(Path.Combine(_sourceRoot, "Game A")));
    }

    [Fact]  // (2)(4)(5) complete release is promoted: all files move staging → source; staging dir cleaned
    public void Complete_PromotesAllFiles_StagingCleaned()
    {
        var store = SeedLeaf("Game B", "a.d64", "b.d64");
        var releases = Releases(store);
        var rid = releases.Keys.First();
        WriteStaging("Game B", "a.d64", new byte[] { 1 });
        WriteStaging("Game B", "b.d64", new byte[] { 2 });

        var res = Run(store, releases, new(StringComparer.Ordinal) { rid }, _ => { /* transform ok */ });

        Assert.True(File.Exists(Path.Combine(_sourceRoot, "Game B", "a.d64")));
        Assert.True(File.Exists(Path.Combine(_sourceRoot, "Game B", "b.d64")));
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "Game B")));   // empty staging dir removed
        Assert.Contains(rid, res.SuccessfulReleaseIds);                          // (11) success set once
    }

    [Fact]  // (3) a complete resumable release finalizes with NO new incoming (affected empty; resumable adds it)
    public void Resumable_Finalizable_WithoutNewIncoming()
    {
        var store = SeedLeaf("Game C", "c.d64");
        var releases = Releases(store);
        var rid = releases.Keys.First();
        WriteStaging("Game C", "c.d64", new byte[] { 9 });

        bool transformCalled = false;
        var res = LeafFinalizationEngine.FinalizeLeaf(
            new HashSet<string>(StringComparer.Ordinal),         // no newly-affected releases this run
            new[] { rid },                                        // resumable
            releases, store, _stagingRoot, _sourceRoot, "c64", "leaf", s => s,
            new IngestionResult(), new Progress<IngestionProgress>(_ => { }),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            _ => transformCalled = true);

        Assert.True(transformCalled);
        Assert.Contains(rid, res.SuccessfulReleaseIds);
        Assert.True(File.Exists(Path.Combine(_sourceRoot, "Game C", "c.d64")));
    }

    // ── Transform dispatch + result sets ────────────────────────────────────

    [Fact]  // (8) the finalizer calls the supplied transform delegate with the promoted source dir + files
    public void Dispatch_CallsTransformDelegate_WithSourceInputs()
    {
        var store = SeedLeaf("Game D", "d.d64");
        var releases = Releases(store);
        var rid = releases.Keys.First();
        WriteStaging("Game D", "d.d64", new byte[] { 7 });

        LeafReleaseFinalizeArgs? seen = null;
        Run(store, releases, new(StringComparer.Ordinal) { rid }, a => seen = a);

        Assert.NotNull(seen);
        Assert.Equal(rid, seen!.Release.Id);
        Assert.Equal(Path.Combine(_sourceRoot, "Game D"), seen.SourceDir);
        Assert.Single(seen.ExpectedFiles);
    }

    [Fact]  // (13)(14) a transform failure lands in transformFailedReleases and NOT the success set
    public void TransformFailure_RecordedNotPresent()
    {
        var store = SeedLeaf("Game E", "e.d64");
        var releases = Releases(store);
        var rid = releases.Keys.First();
        WriteStaging("Game E", "e.d64", new byte[] { 4 });

        var failed = new HashSet<string>(StringComparer.Ordinal);
        var success = new HashSet<string>(StringComparer.Ordinal);
        // Fake processor "fails" by adding to transformFailedReleases (exactly like the real processors).
        var res = Run(store, releases, new(StringComparer.Ordinal) { rid },
            a => failed.Add(a.Release.Id), transformFailed: failed, successful: success);

        Assert.Contains(rid, res.TransformFailedReleaseIds);
        Assert.DoesNotContain(rid, res.SuccessfulReleaseIds);           // status NOT set present
        Assert.Empty(success);
    }

    // ── Safe-copy invariant ─────────────────────────────────────────────────

    [Fact]  // (15) transform failure leaves the managed source intact (never both source and artifact gone)
    public void SafeCopy_TransformFailure_LeavesSourceIntact()
    {
        var store = SeedLeaf("Game F", "f.d64");
        var releases = Releases(store);
        var rid = releases.Keys.First();
        WriteStaging("Game F", "f.d64", new byte[] { 5 });

        var failed = new HashSet<string>(StringComparer.Ordinal);
        // Transform "fails" AFTER promotion — the promoted source must still exist for retry.
        Run(store, releases, new(StringComparer.Ordinal) { rid },
            a => failed.Add(a.Release.Id), transformFailed: failed);

        Assert.True(File.Exists(Path.Combine(_sourceRoot, "Game F", "f.d64")),
            "managed source copy must survive a transform failure");
    }

    [Fact]  // (6) promotion failure is reported and does not report success
    public void PromotionFailure_NoSuccess()
    {
        var store = SeedLeaf("Game G", "g.d64");
        var releases = Releases(store);
        var rid = releases.Keys.First();
        WriteStaging("Game G", "g.d64", new byte[] { 6 });
        // Make promotion fail: pre-create the SOURCE destination path as a directory so File.Move throws.
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "Game G", "g.d64"));

        bool transformCalled = false;
        var success = new HashSet<string>(StringComparer.Ordinal);
        var res = Run(store, releases, new(StringComparer.Ordinal) { rid },
            _ => transformCalled = true, successful: success);

        Assert.False(transformCalled);                       // no transform after failed promotion
        Assert.DoesNotContain(rid, res.SuccessfulReleaseIds);
    }
}

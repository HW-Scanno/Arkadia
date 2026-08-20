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
/// After a leaf is finalized, its empty <c>staging/&lt;platform&gt;/&lt;leafId&gt;</c> root — and, once the
/// platform's last leaf drains, the <c>staging/&lt;platform&gt;</c> root — are pruned (DIRECTORY-ONLY). Incomplete
/// releases keep their files and every ancestor. Real temp filesystem + real leaf DBs.
/// </summary>
public sealed class StagingRootCleanupTests : IDisposable
{
    private const string Platform = "archimedes";
    private readonly string _dir;

    public StagingRootCleanupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkStgRoot_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string StagingPlatform      => Path.Combine(_dir, "staging", Platform);
    private string LeafStaging(string leafId) => Path.Combine(StagingPlatform, leafId);
    private string SourceLeaf(string leafId)  => Path.Combine(_dir, "source", Platform, leafId);

    private DatLineStore SeedLeafDb(string leafId, params (string Name, string[] Roms)[] games)
    {
        var dbPath = Path.Combine(_dir, "db", leafId + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var parsed = games.Select(g => new DatParser.ParsedGame
        {
            Name = g.Name,
            Roms = g.Roms.Select(r => new DatParser.ParsedRom { Name = r, Size = "3", Sha1 = new string('a', 40) }).ToList(),
        }).ToList();
        LeafDatDatabaseBuilder.Build(dbPath, leafId, parsed, null, CancellationToken.None);
        return new DatLineStore(dbPath);
    }

    private void Stage(string leafId, string safeFolder, string rom, byte[] bytes)
    {
        var dir = Path.Combine(LeafStaging(leafId), safeFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, rom), bytes);
    }

    private void Finalize(string leafId, DatLineStore store, IEnumerable<string> affected)
    {
        var releases = store.LoadReleases().ToDictionary(r => r.Id, StringComparer.Ordinal);
        LeafFinalizationEngine.FinalizeLeaf(
            new HashSet<string>(affected, StringComparer.Ordinal), Array.Empty<string>(), releases, store,
            LeafStaging(leafId), SourceLeaf(leafId), Platform, leafId, IngestionPaths.SafeFolderName,
            new IngestionResult(), new Progress<IngestionProgress>(_ => { }),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            _ => { /* transform ok */ });
    }

    private static string Rid(DatLineStore s, string name) => s.LoadReleases().First(r => r.Name == name).Id;

    // ── Fully completed leaf → leaf root AND (only-leaf) platform root removed ────

    [Fact]
    public void CompletedLeaf_RemovesLeafAndPlatformRoots()
    {
        var store = SeedLeafDb("leaf-a", ("GameA", new[] { "a.bin", "b.bin" }));
        Stage("leaf-a", "GameA", "a.bin", new byte[] { 1 });
        Stage("leaf-a", "GameA", "b.bin", new byte[] { 2 });

        Finalize("leaf-a", store, new[] { Rid(store, "GameA") });

        Assert.True(File.Exists(Path.Combine(SourceLeaf("leaf-a"), "GameA", "a.bin")));   // promoted
        Assert.False(Directory.Exists(LeafStaging("leaf-a")));                            // leaf root gone
        Assert.False(Directory.Exists(StagingPlatform));                                  // platform root gone (drained)
    }

    // ── Fully completed platform: last leaf drains → platform root removed ────────

    [Fact]
    public void LastLeafDrains_RemovesPlatformRoot()
    {
        var a = SeedLeafDb("leaf-a", ("GA", new[] { "a.bin" }));
        var b = SeedLeafDb("leaf-b", ("GB", new[] { "b.bin" }));
        Stage("leaf-a", "GA", "a.bin", new byte[] { 1 });
        Stage("leaf-b", "GB", "b.bin", new byte[] { 2 });

        Finalize("leaf-a", a, new[] { Rid(a, "GA") });
        Assert.False(Directory.Exists(LeafStaging("leaf-a")));   // A gone
        Assert.True(Directory.Exists(StagingPlatform));          // platform kept — leaf-b still staged

        Finalize("leaf-b", b, new[] { Rid(b, "GB") });
        Assert.False(Directory.Exists(LeafStaging("leaf-b")));   // B gone
        Assert.False(Directory.Exists(StagingPlatform));         // platform root removed after last leaf
    }

    // ── Partial leaf: completed release removed; incomplete file + ancestors kept ─

    [Fact]
    public void PartialLeaf_PreservesIncompleteFileAndAncestors()
    {
        var store = SeedLeafDb("leaf-p",
            ("RelA", new[] { "a.bin" }),
            ("RelB", new[] { "b1.bin", "b2.bin" }));
        Stage("leaf-p", "RelA", "a.bin",  new byte[] { 1 });        // complete
        Stage("leaf-p", "RelB", "b1.bin", new byte[] { 2 });        // incomplete (b2 missing)

        Finalize("leaf-p", store, new[] { Rid(store, "RelA"), Rid(store, "RelB") });

        Assert.False(Directory.Exists(Path.Combine(LeafStaging("leaf-p"), "RelA")));            // completed tree gone
        Assert.True(File.Exists(Path.Combine(LeafStaging("leaf-p"), "RelB", "b1.bin")));        // no file deleted
        Assert.True(Directory.Exists(Path.Combine(LeafStaging("leaf-p"), "RelB")));             // release dir kept
        Assert.True(Directory.Exists(LeafStaging("leaf-p")));                                    // leaf dir kept
        Assert.True(Directory.Exists(StagingPlatform));                                          // platform dir kept
    }

    // ── Mixed Group: complete leaf removed, incomplete leaf + platform preserved ──

    [Fact]
    public void MixedGroup_RemovesCompleteLeaf_KeepsIncompleteLeafAndPlatform()
    {
        var a = SeedLeafDb("leaf-a", ("GA", new[] { "a.bin" }));
        var b = SeedLeafDb("leaf-b", ("GB", new[] { "b1.bin", "b2.bin" }));
        Stage("leaf-a", "GA", "a.bin",  new byte[] { 1 });        // complete
        Stage("leaf-b", "GB", "b1.bin", new byte[] { 2 });        // incomplete

        Finalize("leaf-a", a, new[] { Rid(a, "GA") });
        Finalize("leaf-b", b, new[] { Rid(b, "GB") });

        Assert.False(Directory.Exists(LeafStaging("leaf-a")));                        // complete leaf removed
        Assert.True(File.Exists(Path.Combine(LeafStaging("leaf-b"), "GB", "b1.bin"))); // incomplete file kept
        Assert.True(Directory.Exists(LeafStaging("leaf-b")));                          // incomplete leaf kept
        Assert.True(Directory.Exists(StagingPlatform));                               // platform root kept
    }
}

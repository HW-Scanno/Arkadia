using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Arkadia.Data;
using Arkadia.GroupDats;
using Arkadia.Ingestion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// LAZY-DIRECTORY-CREATION: per-DAT / per-leaf / per-release directories under <c>archive/</c>, <c>source/</c>,
/// <c>staging/</c> and <c>data/media/</c> are created ONLY immediately before real content is written there —
/// never because a DAT, Group, leaf or release exists in the catalog. Isolated temp app roots only.
/// MainWindow-only flows (ingestion bootstrap, Library load, archive commit) are pinned by source-structure checks,
/// like the existing wiring tests.
/// </summary>
public sealed class LazyDirectoryCreationTests : IDisposable
{
    private readonly string _app;    // isolated app root
    private readonly string _data;   // <app>/data (catalog.db, systems/, media/)

    public LazyDirectoryCreationTests()
    {
        _app  = Path.Combine(Path.GetTempPath(), "ArkLazyDirs_" + Guid.NewGuid().ToString("N")[..8]);
        _data = Path.Combine(_app, "data");
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_app, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AssertNoContentTrees()
    {
        Assert.False(Directory.Exists(Path.Combine(_app, "archive")),  "archive/ was created");
        Assert.False(Directory.Exists(Path.Combine(_app, "source")),   "source/ was created");
        Assert.False(Directory.Exists(Path.Combine(_app, "staging")),  "staging/ was created");
        Assert.False(Directory.Exists(Path.Combine(_data, "media")),   "data/media/ was created");
    }

    private CatalogService NewCatalog(string family)
    {
        var c = new CatalogService(_data);
        if (c.GetHardwareFamily(family) is null)
            c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
            {
                new() { Id = family, Name = family.ToUpperInvariant(), Manufacturer = "M", HardwareTypeId = "console" },
            });
        return c;
    }

    private static List<DatParser.ParsedGame> Games(int n, string prefix = "Game") =>
        Enumerable.Range(0, n).Select(i => new DatParser.ParsedGame
        {
            Name = $"{prefix} {i}",
            Roms = [new DatParser.ParsedRom { Name = $"{prefix} {i}.bin", Size = "10", Sha1 = new string((char)('a' + i % 6), 40) }],
            ContentKey = "sha1:" + new string((char)('a' + i % 6), 40),
        }).ToList();

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private static string Read(string rel) => File.ReadAllText(RepoFile(rel)).Replace("\r\n", "\n");

    private static string Region(string src, string start, string end)
    {
        var s = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(s >= 0, start);
        var e = src.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        Assert.True(e > s, end);
        return src.Substring(s, e - s);
    }

    // ── 1 / 4. DAT import and update ─────────────────────────────────────────

    [Fact]
    public void SingleDatImport_AndDatUpdate_CreateNoArchiveSourceStagingOrMediaTrees()
    {
        var catalog = NewCatalog("nds");
        var r = new SingleDatImportService(catalog, _data).Execute(new SingleDatImportRequest
        {
            HardwareFamilyId = "nds", Authority = "nointro", MediaTypeId = "cartridge", Variant = "main",
            DisplayName = "NoIntro - Main", Version = "v1", Games = Games(3),
        }, null, CancellationToken.None);
        Assert.Equal(SingleDatImportError.None, r.Error);
        AssertNoContentTrees();

        // DAT update (same reconciliation entry point MainWindow.RunUpdateWork uses).
        var dl    = catalog.LoadDatLines().Single(d => d.Id == r.DatLineId);
        var store = new DatLineStore(Path.Combine(_data, dl.DataStorePath));
        ReconciliationEngine.ApplyDatUpdate(store, dl.Id, Games(5));
        AssertNoContentTrees();
    }

    // ── 2. Group registration with many leaves ───────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task GroupCreate_WithManyLeaves_CreatesNoPerLeafDirectoryTrees()
    {
        const int leafCount = 120;
        var src = Path.Combine(_app, "src");
        for (int i = 0; i < leafCount; i++)
        {
            var abs = Path.Combine(src, $"F{i:D3}", "x.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            var sb = new StringBuilder("<?xml version=\"1.0\"?><datafile><header><name>L")
                .Append(i).Append("</name><version>1</version></header>")
                .Append($"<game name=\"G{i} (USA)\"><rom name=\"g{i}.bin\" size=\"{100 + i}\" crc=\"00000000\" sha1=\"{new string('a', 40)}\"/></game>")
                .Append("</datafile>");
            File.WriteAllText(abs, sb.ToString());
        }

        var catalog = NewCatalog("c64");
        var disc    = new DatGroupSourceDiscoveryService().Discover(src);
        var leaves  = disc.Leaves.Select(l => new GroupDatNewLeafPlan(
            "c64-tosec-" + l.RelativePath.Split('/')[0].ToLowerInvariant(), "other",
            l.RelativePath, l.SourcePath, l.DatName, l.DatVersion, l.GameCount)).ToImmutableArray();
        var plan = new GroupDatReconciliationPlan(
            GroupDatReconciliationMode.NewGroup, src, "c64", "C64", "tosec", "c64-tosec", "c64 tosec", "c64",
            ImmutableArray<GroupDatUpdateActionPlan>.Empty, leaves,
            ImmutableArray<GroupDatAbsentLeafPlan>.Empty, disc.Leaves.ToImmutableArray());

        var res = await new GroupDatExecutionService(catalog, _data).ExecuteCreateAsync(plan, null, CancellationToken.None);

        Assert.True(res.Succeeded);
        Assert.Equal(leafCount, catalog.LoadDatLines().Count(d => d.HardwareFamilyId == "c64"));
        AssertNoContentTrees();
    }

    // ── 3. Library loading / navigation ──────────────────────────────────────

    [Fact]
    public void LibraryLoad_AndEveryApplicationFlow_NeverCallsEnsureMediaFolders()
    {
        var lib = Region(Read("MainWindow.axaml.cs"), "private void BuildLibraryDatasetsCore()", "var releases = store.LoadReleases()");
        Assert.DoesNotContain("EnsureMediaFolders", lib);
        Assert.DoesNotContain("CreateDirectory", lib);

        var root    = Path.GetDirectoryName(RepoFile("Arkadia.sln"))!;
        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}Arkadia.Tests{Path.DirectorySeparatorChar}")
                     && !p.EndsWith($"{Path.DirectorySeparatorChar}MediaStore.cs", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains("EnsureMediaFolders(", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(callers);
    }

    [Fact]
    public void MediaReaders_OnMissingFolders_ReturnNothing_AndCreateNothing()
    {
        Assert.Null(MediaStore.FindCoverFront(_data, "c64", "leaf", "Game"));
        Assert.Empty(MediaStore.FindAllCoverRegions(_data, "c64", "leaf", "Game", "covers-front"));
        Assert.Empty(MediaStore.FindAllScreenshots(_data, "c64", "leaf", "Game"));
        Assert.Empty(MediaStore.FindLogos(_data, "c64", "leaf", "Game"));
        Assert.Null(MediaStore.FindMarquee(_data, "c64", "leaf", "Game"));
        Assert.Empty(MediaStore.FindManuals(_data, "c64", "leaf", "Game"));
        var path = MediaStore.NextIndexedCoverStem(_data, "c64", "leaf", "Game", "covers-front", "wor");
        Assert.EndsWith("game_wor_001", path);
        AssertNoContentTrees();
    }

    // ── 5. Logical Source Artifact registration ──────────────────────────────

    [Fact]
    public void RegisteringLogicalSourceArtifact_CreatesNoPhysicalSourceDirectory()
    {
        var store = new DatLineStore(Path.Combine(_data, "systems", "gc", "leaf.db"));
        store.SaveSourceArtifact(new SourceArtifactRecord
        {
            Id = "sa1", ContentIdentityKey = "sha1:" + new string('a', 40), SourceSizeBytes = 1_459_978_240,
            HashedSourceSha1 = new string('a', 40), HashedSourceMd5 = null, HashedSourceCrc32 = "0cb5d407",
            VerifiedAtUtc = DateTime.UtcNow,
        });
        Assert.NotNull(store.GetSourceByContentKey("sha1:" + new string('a', 40)));
        AssertNoContentTrees();
    }

    // ── 6. Real media write creates its parents ──────────────────────────────

    [Fact]
    public void RealMediaWrite_CreatesOnlyItsOwnParent_ExistingFilesUntouched()
    {
        var input = Path.Combine(_app, "cover.png");
        File.WriteAllBytes(input, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        var svc   = new ReleaseMediaCurationService(_data);
        var store = new DatLineStore(Path.Combine(_data, "systems", "snes", "d1.db"));
        _ = store.LoadReleases();   // ensure schema

        var asset = svc.AddMediaFile(Path.Combine(_data, "systems", "snes", "d1.db"), "r1", "Sonic",
                                     "snes", "d1", input, "cover-front");

        var coverDir = Path.Combine(MediaStore.DatLinePath(_data, "snes", "d1"), "covers-front");
        Assert.True(File.Exists(asset.FilePath));
        Assert.Equal(coverDir, Path.GetDirectoryName(asset.FilePath));
        // Only the folder that received content exists — none of the other 15 standard media folders.
        Assert.Equal(new[] { "covers-front" },
            Directory.GetDirectories(MediaStore.DatLinePath(_data, "snes", "d1")).Select(Path.GetFileName));

        // (11) an existing file stays readable and unchanged; the next index skips it.
        var before = File.ReadAllBytes(asset.FilePath);
        var next   = MediaStore.NextIndexedCoverStem(_data, "snes", "d1", "Sonic", "covers-front", "wor");
        Assert.EndsWith("sonic_wor_002", next);
        Assert.Equal(before, File.ReadAllBytes(asset.FilePath));
    }

    // ── 7. Real archive commit creates its parents (pinned in MainWindow) ────

    [Fact]
    public void ArchiveCommit_CreatesParentOnlyImmediatelyBeforeTheWrite()
    {
        var mw = Read("MainWindow.axaml.cs");

        // File-oriented: no eager CreateDirectory between the write plan and the collision guard; the parent is
        // created right before TransformEngine writes (RVZ promotes through RvzConversion, which creates it).
        var fileOriented = Region(mw, "private void ProcessFileOrientedRelease(", "// ── Configured leaf finalization adapter");
        var planToGuard  = Region(fileOriented, "var destName = Path.GetFileName(destPath);", "var guardDecision = Archive.ArchiveWriteCollisionGuard.Decide(");
        Assert.DoesNotContain("CreateDirectory", planToGuard);
        Assert.Contains("Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);\n                        if (!TransformEngine.ExecuteTransform(", fileOriented);

        // Every archive write-plan site: no CreateDirectory between computing the destination and the guard /
        // satisfaction check.
        foreach (var start in new[]
                 {
                     "private void ProcessFolderOrientedRelease(",
                     "private void ProcessReleaseShapeOrientedRelease(",
                     "private void ProcessFolderOrientedFolderRelease(",
                 })
        {
            var body  = mw[mw.IndexOf(start, StringComparison.Ordinal)..];
            var gate  = body.IndexOf("var guardDecision = Archive.ArchiveWriteCollisionGuard.Decide(", StringComparison.Ordinal);
            var plan  = body.IndexOf("var destPath", StringComparison.Ordinal);
            Assert.True(plan > 0 && gate > plan, start);
            Assert.DoesNotContain("CreateDirectory", body[plan..gate]);
        }
    }

    [Fact]
    public void RvzConversionPromotion_CreatesTheArchiveParent()
    {
        // Phase A promotes into a not-yet-existing archive folder (created only at the native-rename promotion).
        var src  = Path.Combine(_app, "in", "g.iso");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        var iso  = new byte[4096];
        new Random(3).NextBytes(iso);
        File.WriteAllBytes(src, iso);
        var dest = Path.Combine(_app, "archive", "gc", "leaf", "g.rvz");
        var exp  = new RvzExpectedLogical(iso.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(iso)).ToLowerInvariant(), "",
            Crc32Hasher.ComputeHex(new MemoryStream(iso)));
        var xform = new TransformRecord { Id = "rvz_compression", ToolId = "dolphintool", OutputExtension = ".rvz",
            CommandTemplate = "convert -f rvz -b 131072 -c zstd -l 5 -i \"{input}\" -o \"{output}\"" };

        var r = RvzConversion.Run(_app, xform, null, src, dest, exp,
            convertOverride: (_, output) =>
            {
                var bytes = new byte[RvzHeader.HeaderSize + 64];
                RvzHeader.BuildForTests(iso.Length, bytes.Length).CopyTo(bytes, 0);
                File.WriteAllBytes(output, bytes);
                return (true, "");
            },
            verifyOverride: _ => new DolphinVerifyResult(true, new RvzLogicalHashes(exp.Sha1, "", exp.Crc32), "", false, Array.Empty<string>()));

        Assert.True(r.Success, r.Error);
        Assert.True(File.Exists(dest));
        Assert.False(Directory.Exists(Path.Combine(_app, "source")));
        Assert.False(Directory.Exists(Path.Combine(_app, "staging")));
    }

    // ── 8 / 9. Physical staging and source writes ────────────────────────────

    private static Dictionary<string, ReleaseRecord> OneRelease(string id, string name) =>
        new(StringComparer.Ordinal) { [id] = new ReleaseRecord { Id = id, DatLineId = "leaf", Name = name, Status = "missing" } };

    [Fact]
    public void Staging_CreatesOnlyTheReleaseFolderThatReceivesAFile()
    {
        var incoming = Path.Combine(_app, "incoming-roms", "c64");
        Directory.CreateDirectory(incoming);
        var file = Path.Combine(incoming, "a.d64");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        var stagingRoot = Path.Combine(_app, "staging", "c64", "leaf");
        var releases    = OneRelease("r1", "Game A");
        releases["r2"]  = new ReleaseRecord { Id = "r2", DatLineId = "leaf", Name = "Game B", Status = "missing" };
        var copyPlan = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [file] = new() { ("r1", "a.d64") },
        };

        IngestionStagingEngine.StageTargets(copyPlan, releases, new HashSet<string>(), stagingRoot, "c64", "leaf",
            s => s, new IngestionResult(), new Progress<IngestionProgress>(_ => { }));

        Assert.True(File.Exists(Path.Combine(stagingRoot, "Game A", "a.d64")));
        Assert.Equal(new[] { "Game A" }, Directory.GetDirectories(stagingRoot).Select(Path.GetFileName));  // no "Game B"
        Assert.False(Directory.Exists(Path.Combine(_app, "source")));
        Assert.False(Directory.Exists(Path.Combine(_app, "archive")));
    }

    [Fact]
    public void Staging_TargetFailingPathResolution_CreatesNoReleaseFolder()
    {
        var incoming = Path.Combine(_app, "incoming-roms", "c64");
        Directory.CreateDirectory(incoming);
        var file = Path.Combine(incoming, "a.d64");
        File.WriteAllBytes(file, new byte[] { 1 });
        var stagingRoot = Path.Combine(_app, "staging", "c64", "leaf");
        var copyPlan = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [file] = new() { ("r1", @"..\..\escape.d64") },               // rejected by ResolveReleaseChild
        };

        IngestionStagingEngine.StageTargets(copyPlan, OneRelease("r1", "Game A"), new HashSet<string>(), stagingRoot,
            "c64", "leaf", s => s, new IngestionResult(), new Progress<IngestionProgress>(_ => { }));

        Assert.False(Directory.Exists(Path.Combine(stagingRoot, "Game A")));
        Assert.True(File.Exists(file));                                     // incoming untouched
    }

    [Fact]
    public void SourcePromotion_CreatesSourceParentsOnlyForAPromotedRelease()
    {
        var dbPath = Path.Combine(_data, "systems", "c64", "leaf.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        LeafDatDatabaseBuilder.Build(dbPath, "leaf", new List<DatParser.ParsedGame>
        {
            new() { Name = "Game A", Roms = [new DatParser.ParsedRom { Name = "a.d64", Size = "3", Sha1 = new string('a', 40) }] },
        }, null, CancellationToken.None);
        var store    = new DatLineStore(dbPath);
        var releases = store.LoadReleases().ToDictionary(r => r.Id, StringComparer.Ordinal);
        var rid      = releases.Keys.Single();

        var stagingRoot = Path.Combine(_app, "staging", "c64", "leaf");
        var sourceRoot  = Path.Combine(_app, "source",  "c64", "leaf");
        Directory.CreateDirectory(Path.Combine(stagingRoot, "Game A"));
        File.WriteAllBytes(Path.Combine(stagingRoot, "Game A", "a.d64"), new byte[] { 1, 2, 3 });
        Assert.False(Directory.Exists(sourceRoot));

        string? promotedTo = null;
        LeafFinalizationEngine.FinalizeLeaf(
            new HashSet<string>(StringComparer.Ordinal) { rid }, Array.Empty<string>(), releases, store,
            stagingRoot, sourceRoot, "c64", "leaf", s => s,
            new IngestionResult(), new Progress<IngestionProgress>(_ => { }),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            a => promotedTo = a.SourceDir);

        Assert.Equal(Path.Combine(sourceRoot, "Game A"), promotedTo);
        Assert.True(File.Exists(Path.Combine(sourceRoot, "Game A", "a.d64")));     // physical source write
        Assert.False(Directory.Exists(Path.Combine(_app, "archive")));
    }

    // ── 10. Read-only operations on missing directories ──────────────────────

    [Fact]
    public void ReadOnlyIngestionHelpers_OnMissingRoots_DoNotThrow_AndCreateNothing()
    {
        var stagingRoot = Path.Combine(_app, "staging", "c64", "leaf");
        var sourceRoot  = Path.Combine(_app, "source",  "c64", "leaf");
        var skipDir     = Path.Combine(_app, "incoming-skip", "c64");

        StaleUnwantedCleanup.Run(stagingRoot, sourceRoot, skipDir, new List<(string, string)> { ("Game A", "unwanted") });
        StaleCueOnlyStagingCleanup.Run(stagingRoot, skipDir, _ => false);
        ResumableStagingDetector.Detect(stagingRoot, new List<ResumeReleaseInput>
        {
            new("r1", "Game A", "missing", new List<string> { "a.d64" }, new List<string>()),
        });
        Assert.Empty(Arkadia.Systems.StagingSnapshot.LeafIdsWithStaging(_app, "c64"));
        IngestionPaths.RemoveEmptyDirectoriesBottomUp(stagingRoot);

        AssertNoContentTrees();
        Assert.False(Directory.Exists(skipDir));
    }

    [Fact]
    public void IngestionBootstrap_NoLongerCreatesPerLeafStagingOrSourceRoots()
    {
        var w = Region(Read("MainWindow.axaml.cs"), "private IngestionResult RunIngestionWork(", "// ── Archive-output validation gate (M1f)");
        Assert.DoesNotContain("Directory.CreateDirectory(stagingRoot)", w);
        Assert.DoesNotContain("Directory.CreateDirectory(sourceRoot)", w);
        Assert.Contains("Directory.CreateDirectory(incomingDir);", w);   // platform-level drop/quarantine folders kept
        Assert.Contains("Directory.CreateDirectory(skipDir);", w);
    }
}

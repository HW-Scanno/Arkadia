using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// DAT-NAMING-2 persistence: the UI-free Single DAT creation service + the pure-INSERT catalog method. Real
/// CatalogService and leaf DBs over temp directories only.
/// </summary>
public sealed class SingleDatImportServiceTests : IDisposable
{
    private readonly string _dir;

    public SingleDatImportServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkNaming2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogService NewCatalog(string family = "nds")
    {
        var c = new CatalogService(_dir);
        if (c.GetHardwareFamily(family) is null)
            c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
            {
                new() { Id = family, Name = family.ToUpperInvariant(), Manufacturer = "M", HardwareTypeId = "console" },
            });
        return c;
    }

    private static DatParser.ParsedGame Game(string name, string sha1Seed = "a", string workingState = "")
        => new()
        {
            Name = name, WorkingState = workingState,
            Roms = [new DatParser.ParsedRom { Name = name + ".bin", Size = "10", Sha1 = new string(sha1Seed[0], 40) }],
            ContentKey = "sha1:" + new string(sha1Seed[0], 40),
        };

    private static SingleDatImportRequest Req(
        string variant = "main", string display = "NoIntro - Main", string family = "nds",
        string authority = "nointro", string media = "cartridge", int games = 3)
        => new()
        {
            HardwareFamilyId = family, Authority = authority, MediaTypeId = media, Variant = variant,
            DisplayName = display, Version = "v1",
            Games = Enumerable.Range(0, games).Select(i => Game($"Game {i}", ((char)('a' + i)).ToString())).ToList(),
        };

    private SingleDatImportResult Run(CatalogService c, SingleDatImportRequest r, CancellationToken ct = default)
        => new SingleDatImportService(c, _dir).Execute(r, null, ct);

    private string LeafPath(string family, string id) => Path.Combine(_dir, "systems", family, id + ".db");

    private static DatLineRecord Legacy(string id, string family = "nds", string authority = "nointro", string media = "cartridge")
        => new()
        {
            Id = id, HardwareFamilyId = family, Name = media, Authority = authority, MediaTypeId = media,
            Version = "old", DataStorePath = $"systems/{family}/{id}.db", ReleaseCount = 5, ImportedAtUtc = DateTime.UtcNow,
        };

    private int CountRows() => new CatalogService(_dir).LoadDatLines().Count;

    private void RawExec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "catalog.db")}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── 1. Legacy DAT imported before this change ───────────────────────────────

    [Fact]
    public void LegacyDat_IsUntouchedByANewImport_AndKeepsNullDisplayName()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord> { Legacy("nds-nointro-cartridge") });

        var r = Run(c, Req());
        Assert.True(r.Success, r.Message);

        var legacy = c.LoadDatLines().Single(l => l.Id == "nds-nointro-cartridge");
        Assert.Null(legacy.DisplayName);
        Assert.Equal("cartridge", legacy.Name);
        Assert.Equal("old", legacy.Version);
        Assert.Equal(5, legacy.ReleaseCount);
        Assert.Equal("systems/nds/nds-nointro-cartridge.db", legacy.DataStorePath);
        Assert.Equal(2, c.LoadDatLines().Count);
    }

    // ── 2/3. Default and custom variant ─────────────────────────────────────────

    [Fact]
    public void NewDat_DefaultVariantMain()
    {
        var c = NewCatalog();
        var r = Run(c, Req());
        Assert.True(r.Success, r.Message);
        Assert.Equal("nds-nointro-cartridge-main", r.DatLineId);
        Assert.Equal(3, r.ReleaseCount);
    }

    [Fact]
    public void NewDat_CustomVariantDownloadplay()
    {
        var c = NewCatalog();
        var r = Run(c, Req(variant: "downloadplay", display: "NoIntro - Download Play", media: "other"));
        Assert.True(r.Success, r.Message);
        Assert.Equal("nds-nointro-other-downloadplay", r.DatLineId);
    }

    // ── 4. Display name persists across reopen ──────────────────────────────────

    [Fact]
    public void CustomDisplayName_PersistsAfterReopeningTheCatalog()
    {
        var c = NewCatalog();
        Assert.True(Run(c, Req(display: "  NoIntro - Main (curated)  ")).Success);

        SqliteConnection.ClearAllPools();
        var reopened = new CatalogService(_dir);
        var line = Assert.Single(reopened.LoadDatLines());
        Assert.Equal("NoIntro - Main (curated)", line.DisplayName);     // trimmed, otherwise verbatim
        Assert.Equal("nds-nointro-cartridge-main", line.Id);
    }

    // ── 5/6. Same system+authority+media ────────────────────────────────────────

    [Fact]
    public void SameSystemAuthorityMedia_DifferentVariants_AreTwoIndependentDats()
    {
        var c = NewCatalog();
        Assert.True(Run(c, Req(variant: "main",  display: "NoIntro - Main")).Success);
        Assert.True(Run(c, Req(variant: "extra", display: "NoIntro - Extra", games: 2)).Success);

        var lines = c.LoadDatLines().OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "nds-nointro-cartridge-extra", "nds-nointro-cartridge-main" }, lines.Select(l => l.Id));
        Assert.Equal(new[] { 2, 3 }, lines.Select(l => l.ReleaseCount));
        Assert.True(File.Exists(LeafPath("nds", "nds-nointro-cartridge-extra")));
        Assert.True(File.Exists(LeafPath("nds", "nds-nointro-cartridge-main")));
        Assert.All(lines, l => Assert.Equal(("nds", "nointro", "cartridge"), (l.HardwareFamilyId, l.Authority, l.MediaTypeId)));
    }

    [Fact]
    public void SameTriple_DifferentDisplayNames_ArePersistedIndependently()
    {
        var c = NewCatalog();
        Run(c, Req(variant: "main",  display: "Alpha"));
        Run(c, Req(variant: "extra", display: "Beta"));

        var byId = c.LoadDatLines().ToDictionary(l => l.Id);
        Assert.Equal("Alpha", byId["nds-nointro-cartridge-main"].DisplayName);
        Assert.Equal("Beta",  byId["nds-nointro-cartridge-extra"].DisplayName);
    }

    [Fact]
    public void SameTriple_SameDisplayName_IsAllowed_AndIdsStayDistinct()
    {
        var c = NewCatalog();
        Assert.True(Run(c, Req(variant: "main",  display: "Same")).Success);
        Assert.True(Run(c, Req(variant: "extra", display: "Same")).Success);
        Assert.Equal(2, c.LoadDatLines().Select(l => l.Id).Distinct().Count());
    }

    // ── 7. Invalid variant / display name block the import ──────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("Main")]
    [InlineData("a b")]
    [InlineData("../evil")]
    [InlineData("main\n")]
    public void InvalidVariant_CreatesNothing(string variant)
    {
        var c = NewCatalog();
        var r = Run(c, Req(variant: variant));
        Assert.False(r.Success);
        Assert.Equal(SingleDatImportError.InvalidId, r.Error);
        Assert.Empty(c.LoadDatLines());
        Assert.False(Directory.Exists(Path.Combine(_dir, "systems")) && Directory.EnumerateFiles(Path.Combine(_dir, "systems"), "*", SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDisplayName_CreatesNothing(string display)
    {
        var c = NewCatalog();
        var r = Run(c, Req(display: display));
        Assert.Equal(SingleDatImportError.InvalidDisplayName, r.Error);
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]
    public void NoReleases_CreatesNothing()
    {
        var c = NewCatalog();
        var r = Run(c, Req(games: 0));
        Assert.Equal(SingleDatImportError.NoReleases, r.Error);
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]
    public void UnknownSystemOrMedia_CreatesNothing()
    {
        var c = NewCatalog();
        Assert.Equal(SingleDatImportError.UnknownSystem,    Run(c, Req(family: "zzz")).Error);
        Assert.Equal(SingleDatImportError.UnknownMediaType, Run(c, Req(media: "nomedia")).Error);
        Assert.Empty(c.LoadDatLines());
    }

    // ── 8/9. Collisions ─────────────────────────────────────────────────────────

    [Fact]
    public void CaseInsensitiveCollision_Blocks_AndNeverOverwritesTheExistingDat()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord> { Legacy("NDS-NoIntro-Cartridge-Main") });   // legacy odd-case id

        var r = Run(c, Req());
        Assert.False(r.Success);
        Assert.Equal(SingleDatImportError.IdCollision, r.Error);

        var only = Assert.Single(c.LoadDatLines());
        Assert.Equal("NDS-NoIntro-Cartridge-Main", only.Id);
        Assert.Equal("old", only.Version);
        Assert.Null(only.DisplayName);
        Assert.False(File.Exists(LeafPath("nds", "nds-nointro-cartridge-main")));
    }

    [Fact]
    public void CollisionWithAGroupLeaf_Blocks()
    {
        var c = NewCatalog("c64");
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "C64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec",
            Leaves = new[]
            {
                new GroupDatCatalogLeafCreate
                {
                    DatLine = Legacy("c64-tosec-disk-games", "c64", "tosec", "other"),
                    RelativeDatPath = "G/g.dat", SourceDatName = "g.dat", SourceDatSha256 = new string('a', 64),
                    LastSeenGroupRevision = 0, InitialWorkingStates = Array.Empty<GroupDatInitialWorkingState>(),
                },
            },
        });
        // Build a request whose rebuilt id equals the leaf id.
        var r = Run(c, Req(family: "c64", authority: "tosec", media: "disk", variant: "games", display: "x"));
        Assert.Equal(SingleDatImportError.IdCollision, r.Error);
        Assert.Single(c.LoadDatLines());
    }

    [Fact]
    public void CatalogInsert_IsAPureInsert_NeverAnUpsert()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord> { Legacy("nds-nointro-cartridge-main") });

        var dup = Legacy("NDS-NOINTRO-CARTRIDGE-MAIN");     // case variant
        dup.DisplayName = "Overwrite attempt";
        var ex = Assert.Throws<SingleDatCreateException>(() => c.CreateSingleDatLine(dup));
        Assert.Equal(SingleDatCreateFailure.IdCollision, ex.Failure);

        var exact = Legacy("nds-nointro-cartridge-main");
        Assert.Equal(SingleDatCreateFailure.IdCollision,
            Assert.Throws<SingleDatCreateException>(() => c.CreateSingleDatLine(exact)).Failure);

        var only = Assert.Single(c.LoadDatLines());
        Assert.Null(only.DisplayName);
        Assert.Equal("old", only.Version);
    }

    [Fact]
    public void CatalogInsert_RejectsDuplicateStorePath_UnknownFamily_UnknownMedia()
    {
        var c = NewCatalog();
        c.SaveDatLines(new List<DatLineRecord> { Legacy("nds-a") });

        var samePath = Legacy("nds-b"); samePath.DataStorePath = "SYSTEMS/nds/nds-a.db";
        Assert.Equal(SingleDatCreateFailure.StorePathCollision,
            Assert.Throws<SingleDatCreateException>(() => c.CreateSingleDatLine(samePath)).Failure);

        Assert.Equal(SingleDatCreateFailure.UnknownHardwareFamily,
            Assert.Throws<SingleDatCreateException>(() => c.CreateSingleDatLine(Legacy("x-1", "nofamily"))).Failure);

        Assert.Equal(SingleDatCreateFailure.UnknownMediaType,
            Assert.Throws<SingleDatCreateException>(() => c.CreateSingleDatLine(Legacy("x-2", media: "nomedia"))).Failure);

        Assert.Single(c.LoadDatLines());
    }

    // ── 10. One id across catalog, leaf DB and storage ──────────────────────────

    [Fact]
    public void FullId_IsConsistentAcrossCatalogLeafDbAndStoragePath()
    {
        var c = NewCatalog();
        var r = Run(c, Req(variant: "downloadplay", display: "NoIntro - Download Play", media: "other"));
        Assert.True(r.Success, r.Message);
        const string id = "nds-nointro-other-downloadplay";

        var line = Assert.Single(c.LoadDatLines());
        Assert.Equal(id, line.Id);
        Assert.Equal($"systems/nds/{id}.db", line.DataStorePath);
        Assert.Equal("other", line.MediaTypeId);
        Assert.Equal("other", line.Name);                 // `name` keeps its historical meaning
        Assert.Equal(3, line.ReleaseCount);
        Assert.Equal("v1", line.Version);

        var abs = Path.Combine(_dir, line.DataStorePath);
        Assert.True(File.Exists(abs));
        var store = new DatLineStore(abs);
        var releases = store.LoadReleases();
        Assert.Equal(3, releases.Count);
        Assert.All(releases, rel => Assert.Equal(id, rel.DatLineId));
        Assert.Equal(3, store.LoadAllReleaseFiles().Values.Sum(v => v.Count));

        // No group metadata (this is a Single DAT).
        Assert.Null(c.GetDatLineGroupMetadata(id)!.GroupId);
        // No temp files left behind.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "systems"), "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void WorkingStateHints_AreAppliedAfterCreation()
    {
        var c = NewCatalog();
        var req = new SingleDatImportRequest
        {
            HardwareFamilyId = "nds", Authority = "nointro", MediaTypeId = "cartridge", Variant = "main",
            DisplayName = "x", Games = [Game("Game ws", "a", "working")],
        };
        Assert.True(Run(c, req).Success);
        Assert.Equal("working", c.GetWorkingState("Game ws")?.State);
    }

    // ── 13. Failure handling ────────────────────────────────────────────────────

    [Fact]
    public void ExistingLeafFile_IsNeverOverwritten()
    {
        var c = NewCatalog();
        var path = LeafPath("nds", "nds-nointro-cartridge-main");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "precious");

        var r = Run(c, Req());
        Assert.Equal(SingleDatImportError.LeafDatabaseExists, r.Error);
        Assert.Equal("precious", File.ReadAllText(path));
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]
    public void ConcurrentClaimDuringBuild_IsCaughtByTheSecondValidation_NothingPublished()
    {
        var c = NewCatalog();
        var svc = new SingleDatImportService(c, _dir)
        {
            // Another actor registers the same id (case variant) while the temp DB is being built.
            OnLeafBuiltForTests = () => c.SaveDatLines(new List<DatLineRecord> { Legacy("NDS-NOINTRO-CARTRIDGE-MAIN") }),
        };

        var r = svc.Execute(Req(), null, CancellationToken.None);

        Assert.Equal(SingleDatImportError.IdCollision, r.Error);
        var only = Assert.Single(c.LoadDatLines());
        Assert.Equal("NDS-NOINTRO-CARTRIDGE-MAIN", only.Id);
        Assert.Equal("old", only.Version);
        Assert.False(File.Exists(LeafPath("nds", "nds-nointro-cartridge-main")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "systems"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CatalogRaceAfterPublish_RemovesOnlyTheFileThisRunCreated_AndLeavesTheOtherRowIntact()
    {
        var c = NewCatalog();
        var svc = new SingleDatImportService(c, _dir)
        {
            // Race: a conflicting row appears AFTER the second validation, before our INSERT.
            OnLeafPublishedForTests = () => RawExec(
                "INSERT INTO dat_lines(id, hardware_family_id, name, authority, media_type_id, imported_at_utc, data_store_path) " +
                "VALUES('nds-nointro-cartridge-main','nds','cartridge','nointro','cartridge','2026-01-01T00:00:00.0000000Z','other/path.db')"),
        };

        var r = svc.Execute(Req(), null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Equal(SingleDatImportError.IdCollision, r.Error);
        var only = Assert.Single(c.LoadDatLines());
        Assert.Equal("other/path.db", only.DataStorePath);          // the other row is untouched
        Assert.Null(only.DisplayName);
        Assert.False(File.Exists(LeafPath("nds", "nds-nointro-cartridge-main")));   // our published file removed
    }

    [Fact]
    public void Cancelled_CreatesNothing()
    {
        var c = NewCatalog();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = Run(c, Req(), cts.Token);
        Assert.Equal(SingleDatImportError.Cancelled, r.Error);
        Assert.Empty(c.LoadDatLines());
    }

    [Fact]
    public void FailedImport_DoesNotDamageExistingDatsOrVolumes()
    {
        var c = NewCatalog();
        Assert.True(Run(c, Req(variant: "main")).Success);
        c.SaveVolume(new VolumeRecord
        {
            Id = "v1", Label = "vol", PlatformId = "nds", DatLineId = "nds-nointro-cartridge-main",
            Status = "present", PlannedSizeBytes = 100, ActualSizeBytes = 10, CreatedAt = DateTime.UtcNow,
        });
        var before = File.ReadAllBytes(LeafPath("nds", "nds-nointro-cartridge-main"));

        Assert.False(Run(c, Req(variant: "main")).Success);           // collision
        Assert.False(Run(c, Req(variant: "Bad")).Success);            // invalid

        Assert.Single(c.LoadDatLines());
        Assert.Equal("nds-nointro-cartridge-main", Assert.Single(c.GetVolumes()).DatLineId);
        Assert.Equal(before, File.ReadAllBytes(LeafPath("nds", "nds-nointro-cartridge-main")));
        Assert.Equal(3, new DatLineStore(LeafPath("nds", "nds-nointro-cartridge-main")).LoadReleases().Count);
    }

    // ── 12. Group creation unchanged ────────────────────────────────────────────

    [Fact]
    public void GroupCreate_StillProducesLeavesWithNullDisplayName_AndSingleImportDoesNotTouchThem()
    {
        var c = NewCatalog("c64");
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "C64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec",
            Leaves = new[]
            {
                new GroupDatCatalogLeafCreate
                {
                    DatLine = Legacy("c64-tosec-games", "c64", "tosec", "other"),
                    RelativeDatPath = "G/g.dat", SourceDatName = "g.dat", SourceDatSha256 = new string('a', 64),
                    LastSeenGroupRevision = 0, InitialWorkingStates = Array.Empty<GroupDatInitialWorkingState>(),
                },
            },
        });
        Assert.True(Run(c, Req(family: "c64", authority: "tosec", media: "other", variant: "extra", display: "T - Extra")).Success);

        var leaf = Assert.Single(c.GetLeavesForGroup("c64-tosec"));
        Assert.Equal("c64-tosec-games", leaf.DatLine.Id);
        Assert.Equal("c64-tosec", leaf.GroupMetadata.GroupId);
        Assert.Null(c.LoadDatLines().Single(l => l.Id == "c64-tosec-games").DisplayName);
        Assert.Equal(2, c.LoadDatLines().Count);
    }
}

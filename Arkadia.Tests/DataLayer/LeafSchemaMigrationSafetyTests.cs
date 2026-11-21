using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.GroupDats;
using Arkadia.Ingestion;
using Arkadia.Library;
using Arkadia.Pending;
using Arkadia.Systems;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// PERFORMANCE-03 final safety review — leaf schema migration. Startup used to open EVERY leaf database in write mode
/// (the Pending scan), which brought each leaf's schema up to date before the window was interactive. It no longer
/// does, so a leaf whose schema predates the running version can now be reached first by any reader, in any order.
///
/// <para><b>Contract under test.</b> A leaf has no schema version: the writable <see cref="DatLineStore"/> constructor
/// migrates on EVERY open (<c>EnsureSchema</c>: every table is CREATE IF NOT EXISTS, the added columns are ALTERed in),
/// before the caller can run a query — so a reader that opens a leaf through it depends on nobody else having run. The
/// strictly read-only open (<see cref="DatLineStore.OpenReadOnly"/>) never migrates, by design; it must therefore read
/// an older leaf as that leaf's own data means.</para>
///
/// <para>The databases are isolated temporary files created with the DDL of two PAST versions
/// (<see cref="HistoricalLeafSchemas"/>, generated from the repository history) and filled by plain SQL — never through
/// the current store. Every result on a never-migrated leaf is compared with the same reader on an identical twin that
/// was migrated first.</para>
/// </summary>
public sealed class LeafSchemaMigrationSafetyTests : IDisposable
{
    private const string Single = "psx-redump-cd";
    private const string Group  = "c64-tosec";
    private const string LeafA  = "c64-tosec-games-d64";
    private const string LeafB  = "c64-tosec-demos-d64";

    private static readonly string[] AllLeaves   = [Single, LeafA, LeafB];
    private static readonly string[] GroupLeaves = [LeafA, LeafB];

    public static TheoryData<string> Generations => new() { "v3.2", "v7" };

    private static string Ddl(string generation) => generation switch
    {
        "v3.2" => HistoricalLeafSchemas.V3_2,
        "v7"   => HistoricalLeafSchemas.V7,
        _      => throw new ArgumentOutOfRangeException(nameof(generation)),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ArkSchema_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Fixture> _fixtures = [];

    public void Dispose()
    {
        // Release only these fixtures' own pooled connections (never ClearAllPools: other classes run in parallel).
        foreach (var f in _fixtures) f.ReleasePools();
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── Fixture: a catalog + three leaf databases created with a HISTORICAL schema ───────────────────────────

    private sealed class Fixture
    {
        public required string         Dir     { get; init; }   // application root
        public required string         DataDir { get; init; }
        public required CatalogService Catalog { get; init; }

        public string DbPath(string leafId) => Path.Combine(DataDir, RelPath(leafId));

        public void ReleasePools()
        {
            try
            {
                foreach (var leaf in AllLeaves)
                {
                    SqliteConnection.ClearPool(new SqliteConnection($"Data Source={DbPath(leaf)}"));
                    SqliteConnection.ClearPool(new SqliteConnection($"Data Source={DbPath(leaf)};Mode=ReadOnly"));
                }
                SqliteConnection.ClearPool(new SqliteConnection($"Data Source={Path.Combine(DataDir, "catalog.db")}"));
            }
            catch { }
        }
    }

    private static string PlatformOf(string datLineId) => datLineId[..datLineId.IndexOf('-')];
    private static string RelPath(string datLineId)    => $"systems/{PlatformOf(datLineId)}/{datLineId}.db";

    private static DatLineRecord Line(string id, string authority, string media) => new()
    {
        Id = id, HardwareFamilyId = PlatformOf(id), Name = media, Authority = authority, MediaTypeId = media,
        Version = "1", DataStorePath = RelPath(id), ReleaseCount = 8,
        ImportedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    private static void Exec(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One leaf database as an OLDER version left it: that version's DDL, then rows written with plain SQL (8 releases
    /// in every status, their files, two present derived artifacts, a pending reconciliation, a metadata row; for v7
    /// also a legacy "physical-media" curation row and an extra note). Deterministic ids, so two fixtures are twins.
    /// </summary>
    private static void CreateOldLeaf(string dbPath, string leafId, string generation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Exec(dbPath, Ddl(generation));

        string[] statuses = ["present", "present", "missing", "pending", "unwanted", "outdated", "lost", "missing"];
        var sql = new System.Text.StringBuilder("BEGIN;\n");
        for (int n = 1; n <= statuses.Length; n++)
        {
            sql.AppendLine($"""
                INSERT INTO releases(id, dat_line_id, name, status, tier, region, languages, format, size, release_content_key, introduced_at_utc, content_category_id)
                VALUES('{leafId}-r{n}', '{leafId}', '{leafId} Game {n:D2}', '{statuses[n - 1]}', NULL, 'EU', 'en', 'bin', '1024', 'rck-{leafId}-{n}', '2026-03-0{n}T10:00:00.0000000Z', 'games');
                INSERT INTO release_files(id, release_id, rom_name, size, crc, md5, sha1)
                VALUES('{leafId}-r{n}-f1', '{leafId}-r{n}', '{leafId}-game{n:D2}.bin', '1024', 'crc{n}', 'md5{n}', 'sha1-{leafId}-{n}');
                """);
        }
        for (int n = 1; n <= 2; n++)
        {
            sql.AppendLine($"""
                INSERT INTO content_identities(content_identity_key, dat_sha1, dat_md5, dat_crc32, created_at_utc)
                VALUES('sha1:{leafId}-cik{n}', 'sha1-{leafId}-{n}', NULL, NULL, '2026-03-01T10:00:00Z');
                INSERT INTO release_content_links(id, release_id, content_identity_key, created_at_utc)
                VALUES('{leafId}-l{n}', '{leafId}-r{n}', 'sha1:{leafId}-cik{n}', '2026-03-01T10:00:00Z');
                INSERT INTO source_artifacts(id, content_identity_key, source_size_bytes, hashed_source_sha1, verified_at_utc)
                VALUES('{leafId}-sa{n}', 'sha1:{leafId}-cik{n}', 1024, 'sha1-{leafId}-{n}', '2026-03-01T10:00:00Z');
                INSERT INTO derived_artifacts(id, storage_strategy_id, source_artifact_id, content_identity_key, file_name, relative_path, derived_size_bytes, hashed_derived_sha1, status, created_at_utc, archive_tier)
                VALUES('{leafId}-da{n}', 'chd', '{leafId}-sa{n}', 'sha1:{leafId}-cik{n}', '{leafId}-{n}.chd', 'archive/x/{leafId}/{leafId}-{n}.chd', 4096, 'dsha1-{leafId}-{n}', 'present', '2026-03-01T10:00:00Z', 'A');
                """);
        }
        sql.AppendLine($"""
            INSERT INTO pending_reconciliations(id, new_release_id, outdated_release_id, target_name, reason, created_at_utc, status)
            VALUES('{leafId}-p1', '{leafId}-r3', '{leafId}-r6', 'target.chd', 'content_hash_match', '2026-03-02T10:00:00.0000000Z', 'pending');
            INSERT INTO release_metadata(release_id, title, original_title, developer, publisher, year, languages, alternate_titles, description, scraped_at_utc)
            VALUES('{leafId}-r1', 'Title One', 'Original One', 'Dev', 'Pub', '1990', 'en', '', 'A description', '2026-03-03T10:00:00Z');
            """);
        if (generation == "v7")
            sql.AppendLine($"""
                INSERT INTO release_media_curation(release_id, media_type, file_path, is_preferred, is_excluded, created_at, updated_at)
                VALUES('{leafId}-r1', 'physical-media', 'media/{leafId}/box.png', 1, 0, '2026-03-04T10:00:00Z', '2026-03-04T10:00:00Z');
                INSERT INTO release_extra_notes(release_id, notes, created_at, updated_at)
                VALUES('{leafId}-r1', 'A curator note', '2026-03-04T10:00:00Z', '2026-03-04T10:00:00Z');
                """);
        sql.AppendLine("COMMIT;");
        Exec(dbPath, sql.ToString());
    }

    /// <summary>
    /// A complete application data folder whose three leaf databases (one Single DAT, one Group with two leaves) were
    /// left by an older version. <paramref name="migrateFirst"/> builds the reference twin: the same leaves, each
    /// opened once in write mode before anything is read.
    /// </summary>
    private Fixture Build(string generation, bool migrateFirst = false)
    {
        var dir     = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        var dataDir = Path.Combine(dir, "data");
        Directory.CreateDirectory(dataDir);

        var c = new CatalogService(dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" },
            new() { Id = "psx", Name = "PlayStation",  Manufacturer = "Sony",      HardwareTypeId = "" },
        });
        c.SaveDatLines(new List<DatLineRecord> { Line(Single, "redump", "cd") });
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = Group, DisplayName = "Commodore 64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec",
            Leaves = GroupLeaves.Select((id, i) => new GroupDatCatalogLeafCreate
            {
                DatLine = Line(id, "tosec", "floppy"), RelativeDatPath = $"Set{i:D3}/g.dat", SourceDatName = "g.dat",
                SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
            }).ToList(),
        });

        var f = new Fixture { Dir = dir, DataDir = dataDir, Catalog = c };
        _fixtures.Add(f);
        foreach (var leaf in AllLeaves) CreateOldLeaf(f.DbPath(leaf), leaf, generation);

        // One volume per owner; the first present artifact of each leaf is assigned, the second is not.
        var now = DateTime.UtcNow;
        c.SaveVolume(new VolumeRecord { Id = "v-single", Label = "V SINGLE", PlatformId = "psx", DatLineId = Single, Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = now });
        c.SaveVolume(new VolumeRecord { Id = "v-group",  Label = "V GROUP",  PlatformId = "c64", GroupId = Group,     Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = now });
        foreach (var (vol, leaf) in new[] { ("v-single", Single), ("v-group", LeafA), ("v-group", LeafB) })
            c.SaveVolumeArtifact(new VolumeArtifactRecord
            {
                Id = $"va-{leaf}", VolumeId = vol, DatLineId = leaf, DerivedArtifactId = $"{leaf}-da1",
                ContentIdentityKey = "", Status = "present_in_final", AddedAtUtc = now,
            });

        if (migrateFirst)
            foreach (var leaf in AllLeaves) _ = new DatLineStore(f.DbPath(leaf));
        return f;
    }

    // ── Schema inspection (its own non-pooled read-only connection; never the store) ─────────────────────────

    private static List<string> Query(string dbPath, string sql)
    {
        var rows = new List<string>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(r.IsDBNull(0) ? "<null>" : r.GetValue(0).ToString()!);
        return rows;
    }

    private static bool HasColumn(string dbPath, string table, string column)
        => Query(dbPath, $"SELECT name FROM pragma_table_info('{table}')").Contains(column);

    private static bool HasTable(string dbPath, string table)
        => Query(dbPath, "SELECT name FROM sqlite_master WHERE type = 'table'").Contains(table);

    /// <summary>The complete schema text, as stored in the database.</summary>
    private static string SchemaText(string dbPath)
        => string.Join("\n", Query(dbPath, "SELECT type || ' ' || name || ' ' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name"));

    private static bool IsOld(string dbPath) => !HasColumn(dbPath, "releases", "show_in_catalog");

    /// <summary>What the current EnsureSchema leaves behind: every table and every added column.</summary>
    private static void AssertCurrentSchema(string dbPath)
    {
        Assert.True(HasColumn(dbPath, "releases", "show_in_catalog"), "releases.show_in_catalog");
        foreach (var column in new[] { "sort_title", "genre", "subgenre", "players", "release_type", "rating", "notes" })
            Assert.True(HasColumn(dbPath, "release_metadata", column), "release_metadata." + column);
        foreach (var table in new[] { "release_provider_payloads", "release_metadata_field_state", "release_metadata_proposals",
                                      "release_media_curation", "release_extra_notes" })
            Assert.True(HasTable(dbPath, table), table);
        Assert.DoesNotContain("physical-media", Query(dbPath, "SELECT media_type FROM release_media_curation"));
    }

    private static string Sha256(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    // ── The five readers, exactly as the application calls them ──────────────────────────────────────────────

    /// <summary>MainWindow.InitDashboard's background roll-up: every leaf, <c>new DatLineStore(dbPath).GetAllStatusCounts()</c>.</summary>
    private static string DashboardRollUp(Fixture f)
    {
        int missing = 0, pending = 0, outdated = 0, present = 0;
        foreach (var dbPath in f.Catalog.LoadDatLines().Where(dl => dl.DataStorePath.Length > 0)
                                 .Select(dl => Path.Combine(f.DataDir, dl.DataStorePath)))
        {
            if (!File.Exists(dbPath)) continue;
            var (m, pd, o, pr, _, _) = new DatLineStore(dbPath).GetAllStatusCounts();
            missing += m; pending += pd; outdated += o; present += pr;
        }
        return $"missing={missing} pending={pending} outdated={outdated} present={present}";
    }

    /// <summary>The Group coverage worker: <see cref="GroupCoverageLoader.Compute(IReadOnlyList{ValueTuple{string, string, int}}, string, CatalogService, Action{int}?, CancellationToken)"/>.</summary>
    private static string GroupCoverage(Fixture f)
    {
        var leaves = f.Catalog.GetLeavesForGroup(Group)
                      .Select(l => (l.DatLine.Id, l.DatLine.DataStorePath, l.DatLine.ReleaseCount)).ToList();
        return GroupCoverageLoader.Compute(leaves, f.DataDir, f.Catalog).ToString();
    }

    /// <summary>Systems initialization: the complete refresh behind the Systems view.</summary>
    private static string SystemsInit(Fixture f)
    {
        var view = SystemsViewData.Load(f.Catalog, f.DataDir, f.Dir, new GroupCoverageQueue());
        return string.Join(" ; ", view.Platforms.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p =>
                   $"{p.Id}: lines={p.DatLines} titles={p.TotalTitles} present={p.Present} outdated={p.Outdated} missing={p.Missing} lost={p.Lost} unwanted={p.Unwanted} pending={p.CoveragePending}"))
             + $" ; groupCards={view.GroupCards.Count}";
    }

    /// <summary>Pending on-demand loading.</summary>
    private static string PendingScan(Fixture f)
        => string.Join(" ; ", PendingItemsLoader.Load(f.Catalog, f.DataDir, dl => dl.Id).Select(p =>
               $"{p.ReconId}|{p.ReconStatus}|{p.DatLineId}|{p.PlatformName}|{p.NewRelease?.Id}/{p.NewRelease?.Status}/{p.NewRelease?.ShowInCatalog}" +
               $"|{p.OutdatedRelease?.Id}/{p.OutdatedRelease?.Status}|files={string.Join(",", p.NewRomFiles.Select(x => x.RomName))}|{p.CreatedAtUtc:o}"));

    /// <summary>Library loading: the full dataset build.</summary>
    private static string LibraryBuild(Fixture f)
    {
        var datasets = LibraryDatasetBuilder.BuildAll(f.Catalog.LoadPlatforms(), f.Catalog.LoadDatLines(), f.DataDir, dl => dl.Id);
        return string.Join(" ; ", datasets.Select(d => $"[{d.Platform}/{d.DatLine}] " + string.Join(",", d.Entries.Select(e =>
            $"{e.ReleaseId}:{e.Status}:{e.Tier}:{(e.ShowInCatalog ? "shown" : "hidden")}:{e.Metadata?.Title}:{e.Metadata?.SortTitle}:{e.Metadata?.Genre}"))));
    }

    private static readonly (string Name, Func<Fixture, string> Run, string[] LeavesItOpens)[] Readers =
    [
        ("Dashboard background roll-up", DashboardRollUp, AllLeaves),
        ("Group coverage",               GroupCoverage,   GroupLeaves),
        ("Systems initialization",       SystemsInit,     [Single]),
        ("Pending on-demand loading",    PendingScan,     AllLeaves),
        ("Library loading",              LibraryBuild,    AllLeaves),
    ];

    public static TheoryData<string, int> EveryReaderOnEveryGeneration()
    {
        var data = new TheoryData<string, int>();
        foreach (var g in new[] { "v3.2", "v7" })
            for (int i = 0; i < 5; i++) data.Add(g, i);
        return data;
    }

    // ── 0. The fixtures really are older schemas, and the writable open really migrates them ─────────────────

    [Theory]
    [MemberData(nameof(Generations))]
    public void Fixture_LeavesReallyPredateTheCurrentSchema(string generation)
    {
        var f = Build(generation);
        foreach (var leaf in AllLeaves)
        {
            var db = f.DbPath(leaf);
            Assert.False(HasColumn(db, "releases", "show_in_catalog"));
            Assert.Equal(generation == "v7", HasColumn(db, "release_metadata", "sort_title"));
            Assert.Equal(generation == "v7", HasTable(db, "release_media_curation"));
            Assert.Equal(generation == "v7", HasTable(db, "release_extra_notes"));
            Assert.Equal(generation == "v7", HasTable(db, "release_provider_payloads"));
            Assert.Equal(["8"], Query(db, "SELECT COUNT(*) FROM releases"));
        }
    }

    [Theory]
    [MemberData(nameof(Generations))]
    public void WritableOpen_AloneBringsAnOlderLeafFullyUpToDate_KeepingItsData(string generation)
    {
        var f  = Build(generation);
        var db = f.DbPath(Single);
        var releasesBefore = Query(db, "SELECT id || '|' || name || '|' || status FROM releases ORDER BY id");

        _ = new DatLineStore(db);   // the constructor is the whole migration

        AssertCurrentSchema(db);
        Assert.Equal(releasesBefore, Query(db, "SELECT id || '|' || name || '|' || status FROM releases ORDER BY id"));
        Assert.Equal(["1"], Query(db, "SELECT DISTINCT show_in_catalog FROM releases"));   // every existing release visible
        Assert.Equal(["Title One"], Query(db, "SELECT title FROM release_metadata"));
        Assert.Equal([""], Query(db, "SELECT sort_title FROM release_metadata"));
        if (generation == "v7")
            Assert.Equal(["physical"], Query(db, "SELECT media_type FROM release_media_curation"));

        // …and it is idempotent: a second open changes nothing.
        var schema = SchemaText(db);
        _ = new DatLineStore(db);
        Assert.Equal(schema, SchemaText(db));
    }

    // ── 1. Each of the five readers ALONE, first, on never-migrated leaves ───────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryReaderOnEveryGeneration))]
    public void Reader_RunFirstOnNeverMigratedLeaves_IsCorrect_AndMigratesWhatItOpens(string generation, int reader)
    {
        var (name, run, opens) = Readers[reader];
        var old      = Build(generation);
        var migrated = Build(generation, migrateFirst: true);
        Assert.All(AllLeaves, leaf => Assert.True(IsOld(old.DbPath(leaf)), $"{leaf} must start un-migrated"));

        var expected = run(migrated);
        var actual   = run(old);     // nothing else has ever opened these leaves — no Dashboard, no other reader

        Assert.False(string.IsNullOrWhiteSpace(expected), name);
        Assert.Equal(expected, actual);
        foreach (var leaf in opens) AssertCurrentSchema(old.DbPath(leaf));
    }

    [Theory]
    [MemberData(nameof(Generations))]
    public void Readers_ReportTheSeededData(string generation)
    {
        // Absolute values, so "equal to the migrated twin" cannot hide two equally wrong answers.
        var f = Build(generation);
        Assert.Equal("missing=6 pending=3 outdated=3 present=6", DashboardRollUp(Build(generation)));
        Assert.Equal(new GroupStorageFacts(Present: 4, Unwanted: 2, OwnedPresentDerived: 4, Unassigned: 2).ToString(), GroupCoverage(Build(generation)));
        Assert.Contains("psx: lines=1 titles=8 present=2 outdated=1 missing=3 lost=1 unwanted=1 pending=False", SystemsInit(Build(generation)));
        Assert.Equal(3, PendingItemsLoader.Load(f.Catalog, f.DataDir, dl => dl.Id).Count);
        var library = LibraryBuild(Build(generation));
        Assert.Contains($"{Single}-r1:Present:A:shown:Title One::", library);
        Assert.DoesNotContain(":hidden:", library);
    }

    // ── 2. No reader depends on another having run first — in particular not on the Dashboard ────────────────

    public static TheoryData<string, string> Orders()
    {
        var data = new TheoryData<string, string>();
        string[] orders =
        [
            "01234", "12340", "23401", "34012", "40123",   // each reader first, the Dashboard (0) in every position
            "43210", "21043",
            "1234",  "4321",                               // the Dashboard never runs at all
        ];
        foreach (var g in new[] { "v3.2", "v7" })
            foreach (var o in orders) data.Add(g, o);
        return data;
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void Readers_InAnyOrder_WithOrWithoutTheDashboard_GiveTheSameResults(string generation, string order)
    {
        var migrated = Build(generation, migrateFirst: true);
        var expected = Readers.Select(r => r.Run(migrated)).ToArray();

        var old = Build(generation);
        foreach (var index in order.Select(ch => ch - '0'))
            Assert.Equal(expected[index], Readers[index].Run(old));
    }

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
    }

    [Theory]   // the property everything above rests on: these readers open leaves through the MIGRATING constructor
    [InlineData("MainWindow.axaml.cs",              "new DatLineStore(dbPath).GetAllStatusCounts()")]   // Dashboard roll-up
    [InlineData("Systems/GroupCoverageLoader.cs",   "var store = new DatLineStore(abs);")]
    [InlineData("Systems/SystemsViewData.cs",       "var store = new DatLineStore(absPath);")]
    [InlineData("Pending/PendingItemsLoader.cs",    "var store = new DatLineStore(absPath);")]
    [InlineData("Library/LibraryDatasetBuilder.cs", "var store        = new DatLineStore(absPath);")]
    public void TheFiveReaders_OpenLeavesThroughTheMigratingConstructor(string file, string open)
    {
        var source = RepoFile(file);
        Assert.Contains(open, source);
        if (file != "MainWindow.axaml.cs") Assert.DoesNotContain("OpenReadOnly", source);
    }

    [Fact]
    public void WritableConstructor_AlwaysRunsTheMigration_BeforeAnyQueryCanRun()
    {
        var store = RepoFile("DataLayer/DatLineStore.cs");
        int ctor  = store.IndexOf("    public DatLineStore(string dbPath)\n", StringComparison.Ordinal);
        int end   = store.IndexOf("    }\n", ctor, StringComparison.Ordinal);
        Assert.True(ctor >= 0 && end > ctor);
        Assert.Contains("        EnsureSchema();\n", store[ctor..end]);
        // the read-only constructor never does — which is why the read-only loaders must tolerate an older leaf
        int ro    = store.IndexOf("    private DatLineStore(string dbPath, bool readOnly)\n", StringComparison.Ordinal);
        int roEnd = store.IndexOf("    }\n", ro, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureSchema();", store[ro..roEnd]);
    }

    // ── 3. Several threads reaching the same never-migrated leaf at once (Dashboard + coverage + UI thread) ──

    [Theory]
    [MemberData(nameof(Generations))]
    public void ConcurrentFirstOpens_OfTheSameOlderLeaf_AllSucceed(string generation)
    {
        const int Rounds = 6, Threads = 8;
        for (int round = 0; round < Rounds; round++)
        {
            var f  = Build(generation);
            var db = f.DbPath(LeafA);
            using var start = new Barrier(Threads);
            var results = new string[Threads];
            var errors  = new List<Exception>();

            var threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
            {
                try
                {
                    start.SignalAndWait(TimeSpan.FromSeconds(30));
                    var store = new DatLineStore(db);                      // each thread migrates-or-finds-migrated
                    results[i] = (i % 4) switch
                    {
                        0 => "counts " + store.GetAllStatusCounts(),                                             // Dashboard
                        1 => "releases " + string.Join(",", store.LoadReleases().Select(r => $"{r.Id}:{r.Status}:{r.ShowInCatalog}")),   // coverage / Library
                        2 => "pending " + store.LoadPendingReconciliations("pending").Count,                    // Pending
                        _ => "metadata " + string.Join(",", store.LoadReleaseMetadata().Select(m => $"{m.Key}:{m.Value.Title}:{m.Value.SortTitle}")),
                    };
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(120)), "a thread did not finish"));

            Assert.Empty(errors);
            AssertCurrentSchema(db);
            Assert.Single(Query(db, "SELECT name FROM pragma_table_info('releases') WHERE name = 'show_in_catalog'"));
            for (int i = 0; i < Threads; i++) Assert.Equal(results[i % 4], results[i]);   // same reader → same answer on every thread
            Assert.Equal("counts (2, 1, 1, 2, 1, 1)", results[0]);
            Assert.Equal("pending 1", results[2]);
            Assert.Equal($"metadata {LeafA}-r1:Title One:", results[3]);
            Assert.Equal(8, results[1].Split(',').Length);
            Assert.DoesNotContain(":False", results[1]);
        }
    }

    // ── 4. The strictly read-only readers (Group planning): they never migrate, so they must read an older leaf ──

    private static string Describe(IEnumerable<ReleaseRecord> releases)
        => string.Join(" ; ", releases.Select(r =>
               $"{r.Id}|{r.DatLineId}|{r.Name}|{r.Status}|{r.Tier}|{r.Region}|{r.Languages}|{r.Format}|{r.Size}|{r.ReleaseContentKey}|{r.IntroducedAtUtc:o}|{r.ContentCategoryId}|{(r.ShowInCatalog ? "shown" : "hidden")}"));

    [Theory]
    [MemberData(nameof(Generations))]
    public void ReadOnly_LoadReleases_ReadsANeverMigratedLeaf_ExactlyAsItsMigrationWould(string generation)
    {
        var old      = Build(generation);
        var migrated = Build(generation, migrateFirst: true);
        var db       = old.DbPath(LeafA);
        var schema   = SchemaText(db);
        var bytes    = Sha256(db);

        var readOnly = DatLineStore.OpenReadOnly(db);
        var releases = readOnly.LoadReleases();
        var byLine   = readOnly.LoadReleasesByDatLine(LeafA);

        // A leaf that predates show_in_catalog has no hidden release: the migration itself records DEFAULT 1.
        Assert.Equal(8, releases.Count);
        Assert.All(releases, r => Assert.True(r.ShowInCatalog));
        Assert.Equal(Describe(new DatLineStore(migrated.DbPath(LeafA)).LoadReleases()), Describe(releases));
        Assert.Equal(Describe(releases), Describe(byLine));

        // …and the read-only open stayed read-only: nothing migrated, not one byte of the database changed.
        Assert.Equal(schema, SchemaText(db));
        Assert.True(IsOld(db));
        Assert.Equal(bytes, Sha256(db));
    }

    [Theory]
    [MemberData(nameof(Generations))]
    public void ReadOnly_EveryMethodTheGroupPlannersUse_WorksOnANeverMigratedLeaf(string generation)
    {
        var old      = Build(generation);
        var migrated = Build(generation, migrateFirst: true);
        var db       = old.DbPath(LeafA);
        var schema   = SchemaText(db);
        string[] daIds = [$"{LeafA}-da1", $"{LeafA}-da2"];
        var assigned = new HashSet<string>(StringComparer.Ordinal) { $"{LeafA}-da1" };

        string Snapshot(DatLineStore s, string appRoot) => string.Join("\n",
            "releases: "  + Describe(s.LoadReleases()),
            "files: "     + string.Join(",", s.LoadAllReleaseFiles().OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={string.Join("+", k.Value.Select(x => x.RomName))}")),
            "planning: "  + string.Join(",", s.GetPlanningCandidates(appRoot, assigned).Select(c => c.ToString())),
            "fillback: "  + string.Join(",", s.GetFillbackCandidateInfos(daIds).Select(c => c.ToString())),
            "build: "     + string.Join(",", s.GetArtifactBuildInfos(daIds).Select(c => c.ToString())),
            "wanted: "    + string.Join(",", s.GetAllWantedArtifactInfos().Select(c => c.ToString())),
            "unwanted: "  + s.GetUnwantedArtifactCount(),
            "counts: "    + s.GetAllStatusCounts(),
            "pending: "   + s.LoadPendingReconciliations("pending").Count);

        var expected = Snapshot(new DatLineStore(migrated.DbPath(LeafA)), migrated.Dir).Replace(migrated.Dir, "<root>");
        var actual   = Snapshot(DatLineStore.OpenReadOnly(db), old.Dir).Replace(old.Dir, "<root>");

        Assert.Equal(expected, actual);
        Assert.Equal(schema, SchemaText(db));   // still never migrated
    }

    [Theory]
    [MemberData(nameof(Generations))]
    public void GroupIngestionPlanning_OnNeverMigratedLeaves_IsNotBlocked_AndMigratesNothing(string generation)
    {
        var old      = Build(generation);
        var migrated = Build(generation, migrateFirst: true);
        var schemas  = GroupLeaves.ToDictionary(l => l, l => SchemaText(old.DbPath(l)));

        GroupIngestionPlan Plan(Fixture f)
        {
            var incoming = Path.Combine(f.Dir, "incoming-empty");
            Directory.CreateDirectory(incoming);
            return GroupIngestionPlanner.Plan(f.Catalog, f.DataDir, f.Dir, Group, "Commodore 64 TOSEC", "c64",
                f.Catalog.GetLeavesForGroup(Group), incoming, null);
        }
        static string Describe(GroupIngestionPlan p) => string.Join("\n",
            "readiness: "   + p.Readiness,
            "diagnostics: " + string.Join(" | ", p.Diagnostics.Select(d => $"{d.DatLineId}: {d.Reason}")),
            "leaves: "      + string.Join(" | ", p.PreparedLeaves.Select(l => $"{l.DatLineId} targets={l.RequiredTargetCount} resumable={l.ResumableReleaseCount}")),
            "targets: "     + p.RequiredTargets.Length);

        var plan = Plan(old);

        Assert.DoesNotContain(plan.Diagnostics, d => d.Reason.Contains("unreadable", StringComparison.OrdinalIgnoreCase));
        Assert.All(plan.PreparedLeaves, l => Assert.True(l.RequiredTargetCount > 0, $"{l.DatLineId} must be planned"));
        Assert.Equal(Describe(Plan(migrated)), Describe(plan));
        foreach (var leaf in GroupLeaves) Assert.Equal(schemas[leaf], SchemaText(old.DbPath(leaf)));   // planning repaired nothing
    }

    [Theory]
    [MemberData(nameof(Generations))]
    public void GroupArchiveEntryScan_OnNeverMigratedLeaves_ScansEveryLeaf(string generation)
    {
        var old      = Build(generation);
        var migrated = Build(generation, migrateFirst: true);
        var leaves   = old.Catalog.GetLeavesForGroup(Group).Select(l => l.DatLine).ToList();

        var summary  = GroupArchiveEntryScanner.Scan(leaves, old.DataDir);
        var expected = GroupArchiveEntryScanner.Scan(leaves, migrated.DataDir);

        Assert.Empty(summary.UnscannedLeafIds);     // before: both leaves were reported unscanned
        Assert.Empty(expected.UnscannedLeafIds);
        Assert.Equal((expected.EntryCount, expected.ReleaseCount, expected.LeafCount), (summary.EntryCount, summary.ReleaseCount, summary.LeafCount));
        Assert.All(GroupLeaves, leaf => Assert.True(IsOld(old.DbPath(leaf))));   // the scan migrated nothing
    }

    [Fact]
    public void ReadOnly_LoadReleases_OnACurrentLeaf_StillReportsHiddenReleases()
    {
        // The older-schema tolerance must not turn a CURRENT leaf's hidden releases visible.
        var f  = Build("v7", migrateFirst: true);
        var db = f.DbPath(Single);
        new DatLineStore(db).SetShowInCatalog($"{Single}-r3", false);

        var releases = DatLineStore.OpenReadOnly(db).LoadReleases();
        var byLine   = DatLineStore.OpenReadOnly(db).LoadReleasesByDatLine(Single);

        Assert.False(releases.Single(r => r.Id == $"{Single}-r3").ShowInCatalog);
        Assert.Equal(7, releases.Count(r => r.ShowInCatalog));
        Assert.Equal(Describe(new DatLineStore(db).LoadReleases()), Describe(releases));
        Assert.Equal(Describe(releases), Describe(byLine));
    }

    [Fact]
    public void ReadOnly_Open_OfADatabaseWithoutTheLeafSchema_StillFails_AndIsNeverRepaired()
    {
        // Tolerating an OLDER leaf is not tolerating a database that is not a leaf: that stays an error for the caller.
        var dir = Path.Combine(_root, "not-a-leaf");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "x.db");
        Exec(db, "CREATE TABLE dummy(x INTEGER)");

        Assert.ThrowsAny<SqliteException>(() => DatLineStore.OpenReadOnly(db).LoadReleases());
        Assert.False(HasTable(db, "releases"));
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={db};Mode=ReadOnly"));
    }
}

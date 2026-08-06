using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Tests for <see cref="LeafDatDatabaseBuilder"/>: the catalog-free leaf-DB builder used by Single-DAT
/// import today and by the future Group executor. Covers the pure <see cref="LeafDatDatabaseBuilder.Prepare"/>
/// mapping phase (no file, no catalog), <see cref="LeafDatDatabaseBuilder.Build"/> persistence, data
/// fidelity, verification counts, publishability (renamable, no WAL dependency), and cancellation.
/// </summary>
public sealed class LeafDatDatabaseBuilderTests : IDisposable
{
    private readonly string _dir;

    public LeafDatDatabaseBuilderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkLeafBuild_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DatParser.ParsedRom Rom(string name, string size, string crc, string md5, string sha1)
        => new() { Name = name, Size = size, Crc = crc, Md5 = md5, Sha1 = sha1 };

    private static DatParser.ParsedGame Game(string name, params DatParser.ParsedRom[] roms)
        => new() { Name = name, Region = "Europe", Languages = "en", ContentKey = "sha1:" + name, Roms = roms.ToList() };

    private string Tmp(string name) => System.IO.Path.Combine(_dir, name);

    private int DbFileCount() => Directory.GetFiles(_dir, "*.db", SearchOption.AllDirectories).Length;

    private static LeafDatBuildResult Build(string dbPath, string datLineId, IReadOnlyList<DatParser.ParsedGame> games,
        CancellationToken ct = default)
        => LeafDatDatabaseBuilder.Build(dbPath, datLineId, games, progress: null, ct);

    // ── Prepare: pure mapping, no file, no catalog ──────────────────────────────

    [Fact]  // (1)(2) Prepare creates no file and needs no catalog — it only returns mapped data
    public void Prepare_CreatesNoFile_AndNoCatalog()
    {
        var prepared = LeafDatDatabaseBuilder.Prepare(
            "c64-tosec-a",
            new[] { Game("G1", Rom("a", "1", "c", "m", "s")), Game("G2") },
            CancellationToken.None);

        Assert.Equal(0, DbFileCount());          // no database created by Prepare
        Assert.Equal(2, prepared.ReleaseCount);
        Assert.Equal(1, prepared.ReleaseFileCount);
        Assert.Equal("c64-tosec-a", prepared.DatLineId);
        Assert.All(prepared.Releases, r => Assert.Equal("missing", r.Status));
    }

    [Fact]  // (3) Prepare preserves every ROM field and release metadata
    public void Prepare_PreservesAllRomData()
    {
        var prepared = LeafDatDatabaseBuilder.Prepare("c64-tosec-c",
            new[] { Game("Hashy", Rom("rom.bin", "12345", "DEADBEEF",
                "0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef01234567")) },
            CancellationToken.None);

        var release = Assert.Single(prepared.Releases);
        Assert.Equal("Hashy", release.Name);
        Assert.Equal("Europe", release.Region);
        Assert.Equal("en", release.Languages);
        Assert.Equal("sha1:Hashy", release.ReleaseContentKey);

        var set  = Assert.Single(prepared.Files);
        Assert.Equal(release.Id, set.ReleaseId);          // files aligned to their release id
        var file = Assert.Single(set.Files);
        Assert.Equal("rom.bin", file.RomName);
        Assert.Equal("12345", file.Size);
        Assert.Equal("DEADBEEF", file.Crc);
        Assert.Equal("0123456789abcdef0123456789abcdef", file.Md5);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", file.Sha1);
    }

    [Fact]  // (4) Prepare cancelled → throws and creates no file
    public void Prepare_Cancelled_ThrowsAndCreatesNoFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => LeafDatDatabaseBuilder.Prepare("c64-tosec-x",
                new[] { Game("G", Rom("g", "1", "c", "m", "s")) }, cts.Token));
        Assert.Equal(0, DbFileCount());
    }

    // ── Build from prepared data ────────────────────────────────────────────────

    [Fact]  // (5) Build(prepared) creates the correct database
    public void BuildFromPrepared_CreatesCorrectDatabase()
    {
        var prepared = LeafDatDatabaseBuilder.Prepare("c64-tosec-d",
            new[] { Game("A", Rom("a", "1", "c", "m", "s")), Game("B", Rom("b", "2", "c", "m", "s")) },
            CancellationToken.None);

        var db  = Tmp("prepared.db");
        var res = LeafDatDatabaseBuilder.Build(db, prepared, progress: null, CancellationToken.None);

        Assert.Equal(2, res.ReleaseCount);
        Assert.Equal(2, res.ReleaseFileCount);
        Assert.Equal(2, res.VerifiedReleaseCount);
        Assert.Equal(2, res.VerifiedReleaseFileCount);
        Assert.Equal(2, new DatLineStore(db).LoadReleases().Count);
    }

    [Fact]  // (6) convenience Build == Prepare + Build (same counts and data)
    public void ConvenienceBuild_EqualsPrepareThenBuild()
    {
        var games = new[]
        {
            Game("G1", Rom("g1", "1", "c1", "m1", "s1")),
            Game("G2", Rom("g2a", "2", "c2", "m2", "s2"), Rom("g2b", "3", "c3", "m3", "s3")),
        };

        var conv = Build(Tmp("conv.db"), "c64-tosec-e", games);

        var prepared = LeafDatDatabaseBuilder.Prepare("c64-tosec-e", games, CancellationToken.None);
        var split    = LeafDatDatabaseBuilder.Build(Tmp("split.db"), prepared, progress: null, CancellationToken.None);

        Assert.Equal(conv.ReleaseCount,     split.ReleaseCount);
        Assert.Equal(conv.ReleaseFileCount, split.ReleaseFileCount);

        // Both databases carry the same release names and file hashes (ids are per-build GUIDs).
        static (List<string> Names, List<string> Sha1s) Read(string db)
        {
            var s = new DatLineStore(db);
            var names = s.LoadReleases().Select(r => r.Name).OrderBy(x => x).ToList();
            var sha1s = s.LoadAllReleaseFiles().Values.SelectMany(v => v).Select(f => f.Sha1).OrderBy(x => x).ToList();
            return (names, sha1s);
        }
        var a = Read(Tmp("conv.db"));
        var b = Read(Tmp("split.db"));
        Assert.Equal(a.Names, b.Names);
        Assert.Equal(a.Sha1s, b.Sha1s);
    }

    // ── Data fidelity / counts (convenience overload) ───────────────────────────

    [Fact]
    public void OneReleaseOneFile_BuildsAndVerifies()
    {
        var db = Tmp("one.db");
        var res = Build(db, "c64-tosec-a", new[] { Game("Game A", Rom("a.d64", "170", "aaaa", "md5a", "sha1a")) });

        Assert.Equal(1, res.ReleaseCount);
        Assert.Equal(1, res.ReleaseFileCount);
        Assert.Equal(1, res.VerifiedReleaseCount);
        Assert.Equal(1, res.VerifiedReleaseFileCount);
        Assert.True(File.Exists(db));
    }

    [Fact]
    public void ManyReleasesAndFiles_CountsMatch()
    {
        var games = new[]
        {
            Game("G1", Rom("g1a.d64", "1", "c1", "m1", "s1"), Rom("g1b.d64", "2", "c2", "m2", "s2")),
            Game("G2", Rom("g2a.t64", "3", "c3", "m3", "s3")),
            Game("G3"),   // zero files
        };
        var res = Build(Tmp("many.db"), "c64-tosec-b", games);

        Assert.Equal(3, res.ReleaseCount);
        Assert.Equal(3, res.ReleaseFileCount);
        Assert.Equal(3, res.VerifiedReleaseCount);
        Assert.Equal(3, res.VerifiedReleaseFileCount);
    }

    [Fact]
    public void RomHashesAndSize_ArePreserved()
    {
        var db = Tmp("hashes.db");
        Build(db, "c64-tosec-c", new[]
        {
            Game("Hashy", Rom("rom.bin", "12345", "DEADBEEF", "0123456789abcdef0123456789abcdef",
                                            "0123456789abcdef0123456789abcdef01234567")),
        });

        var store   = new DatLineStore(db);
        var release = store.LoadReleases().Single();
        var file    = store.LoadAllReleaseFiles()[release.Id].Single();
        Assert.Equal("Hashy", release.Name);
        Assert.Equal("missing", release.Status);
        Assert.Equal("rom.bin", file.RomName);
        Assert.Equal("12345", file.Size);
        Assert.Equal("DEADBEEF", file.Crc);
        Assert.Equal("0123456789abcdef0123456789abcdef", file.Md5);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", file.Sha1);
    }

    [Fact]
    public void ZeroReleases_BuildsEmptyDatabase()
    {
        var db = Tmp("empty.db");
        var res = Build(db, "c64-tosec-empty", Array.Empty<DatParser.ParsedGame>());

        Assert.Equal(0, res.ReleaseCount);
        Assert.Equal(0, res.ReleaseFileCount);
        Assert.True(File.Exists(db));
        Assert.Empty(new DatLineStore(db).LoadReleases());
    }

    // ── Reopenable + publishable by rename (WAL) ────────────────────────────────

    [Fact]
    public void AfterBuild_DatabaseIsReopenable()
    {
        var db = Tmp("reopen.db");
        Build(db, "c64-tosec-d", new[] { Game("R", Rom("r.d64", "1", "c", "m", "s")) });
        Assert.Single(new DatLineStore(db).LoadReleases());
    }

    [Fact]  // (9) WAL/rename behaviour unchanged: plain .db rename succeeds and reads back
    public void AfterBuild_DatabaseIsRenamable_WithoutWalSidecar()
    {
        var db = Tmp("pub.db");
        Build(db, "c64-tosec-e2", new[]
        {
            Game("A", Rom("a", "1", "c", "m", "s")),
            Game("B", Rom("b", "2", "c", "m", "s")),
        });

        var final = Tmp("published.db");
        File.Move(db, final);
        Assert.False(File.Exists(db));
        Assert.True(File.Exists(final));
        Assert.Equal(2, new DatLineStore(final).LoadReleases().Count);
    }

    [Fact]
    public void BuildsOnTemporaryExecutionPath_ThenRenames()
    {
        var final = Tmp("c64-tosec-games.db");
        var tmp   = final + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        Build(tmp, "c64-tosec-games", new[] { Game("T", Rom("t", "1", "c", "m", "s")) });
        Assert.True(File.Exists(tmp));

        File.Move(tmp, final);
        Assert.True(File.Exists(final));
        Assert.Single(new DatLineStore(final).LoadReleases());
    }

    // ── Cancellation ────────────────────────────────────────────────────────────

    [Fact]
    public void CancelledBeforeStart_ThrowsAndCreatesNoFile()
    {
        var db = Tmp("cancel-early.db");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Build(db, "c64-tosec-x", new[] { Game("G", Rom("g", "1", "c", "m", "s")) }, cts.Token));
        Assert.False(File.Exists(db));
    }

    [Fact]  // cancel during persistence → partial target created, and deletable (no lingering lock)
    public void CancelledDuringBuild_TargetIsDeletable()
    {
        var prepared = LeafDatDatabaseBuilder.Prepare("c64-tosec-big",
            Enumerable.Range(0, 2000).Select(i => Game($"G{i}", Rom($"r{i}", "1", "c", "m", "s"))).ToArray(),
            CancellationToken.None);

        var db = Tmp("cancel-mid.db");
        using var cts = new CancellationTokenSource();
        // Cancel on the first Build report (SavingReleases), which fires after the DB file is created.
        var progress = new DelegateProgress<LeafDatBuildProgress>(_ => cts.Cancel());

        Assert.Throws<OperationCanceledException>(
            () => LeafDatDatabaseBuilder.Build(db, prepared, progress, cts.Token));

        Assert.True(File.Exists(db));   // partial target created
        File.Delete(db);                // must not throw — connections were released
        Assert.False(File.Exists(db));
    }

    [Fact]  // a failed build does not delete or modify files it did not create
    public void UnwritablePath_Throws_AndLeavesOtherFilesUntouched()
    {
        var blocker = Tmp("blocker");
        File.WriteAllText(blocker, "x");
        var bad = System.IO.Path.Combine(blocker, "child.db");

        Assert.ThrowsAny<Exception>(
            () => Build(bad, "c64-tosec-bad", new[] { Game("G", Rom("g", "1", "c", "m", "s")) }));
        Assert.Equal("x", File.ReadAllText(blocker));
    }

    // ── Minimal IProgress helper ────────────────────────────────────────────────

    private sealed class DelegateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _on;
        public DelegateProgress(Action<T> on) => _on = on;
        public void Report(T value) => _on(value);
    }
}

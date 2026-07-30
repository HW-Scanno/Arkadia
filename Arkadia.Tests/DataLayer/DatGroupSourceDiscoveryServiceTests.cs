using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.DataLayer;

/// <summary>
/// Phase 3A tests for the pure, DB-independent Group DAT source discovery service: recursive
/// traversal, .dat recognition, normalized relative paths, deterministic ordering, partial
/// parse-failure handling, collisions, Unicode, reparse-point safety, cancellation, and
/// DB/runtime independence. No catalog, no fingerprint, no id generation.
/// </summary>
public sealed class DatGroupSourceDiscoveryServiceTests : IDisposable
{
    private readonly string _root;

    public DatGroupSourceDiscoveryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkGDisc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly DatGroupSourceDiscoveryService Svc = new();

    private string WriteValidDat(string relPath, string name = "Test")
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>{name}</name><version>1</version><date>2026-01-01</date><author>A</author></header>
              <game name="Game 1"><rom name="g1.bin" size="1" sha1="da39a3ee5e6b4b0d3255bfef95601890afd80709"/></game>
            </datafile>
            """);
        return full;
    }

    private string WriteMalformedDat(string relPath)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "<datafile><game name=\"broken\"");   // unclosed → XmlException
        return full;
    }

    private void WriteText(string relPath, string content = "x")
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ── 17.1 Root validation ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Discover_NullOrBlankRoot_Throws(string? root)
        => Assert.Throws<ArgumentException>(() => Svc.Discover(root!));

    [Fact]
    public void Discover_MissingRoot_IsBlockingDiagnostic()
    {
        var r = Svc.Discover(Path.Combine(_root, "does-not-exist"));
        Assert.True(r.HasBlockingErrors);
        Assert.False(r.CanProceedToPlanning);
        Assert.Empty(r.Leaves);
        Assert.Contains(r.Diagnostics, d => d.Code == DatGroupDiscoveryDiagnosticCodes.SourceRootMissing);
    }

    [Fact]
    public void Discover_RootIsFile_IsBlockingDiagnostic()
    {
        var file = Path.Combine(_root, "a.dat");
        File.WriteAllText(file, "x");
        var r = Svc.Discover(file);
        Assert.True(r.HasBlockingErrors);
        Assert.Contains(r.Diagnostics, d => d.Code == DatGroupDiscoveryDiagnosticCodes.SourceRootNotDirectory);
    }

    // ── 17.2 Recursive discovery ────────────────────────────────────────────────

    [Fact]
    public void Discover_FindsAllCandidates_WithNormalizedRelativePaths()
    {
        WriteValidDat("Root.dat");
        WriteValidDat("Commodore/C64/Games.dat");
        WriteValidDat("Commodore/C64/Demos.dat");
        WriteValidDat("Commodore/Amiga/CD/Applications.dat");

        var r = Svc.Discover(_root);

        Assert.Equal(4, r.CandidateCount);
        Assert.Equal(4, r.ParsedCount);
        Assert.True(r.CanProceedToPlanning);
        Assert.Equal(
            new[] { "Commodore/Amiga/CD/Applications.dat", "Commodore/C64/Demos.dat", "Commodore/C64/Games.dat", "Root.dat" },
            r.Leaves.Select(l => l.RelativePath).ToArray());   // deterministic Ordinal order
        Assert.All(r.Leaves, l => Assert.DoesNotContain('\\', l.RelativePath));
        Assert.All(r.Leaves, l => Assert.True(l.ParseSucceeded && l.GameCount == 1));
        // absolute path is kept but is not identity / not the ordering key
        Assert.All(r.Leaves, l => Assert.True(Path.IsPathRooted(l.SourcePath)));
    }

    // ── 17.3 Unsupported files ignored ──────────────────────────────────────────

    [Fact]
    public void Discover_IgnoresNonDatFiles_WithoutDiagnosticNoise()
    {
        WriteValidDat("Games.dat");
        WriteText("README.txt");
        WriteText("cover.png");
        WriteText("notes.md");
        WriteText("archive.zip");

        var r = Svc.Discover(_root);

        Assert.Equal(1, r.CandidateCount);
        Assert.Equal("Games.dat", r.Leaves.Single().RelativePath);
        Assert.Empty(r.Diagnostics);   // no per-junk-file noise
    }

    // ── 17.4 Extension case-insensitivity ───────────────────────────────────────

    [Fact]
    public void Discover_RecognizesDatExtensionCaseInsensitively()
    {
        WriteValidDat("A/games.dat");
        WriteValidDat("B/games.DAT");   // distinct directory → distinct relative path

        var r = Svc.Discover(_root);
        Assert.Equal(2, r.CandidateCount);
        Assert.Contains(r.Leaves, l => l.RelativePath == "A/games.dat");
        Assert.Contains(r.Leaves, l => l.RelativePath == "B/games.DAT");
    }

    // ── 17.5 Partial parse failure ──────────────────────────────────────────────

    [Fact]
    public void Discover_MalformedDat_DoesNotAbortScan()
    {
        WriteValidDat("A/Ok1.dat");
        WriteMalformedDat("B/Bad.dat");
        WriteValidDat("C/Ok2.dat");

        var r = Svc.Discover(_root);

        Assert.Equal(3, r.CandidateCount);
        Assert.Equal(2, r.ParsedCount);
        Assert.Equal(1, r.FailedCount);
        Assert.False(r.CanProceedToPlanning);
        var bad = r.Leaves.Single(l => l.RelativePath == "B/Bad.dat");
        Assert.Equal(DiscoveredDatLeafStatus.ParseFailed, bad.Status);
        Assert.NotNull(bad.Diagnostic);
        Assert.Equal(DatGroupDiscoveryDiagnosticCodes.DatParseFailed, bad.Diagnostic!.Code);
        Assert.Equal("B/Bad.dat", bad.Diagnostic.RelativePath);
        Assert.All(r.Leaves.Where(l => l.RelativePath != "B/Bad.dat"), l => Assert.True(l.ParseSucceeded));
    }

    // ── 17.6 Directory with no DAT ──────────────────────────────────────────────

    [Fact]
    public void Discover_EmptyOfDats_YieldsNoCandidatesAndCannotProceed()
    {
        WriteText("Docs/readme.txt");
        var r = Svc.Discover(_root);
        Assert.Equal(0, r.CandidateCount);
        Assert.False(r.CanProceedToPlanning);
        Assert.False(r.HasBlockingErrors);
        Assert.Empty(r.Diagnostics);
    }

    // ── 17.7 Same filename, different directory ─────────────────────────────────

    [Fact]
    public void Discover_SameFilenameDifferentDirectory_AreDistinct()
    {
        WriteValidDat("A/Games.dat");
        WriteValidDat("B/Games.dat");

        var r = Svc.Discover(_root);
        Assert.Equal(2, r.CandidateCount);
        Assert.False(r.HasBlockingErrors);
        Assert.Equal(new[] { "A/Games.dat", "B/Games.dat" }, r.Leaves.Select(l => l.RelativePath).ToArray());
    }

    // ── 17.8 Case-insensitive collision (pure function) ─────────────────────────

    [Fact]
    public void DetectRelativePathCollisions_FlagsCaseVariantsAsBlocking()
    {
        var diags = DatGroupSourceDiscoveryService.DetectRelativePathCollisions(
            new[] { "Systems/C64/Games.dat", "systems/c64/games.dat", "Other/Unique.dat" });

        var collision = Assert.Single(diags);
        Assert.Equal(DatGroupDiscoveryDiagnosticCodes.RelativePathCollision, collision.Code);
        Assert.Equal(DatGroupDiscoveryDiagnosticSeverity.Error, collision.Severity);
    }

    [Fact]
    public void DetectRelativePathCollisions_NoFalsePositiveForDistinctPaths()
    {
        var diags = DatGroupSourceDiscoveryService.DetectRelativePathCollisions(
            new[] { "A/Games.dat", "B/Games.dat" });
        Assert.Empty(diags);
    }

    // ── 17.9 Unicode ────────────────────────────────────────────────────────────

    [Fact]
    public void Discover_PreservesUnicodeRelativePaths()
    {
        WriteValidDat("日本/ゲーム.dat");
        WriteValidDat("Commodore/Démonstrations.dat");

        var r = Svc.Discover(_root);
        Assert.Contains(r.Leaves, l => l.RelativePath == "日本/ゲーム.dat");
        Assert.Contains(r.Leaves, l => l.RelativePath == "Commodore/Démonstrations.dat");
    }

    // ── 17.10 Reparse point / symlink (conditional) ─────────────────────────────

    [Fact]
    public void Discover_DoesNotFollowDirectoryReparsePoints()
    {
        // External directory (outside the scanned root) containing a DAT.
        var external = Path.Combine(Path.GetTempPath(), "ArkGExt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "Outside.dat"), "<datafile><game name=\"x\"/></datafile>");

        var link = Path.Combine(_root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            try { Directory.Delete(external, true); } catch { }
            return;   // no privilege/support to create a link → skip
        }

        try
        {
            WriteValidDat("Real.dat");
            var r = Svc.Discover(_root);

            Assert.DoesNotContain(r.Leaves, l => l.RelativePath.Contains("Outside", StringComparison.Ordinal));
            Assert.Contains(r.Leaves, l => l.RelativePath == "Real.dat");
            Assert.Contains(r.Diagnostics, d => d.Code == DatGroupDiscoveryDiagnosticCodes.ReparsePointSkipped);
        }
        finally
        {
            try { Directory.Delete(external, true); } catch { }
        }
    }

    // ── 17.11 Cancellation ──────────────────────────────────────────────────────

    [Fact]
    public void Discover_PreCancelledToken_Throws()
    {
        WriteValidDat("A/One.dat");
        WriteValidDat("B/Two.dat");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Svc.Discover(_root, cts.Token));
    }

    // ── 17.12 Determinism ───────────────────────────────────────────────────────

    [Fact]
    public void Discover_IsDeterministicAcrossRuns()
    {
        WriteValidDat("Root.dat");
        WriteValidDat("Z/Last.dat");
        WriteValidDat("A/First.dat");
        WriteMalformedDat("M/Bad.dat");

        var r1 = Svc.Discover(_root);
        var r2 = Svc.Discover(_root);

        Assert.Equal(r1.Leaves.Select(l => l.RelativePath), r2.Leaves.Select(l => l.RelativePath));
        Assert.Equal(r1.Leaves.Select(l => l.Status), r2.Leaves.Select(l => l.Status));
        Assert.Equal(r1.Diagnostics.Select(d => d.Code + "|" + d.RelativePath),
                     r2.Diagnostics.Select(d => d.Code + "|" + d.RelativePath));
        Assert.Equal(r1.CandidateCount, r2.CandidateCount);
        Assert.Equal(r1.ParsedCount, r2.ParsedCount);
        Assert.Equal(r1.FailedCount, r2.FailedCount);
    }

    // ── 17.13 No DB / runtime side effects ──────────────────────────────────────

    [Fact]
    public void Discover_DoesNotCreateRuntimeDataOrModifyInput()
    {
        WriteValidDat("Games.dat");
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();

        var r = Svc.Discover(_root);
        Assert.True(r.CanProceedToPlanning);

        var after = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        Assert.Equal(before, after);   // no files/dirs created or removed
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
        Assert.False(File.Exists(Path.Combine(_root, "catalog.db")));
        Assert.False(Directory.Exists(Path.Combine(_root, "systems")));
    }

    // ── Deep parser-result immutability protection ──────────────────────────────

    private string WriteMultiRomDat(string relPath)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Multi</name></header>
              <game name="G1">
                <rom name="a.bin" size="10" crc="aaaa" md5="m1" sha1="s1"/>
                <rom name="b.bin" size="20" crc="bbbb" md5="m2" sha1="s2"/>
              </game>
            </datafile>
            """);
        return full;
    }

    [Fact]
    public void Games_RuntimeTypeIsNotArrayOrList_AndNotCastableToArray()
    {
        WriteMultiRomDat("Multi.dat");
        var leaf = Svc.Discover(_root).Leaves.Single();

        object boxed = leaf.Games;
        Assert.IsType<ImmutableArray<DiscoveredDatGame>>(boxed);   // concrete type is the immutable struct
        Assert.False(boxed is DiscoveredDatGame[]);                // not an array
        Assert.False(boxed is List<DiscoveredDatGame>);            // not a List
        Assert.False(boxed is IList<DiscoveredDatGame> il && !il.IsReadOnly);   // IList view reports read-only
    }

    [Fact]
    public void Games_IListView_RejectsAllMutation()
    {
        WriteMultiRomDat("Multi.dat");
        var leaf = Svc.Discover(_root).Leaves.Single();
        var list = (IList<DiscoveredDatGame>)leaf.Games;   // boxed ImmutableArray
        var stub = new DiscoveredDatGame("x", "", "", "", "", ImmutableArray<DiscoveredDatRom>.Empty);

        Assert.Throws<NotSupportedException>(() => list[0] = stub);   // writable indexer blocked
        Assert.Throws<NotSupportedException>(() => list.Add(stub));
        Assert.Throws<NotSupportedException>(() => list.Remove(list[0]));
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => list.Insert(0, stub));
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void Roms_RuntimeTypeIsNotArrayOrList_AndIListViewRejectsMutation()
    {
        WriteMultiRomDat("Multi.dat");
        var game = Svc.Discover(_root).Leaves.Single().Games.Single();
        Assert.Equal(2, game.Roms.Length);

        object boxed = game.Roms;
        Assert.IsType<ImmutableArray<DiscoveredDatRom>>(boxed);
        Assert.False(boxed is DiscoveredDatRom[]);
        Assert.False(boxed is List<DiscoveredDatRom>);

        var list = (IList<DiscoveredDatRom>)game.Roms;
        var stub = new DiscoveredDatRom("z", "", "", "", "");
        Assert.Throws<NotSupportedException>(() => list[0] = stub);   // element replacement blocked
        Assert.Throws<NotSupportedException>(() => list.Add(stub));
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void PublicElements_AreSnapshotTypes_NotParserTypes()
    {
        WriteMultiRomDat("Multi.dat");
        var game = Svc.Discover(_root).Leaves.Single().Games.Single();

        Assert.IsType<DiscoveredDatGame>(game);
        Assert.All(game.Roms, r => Assert.IsType<DiscoveredDatRom>(r));
        // The public element type is not the parser's mutable model.
        Assert.False(((object)game) is DatParser.ParsedGame);
        Assert.All(game.Roms, r => Assert.False(((object)r) is DatParser.ParsedRom));
    }

    [Fact]
    public void SnapshotGames_PreservesOrderMultiplicityAndValues()
    {
        var parsed = new List<DatParser.ParsedGame>
        {
            new()
            {
                Name = "G1", Region = "USA", Languages = "en", ContentKey = "ck", WorkingState = "working",
                Roms = new List<DatParser.ParsedRom>
                {
                    new() { Name = "a.bin", Size = "10", Crc = "aaaa", Md5 = "m1", Sha1 = "s1" },
                    new() { Name = "b.bin", Size = "20", Crc = "bbbb", Md5 = "m2", Sha1 = "s2" },
                    new() { Name = "a.bin", Size = "10", Crc = "aaaa", Md5 = "m1", Sha1 = "s1" },   // duplicate kept
                },
            },
        };

        var snap = DatGroupSourceDiscoveryService.SnapshotGames(parsed);

        var g = Assert.Single(snap);
        Assert.Equal(("G1", "USA", "en", "ck", "working"), (g.Name, g.Region, g.Languages, g.ContentKey, g.WorkingState));
        Assert.Equal(new[] { "a.bin", "b.bin", "a.bin" }, g.Roms.Select(r => r.Name).ToArray());   // order + multiplicity
        Assert.Equal(new[] { "s1", "s2", "s1" }, g.Roms.Select(r => r.Sha1).ToArray());
        Assert.Equal(("20", "bbbb", "m2"), (g.Roms[1].Size, g.Roms[1].Crc, g.Roms[1].Md5));
    }

    [Fact]
    public void SnapshotGames_DoesNotShareReferencesWithParserResult()
    {
        var roms   = new List<DatParser.ParsedRom> { new() { Name = "a.bin", Sha1 = "s1" } };
        var games  = new List<DatParser.ParsedGame> { new() { Name = "G1", Roms = roms } };

        var snap = DatGroupSourceDiscoveryService.SnapshotGames(games);

        // Mutate the parser's collections AFTER snapshotting.
        roms.Add(new DatParser.ParsedRom { Name = "b.bin" });
        roms.Clear();
        games.Add(new DatParser.ParsedGame { Name = "G2" });

        // Snapshot is unaffected.
        var g = Assert.Single(snap);
        Assert.Equal("G1", g.Name);
        var r = Assert.Single(g.Roms);
        Assert.Equal("a.bin", r.Name);
        Assert.Equal("s1", r.Sha1);
    }

    [Fact]
    public void Discover_GameAndRomSnapshots_AreDeterministic()
    {
        WriteMultiRomDat("Multi.dat");
        var r1 = Svc.Discover(_root).Leaves.Single().Games.Single();
        var r2 = Svc.Discover(_root).Leaves.Single().Games.Single();
        Assert.Equal(r1.Roms.Select(x => x.Name + "|" + x.Sha1), r2.Roms.Select(x => x.Name + "|" + x.Sha1));
    }
}

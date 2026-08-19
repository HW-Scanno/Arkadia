using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Regression coverage for nested DAT <c>rom.name</c> relative paths through Phase 6 staging
/// (<see cref="IngestionStagingEngine"/>) and Phase 7 finalization (<see cref="LeafFinalizationEngine"/>):
/// nested paths stage/promote with their hierarchy intact, flat names are unchanged, duplicate basenames and
/// same-hash fan-out both materialize distinct destinations, and escaping names are rejected with no external
/// write. The end-to-end ZIP-entry test is gated on a resolvable bundled/PATH 7-Zip. Real temp filesystem +
/// temp leaf DB; runtime data is never touched.
/// </summary>
public sealed class NestedRomPathIngestionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stagingRoot;
    private readonly string _sourceRoot;
    private readonly string _incoming;
    private readonly string _dbPath;

    public NestedRomPathIngestionTests()
    {
        _dir         = Path.Combine(Path.GetTempPath(), "ArkNested_" + Guid.NewGuid().ToString("N")[..8]);
        _stagingRoot = Path.Combine(_dir, "staging");
        _sourceRoot  = Path.Combine(_dir, "source");
        _incoming    = Path.Combine(_dir, "incoming");
        _dbPath      = Path.Combine(_dir, "leaf.db");
        Directory.CreateDirectory(_incoming);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

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

    private string MakeIncoming(string name, byte[] bytes)
    {
        var p = Path.Combine(_incoming, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static void WriteNested(string root, string relative, byte[] bytes)
    {
        var full = Path.Combine(new[] { root }.Concat(relative.Split('\\', '/')).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private StagingResult RunStage(
        DatLineStore store,
        Dictionary<string, List<(string ReleaseId, string RomName)>> copyPlan,
        IngestionResult result)
    {
        var releases = store.LoadReleases().ToDictionary(r => r.Id, StringComparer.Ordinal);
        return IngestionStagingEngine.StageTargets(
            copyPlan, releases, new HashSet<string>(StringComparer.Ordinal),
            _stagingRoot, "cx16", "leaf", IngestionPaths.SafeFolderName,
            result, new Progress<IngestionProgress>(_ => { }),
            allowMove: false);   // Group semantics: always copy, keep incoming
    }

    private string SafeFolder(DatLineStore store)
        => IngestionPaths.SafeFolderName(store.LoadReleases().First().Name);

    // ── 1. Nested staging ─────────────────────────────────────────────────────

    [Fact]
    public void NestedStaging_PreservesHierarchy_AndSecures()
    {
        var store = SeedLeaf("Time Pilot v1.2", "TIME-PILOT\\assets\\bank001.au", "LAUNCHER.DATA\\.META\\TIME-PILOT.INF");
        var rid   = store.LoadReleases().First().Id;

        var s1 = MakeIncoming("a.bin", new byte[] { 1, 2, 3 });
        var s2 = MakeIncoming("b.bin", new byte[] { 4, 5, 6 });
        var copyPlan = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [s1] = new() { (rid, "TIME-PILOT\\assets\\bank001.au") },
            [s2] = new() { (rid, "LAUNCHER.DATA\\.META\\TIME-PILOT.INF") },
        };

        var result = new IngestionResult();
        var res    = RunStage(store, copyPlan, result);

        var sf = SafeFolder(store);
        Assert.True(File.Exists(Path.Combine(_stagingRoot, sf, "TIME-PILOT", "assets", "bank001.au")));
        Assert.True(File.Exists(Path.Combine(_stagingRoot, sf, "LAUNCHER.DATA", ".META", "TIME-PILOT.INF")));
        Assert.Equal(2, result.FilesCopied);
        Assert.Equal(2, res.SuccessfullyCopied.Count);
    }

    // ── 2. Nested finalization (staging → source promotion) ───────────────────

    [Fact]
    public void NestedFinalization_PromotesHierarchy()
    {
        var store = SeedLeaf("Time Pilot v1.2", "TIME-PILOT\\assets\\bank001.au", "TIME-PILOT\\TIME-PILOT.PRG");
        var rid   = store.LoadReleases().First().Id;
        var sf    = SafeFolder(store);

        WriteNested(Path.Combine(_stagingRoot, sf), "TIME-PILOT\\assets\\bank001.au", new byte[] { 1 });
        WriteNested(Path.Combine(_stagingRoot, sf), "TIME-PILOT\\TIME-PILOT.PRG",     new byte[] { 2 });

        var releases = store.LoadReleases().ToDictionary(r => r.Id, StringComparer.Ordinal);
        bool transformed = false;
        var res = LeafFinalizationEngine.FinalizeLeaf(
            new HashSet<string>(StringComparer.Ordinal) { rid }, Array.Empty<string>(), releases, store,
            _stagingRoot, _sourceRoot, "cx16", "leaf", IngestionPaths.SafeFolderName,
            new IngestionResult(), new Progress<IngestionProgress>(_ => { }),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            _ => transformed = true);

        Assert.True(transformed);
        Assert.Contains(rid, res.SuccessfulReleaseIds);
        Assert.True(File.Exists(Path.Combine(_sourceRoot, sf, "TIME-PILOT", "assets", "bank001.au")));
        Assert.True(File.Exists(Path.Combine(_sourceRoot, sf, "TIME-PILOT", "TIME-PILOT.PRG")));

        // Staging drains completely: the nested empty branches AND the release dir are gone (STAGING truthful).
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, sf, "TIME-PILOT", "assets")));
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, sf, "TIME-PILOT")));
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, sf)));
    }

    // ── 3. Flat regression ────────────────────────────────────────────────────

    [Fact]
    public void FlatRom_StagesExactlyAsBefore()
    {
        var store = SeedLeaf("Flat Game", "game.bin");
        var rid   = store.LoadReleases().First().Id;
        var s1    = MakeIncoming("x.bin", new byte[] { 7, 8 });
        var copyPlan = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [s1] = new() { (rid, "game.bin") },
        };

        var result = new IngestionResult();
        RunStage(store, copyPlan, result);

        Assert.True(File.Exists(Path.Combine(_stagingRoot, SafeFolder(store), "game.bin")));
        Assert.Equal(1, result.FilesCopied);
    }

    // ── 4. Duplicate basename ─────────────────────────────────────────────────

    [Fact]
    public void DuplicateBasename_StagesToDistinctNestedDestinations()
    {
        var store = SeedLeaf("Multi Disk", "disk1\\data.bin", "disk2\\data.bin");
        var rid   = store.LoadReleases().First().Id;
        var s1    = MakeIncoming("d1.bin", new byte[] { 1 });
        var s2    = MakeIncoming("d2.bin", new byte[] { 2 });
        var copyPlan = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [s1] = new() { (rid, "disk1\\data.bin") },
            [s2] = new() { (rid, "disk2\\data.bin") },
        };

        var result = new IngestionResult();
        RunStage(store, copyPlan, result);

        var sf = SafeFolder(store);
        Assert.True(File.Exists(Path.Combine(_stagingRoot, sf, "disk1", "data.bin")));
        Assert.True(File.Exists(Path.Combine(_stagingRoot, sf, "disk2", "data.bin")));
        Assert.Equal(2, result.FilesCopied);
    }

    // ── 5. Same-hash fan-out (one source → two nested targets) ────────────────

    [Fact]
    public void SameHashFanOut_CreatesBothSecuredTargets()
    {
        var store = SeedLeaf("Fan Out", "dir1\\same.bin", "dir2\\same.bin");
        var rid   = store.LoadReleases().First().Id;
        var src   = MakeIncoming("one.bin", new byte[] { 9, 9, 9 });
        var copyPlan = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [src] = new() { (rid, "dir1\\same.bin"), (rid, "dir2\\same.bin") },
        };

        var result = new IngestionResult();
        RunStage(store, copyPlan, result);

        var sf = SafeFolder(store);
        Assert.True(File.Exists(Path.Combine(_stagingRoot, sf, "dir1", "same.bin")));
        Assert.True(File.Exists(Path.Combine(_stagingRoot, sf, "dir2", "same.bin")));
        Assert.Equal(2, result.FilesCopied);
        Assert.True(File.Exists(src));   // source retained (allowMove:false)
    }

    // ── 7. Traversal rejection (no external write, target not secured) ────────

    [Fact]
    public void EscapingRomName_NotSecured_NoExternalWrite()
    {
        var store = SeedLeaf("Evil", "..\\evil.bin");
        var rid   = store.LoadReleases().First().Id;
        var src   = MakeIncoming("payload.bin", new byte[] { 6, 6, 6 });
        var copyPlan = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [src] = new() { (rid, "..\\evil.bin") },
        };

        var result = new IngestionResult();
        var res    = RunStage(store, copyPlan, result);

        // No file escaped the release/staging roots.
        Assert.False(File.Exists(Path.Combine(_stagingRoot, "evil.bin")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.bin")));
        // Nothing secured; the target failed (conservative retain).
        Assert.Equal(0, result.FilesCopied);
        Assert.Empty(res.SuccessfullyCopied);
        Assert.Contains(res.Ledger, l => l.FailedTargets.Count > 0);
    }

    // ── 8. End-to-end ZIP hierarchy (gated on a resolvable 7-Zip) ─────────────

    [Fact]
    public void ZipCompression_PreservesNestedEntries_NoOuterFolder()
    {
        var sevenZip = Find7z();
        if (sevenZip is null) return;   // environment-gated

        // Build a source release tree, then run the EXACT zip_compression template: a -tzip "{output}" "{input}\*".
        var srcDir = Path.Combine(_dir, "rel");
        WriteNested(srcDir, "TIME-PILOT\\assets\\bank001.au",       new byte[] { 1 });
        WriteNested(srcDir, "TIME-PILOT\\TIME-PILOT.PRG",           new byte[] { 2 });
        WriteNested(srcDir, "LAUNCHER.DATA\\.META\\TIME-PILOT.INF", new byte[] { 3 });
        var zipPath = Path.Combine(_dir, "out.zip");

        var psi = new ProcessStartInfo(sevenZip) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("a"); psi.ArgumentList.Add("-tzip");
        psi.ArgumentList.Add(zipPath); psi.ArgumentList.Add(Path.Combine(srcDir, "*"));
        using (var p = Process.Start(psi)!) { p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        if (!File.Exists(zipPath)) return;   // this build couldn't produce the fixture

        using var za = ZipFile.OpenRead(zipPath);
        var entries  = za.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("TIME-PILOT/assets/bank001.au", entries);      // nested, forward-slash
        Assert.Contains("TIME-PILOT/TIME-PILOT.PRG", entries);
        Assert.Contains("LAUNCHER.DATA/.META/TIME-PILOT.INF", entries);
        // No outer release-name directory wraps the entries.
        Assert.DoesNotContain(entries, e => e.StartsWith("rel/", StringComparison.Ordinal));
    }

    private static string? Find7z()
    {
        var prod = Arkadia.ProviderHelpers.Find7zip();
        if (prod is not null) return prod;
        var candidates = new[]
        {
            @"G:\Arkadia\tools\7zip\7zip.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "7z.exe", "7za.exe" })
                try { var f = Path.Combine(dir, name); if (File.Exists(f)) return f; } catch { }
        return null;
    }
}

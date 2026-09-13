using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Arkadia.Data;
using Arkadia.GroupDats;
using Xunit;

namespace Arkadia.Tests.GroupDats;

/// <summary>
/// The read-only Group archive-entry scan behind the Configure Group notice: merges real leaf DBs, tolerates
/// missing/unreadable leaves (reported, never aborting), never creates or modifies leaf databases, and honors
/// cancellation. Real temp leaf DBs built through the production builder.
/// </summary>
public sealed class GroupArchiveEntryScannerTests : IDisposable
{
    private readonly string _dataDir;

    public GroupArchiveEntryScannerTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "ArkArchScan_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private static string Rel(string id) => $"systems/trs80/{id}.db";

    private DatLineRecord BuildLeaf(string id, params (string Release, string[] Roms)[] releases)
    {
        var games = releases.Select(r => new DatParser.ParsedGame
        {
            Name = r.Release,
            Roms = r.Roms.Select(x => new DatParser.ParsedRom { Name = x, Size = "10", Sha1 = new string('a', 40) }).ToList(),
        }).ToList();
        var abs = Path.Combine(_dataDir, Rel(id));
        LeafDatDatabaseBuilder.Build(abs, id, games, null, CancellationToken.None);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return new DatLineRecord { Id = id, HardwareFamilyId = "trs80", DataStorePath = Rel(id) };
    }

    private static string Sha1File(string path) => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Scan_MergesLeaves_CountsAndLeafQualifiedExamples()
    {
        var a = BuildLeaf("trs80-tosec-a", ("Game A1", new[] { "DISK1.ZIP", "RUN.CMD" }), ("Game A2", new[] { "DATA.7Z" }));
        var b = BuildLeaf("trs80-tosec-b", ("Game B1", new[] { "X.RAR", "Y.ZIP" }));
        var c = BuildLeaf("trs80-tosec-c", ("Plain", new[] { "STRINT.CMD", "STORE.DAT" }));

        var s = GroupArchiveEntryScanner.Scan(new[] { c, b, a }, _dataDir);

        Assert.Equal(4, s.EntryCount);
        Assert.Equal(3, s.ReleaseCount);
        Assert.Equal(2, s.LeafCount);
        Assert.False(s.IsIncomplete);
        Assert.Equal(
            new[] { "trs80-tosec-a|Game A1|DISK1.ZIP", "trs80-tosec-a|Game A2|DATA.7Z", "trs80-tosec-b|Game B1|X.RAR" },
            s.Examples.Select(e => $"{e.LeafId}|{e.ReleaseName}|{e.FileName}"));
    }

    [Fact]  // (25) missing and unreadable leaves are reported; usable results from other leaves are kept
    public void Scan_MissingAndUnreadableLeaves_ReportedWithoutAbort()
    {
        var good    = BuildLeaf("trs80-tosec-good", ("Game", new[] { "DISK.ZIP" }));
        var missing = new DatLineRecord { Id = "trs80-tosec-missing", DataStorePath = Rel("trs80-tosec-missing") };
        var noPath  = new DatLineRecord { Id = "trs80-tosec-nopath",  DataStorePath = "" };

        var corruptAbs = Path.Combine(_dataDir, Rel("trs80-tosec-corrupt"));
        Directory.CreateDirectory(Path.GetDirectoryName(corruptAbs)!);
        File.WriteAllText(corruptAbs, "this is not a sqlite database");
        var corrupt = new DatLineRecord { Id = "trs80-tosec-corrupt", DataStorePath = Rel("trs80-tosec-corrupt") };

        var s = GroupArchiveEntryScanner.Scan(new[] { missing, corrupt, good, noPath }, _dataDir);

        Assert.Equal(1, s.EntryCount);
        Assert.Equal(1, s.LeafCount);
        Assert.Equal(new[] { "trs80-tosec-corrupt", "trs80-tosec-missing", "trs80-tosec-nopath" }, s.UnscannedLeafIds);
        Assert.False(File.Exists(Path.Combine(_dataDir, Rel("trs80-tosec-missing"))));   // missing DB is never created
    }

    [Fact]  // read-only: scanning never modifies a leaf database
    public void Scan_IsReadOnly()
    {
        var leaf = BuildLeaf("trs80-tosec-ro", ("Game", new[] { "DISK.ZIP" }));
        var abs  = Path.Combine(_dataDir, leaf.DataStorePath);
        var before = Sha1File(abs);

        GroupArchiveEntryScanner.Scan(new[] { leaf }, _dataDir);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Assert.Equal(before, Sha1File(abs));
    }

    [Fact]
    public void Scan_ReportsProgressPerLeaf()
    {
        var a = BuildLeaf("trs80-tosec-p1", ("G", new[] { "A.ZIP" }));
        var b = BuildLeaf("trs80-tosec-p2", ("G", new[] { "B.CMD" }));
        var seen = new List<int>();

        GroupArchiveEntryScanner.Scan(new[] { a, b }, _dataDir, progress: new SyncProgress(seen.Add));

        Assert.Equal(new[] { 1, 2 }, seen);
    }

    [Fact]
    public void Scan_Cancelled_Throws()
    {
        var a = BuildLeaf("trs80-tosec-cx", ("G", new[] { "A.ZIP" }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => GroupArchiveEntryScanner.Scan(new[] { a }, _dataDir, cts.Token));
    }

    [Fact]
    public void Scan_NoLeaves_Empty()
    {
        var s = GroupArchiveEntryScanner.Scan(Array.Empty<DatLineRecord>(), _dataDir);
        Assert.False(s.HasArchiveEntries);
        Assert.False(s.IsIncomplete);
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _report;
        public SyncProgress(Action<int> report) => _report = report;
        public void Report(int value) => _report(value);
    }
}

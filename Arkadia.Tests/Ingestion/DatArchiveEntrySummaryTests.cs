using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Pure archive-entry summary + contextual wording for the Configure DAT / Configure Group notice: detection over
/// authoritative DAT release files using the shared container-extension set, counts, deterministic capped examples,
/// Group merge, and File-Handling-aware severity.
/// </summary>
public sealed class DatArchiveEntrySummaryTests
{
    private static ReleaseRecord Rel(string id, string name, string status = "missing")
        => new() { Id = id, DatLineId = "leaf", Name = name, Status = status };

    private static ReleaseFileRecord File_(string rom) => new() { RomName = rom, Size = "10" };

    private static DatArchiveEntrySummary Leaf(string leafId, params (ReleaseRecord Rel, string[] Roms)[] releases)
        => DatArchiveEntrySummary.ForLeaf(
            leafId,
            releases.Select(r => r.Rel).ToList(),
            releases.ToDictionary(r => r.Rel.Id, r => r.Roms.Select(File_).ToList()));

    // ── Detection (1–9) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("GAME.zip")]
    [InlineData("GAME.ZIP")]
    [InlineData("GAME.Zip")]
    [InlineData("GAME.7z")]
    [InlineData("GAME.RAR")]
    [InlineData(@"Disks\Side A\GAME.ZIP")]
    [InlineData("Disks/GAME.7Z")]
    public void ArchiveEntries_Detected(string rom) => Assert.True(DatArchiveEntrySummary.IsArchiveEntry(rom));

    [Theory]
    [InlineData("GAME.zipx")]
    [InlineData("file.zip.txt")]
    [InlineData("README")]
    [InlineData("zip")]
    [InlineData("")]
    [InlineData(null)]
    public void NonArchiveEntries_NotDetected(string? rom) => Assert.False(DatArchiveEntrySummary.IsArchiveEntry(rom));

    // ── Counts (10, 11, 15) ─────────────────────────────────────────────────

    [Fact]  // (10) two archive entries in one release = 2 entries, 1 affected release
    public void MultipleArchivesInOneRelease_CountOnce()
    {
        var s = Leaf("leaf", (Rel("r1", "Game"), new[] { "A.ZIP", "B.ZIP", "RUN.CMD" }));

        Assert.Equal(2, s.EntryCount);
        Assert.Equal(1, s.ReleaseCount);
        Assert.Equal(1, s.LeafCount);
    }

    [Fact]  // (11) multiple affected releases
    public void MultipleAffectedReleases_Counted()
    {
        var s = Leaf("leaf",
            (Rel("r1", "Game 1"), new[] { "A.ZIP" }),
            (Rel("r2", "Game 2"), new[] { "B.7z", "C.rar" }),
            (Rel("r3", "Game 3"), new[] { "PLAIN.DSK" }));

        Assert.Equal(3, s.EntryCount);
        Assert.Equal(2, s.ReleaseCount);
    }

    [Fact]  // (15) no archive entries → no warning
    public void NoArchiveEntries_NoWarning()
    {
        var s = Leaf("leaf", (Rel("r1", "Game"), new[] { "RUN.CMD", "DATA.DAT" }));

        Assert.False(s.HasArchiveEntries);
        Assert.Equal(DatArchiveWarningSeverity.None, DatArchiveEntryWarning.Describe(s, "archives_pre_extraction", isGroup: false).Severity);
        Assert.Equal(DatArchiveWarningSeverity.None, DatArchiveEntryWarning.Describe(DatArchiveEntrySummary.Empty, "all_files", isGroup: true).Severity);
    }

    // ── Release filter (23, 24) ─────────────────────────────────────────────

    [Fact]  // (23) unwanted releases are still analyzed
    public void UnwantedRelease_Included()
    {
        var s = Leaf("leaf", (Rel("r1", "Vetoed", "unwanted"), new[] { "A.ZIP" }));
        Assert.Equal(1, s.EntryCount);
        Assert.Equal(1, s.ReleaseCount);
    }

    [Fact]  // (24) outdated releases are excluded (same non-outdated filter as ingestion)
    public void OutdatedRelease_Excluded()
    {
        var s = Leaf("leaf",
            (Rel("r1", "Old", "outdated"), new[] { "A.ZIP" }),
            (Rel("r2", "Live", "present"), new[] { "B.ZIP" }));

        Assert.Equal(1, s.EntryCount);
        Assert.Equal(1, s.ReleaseCount);
        Assert.Equal("Live", Assert.Single(s.Examples).ReleaseName);
    }

    // ── Examples (12, 13, 14) ───────────────────────────────────────────────

    [Fact]  // (12)(13)(14) deterministic order, capped at 3, "+ N more"
    public void Examples_DeterministicCappedWithMore()
    {
        var s = Leaf("leaf",
            (Rel("r3", "Zork"),  new[] { "Z2.ZIP", "Z1.ZIP" }),
            (Rel("r1", "Alpha"), new[] { "B.ZIP", "A.ZIP" }),
            (Rel("r2", "Beta"),  new[] { "C.7Z" }));

        Assert.Equal(5, s.EntryCount);
        Assert.Equal(DatArchiveEntrySummary.MaxExamples, s.Examples.Count);
        Assert.Equal(new[] { "Alpha/A.ZIP", "Alpha/B.ZIP", "Beta/C.7Z" },
            s.Examples.Select(e => $"{e.ReleaseName}/{e.FileName}"));
        Assert.Equal(2, s.MoreCount);

        var w = DatArchiveEntryWarning.Describe(s, "all_files", isGroup: false);
        Assert.Equal(new[] { "Alpha — A.ZIP", "Alpha — B.ZIP", "Beta — C.7Z" }, w.Examples);
        Assert.Equal("+ 2 more", w.MoreLine);
    }

    [Fact]  // examples never exceed the cap even for large DATs, and ordering is insertion-independent
    public void Examples_LargeDat_StillTopThree()
    {
        var releases = Enumerable.Range(0, 500)
            .Select(i => (Rel($"r{i}", $"Game {i:D3}"), new[] { $"D{i:D3}.ZIP" }))
            .Reverse()
            .ToArray();
        var s = Leaf("leaf", releases);

        Assert.Equal(500, s.EntryCount);
        Assert.Equal(new[] { "Game 000", "Game 001", "Game 002" }, s.Examples.Select(e => e.ReleaseName));
        Assert.Equal(497, s.MoreCount);
    }

    [Fact]  // exactly 3 → no "+ N more"
    public void ExactlyThree_NoMoreLine()
    {
        var s = Leaf("leaf", (Rel("r1", "G"), new[] { "A.ZIP", "B.ZIP", "C.ZIP" }));
        Assert.Null(DatArchiveEntryWarning.Describe(s, "all_files", isGroup: false).MoreLine);
    }

    // ── Group merge (16–19) ─────────────────────────────────────────────────

    [Fact]  // (16)(17)(18)(19)
    public void GroupMerge_CountsAndLeafQualifiedExamples()
    {
        var a = Leaf("trs80-tosec-b", (Rel("r1", "Rel B1"), new[] { "X.ZIP", "Y.ZIP" }));
        var b = Leaf("trs80-tosec-a",
            (Rel("r1", "Rel A1"), new[] { "Q.7z" }),
            (Rel("r2", "Rel A2"), new[] { "W.RAR" }));
        var c = Leaf("trs80-tosec-c", (Rel("r1", "Plain"), new[] { "RUN.CMD" }));   // no archives

        var m = DatArchiveEntrySummary.Merge(new[] { a, b, c });

        Assert.Equal(4, m.EntryCount);                      // (16)
        Assert.Equal(3, m.ReleaseCount);                    // (17)
        Assert.Equal(2, m.LeafCount);                       // (18)
        Assert.Equal(1, m.MoreCount);
        Assert.False(m.IsIncomplete);

        var w = DatArchiveEntryWarning.Describe(m, "archives_pre_extraction", isGroup: true);
        Assert.Equal(new[]                                  // (19) leaf-qualified, deterministic
        {
            "trs80-tosec-a — Rel A1 — Q.7z",
            "trs80-tosec-a — Rel A2 — W.RAR",
            "trs80-tosec-b — Rel B1 — X.ZIP",
        }, w.Examples);
        Assert.Contains("4 archive files in 3 releases across 2 leaf DATs", w.Message[0]);
    }

    // ── Contextual wording (20, 21, 22) ─────────────────────────────────────

    private static DatArchiveEntrySummary OneZip()
        => Leaf("leaf", (Rel("r1", "Game"), new[] { "A.ZIP" }));

    [Fact]  // (20) All Files → Info
    public void AllFiles_IsInfo()
    {
        var w = DatArchiveEntryWarning.Describe(OneZip(), "all_files", isGroup: false);

        Assert.Equal(DatArchiveWarningSeverity.Info, w.Severity);
        Assert.Equal(DatArchiveEntryWarning.Title, w.Title);
        Assert.Contains(w.Message, l => l.StartsWith("File Handling is set to All Files."));
        Assert.DoesNotContain(w.Message, l => l.Contains("Pre-Extraction"));
    }

    [Fact]  // (21) Archives Pre-Extraction → Warning
    public void PreExtraction_IsWarning()
    {
        var w = DatArchiveEntryWarning.Describe(OneZip(), "archives_pre_extraction", isGroup: false);

        Assert.Equal(DatArchiveWarningSeverity.Warning, w.Severity);
        Assert.Equal("This DAT contains 1 archive file in 1 release as authoritative entries.", w.Message[0]);
        Assert.Equal("These may be expected software files rather than distribution containers.", w.Message[1]);
        Assert.Contains("File Handling is set to Archives Pre-Extraction.", w.Message);
        Assert.Contains(w.Message, l => l.Contains("may not be available to satisfy that DAT requirement"));
        Assert.Contains(w.Message, l => l.StartsWith("Verify that these archive entries are intended"));
    }

    [Theory]  // (22) unknown / mixed File Handling → safe informational fallback
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something_new")]
    public void UnknownFileHandling_IsInfoFallback(string? fileHandling)
    {
        var w = DatArchiveEntryWarning.Describe(OneZip(), fileHandling, isGroup: true);

        Assert.Equal(DatArchiveWarningSeverity.Info, w.Severity);
        Assert.Contains(w.Message, l => l.Contains("not set to a single known mode"));
    }

    // ── Incomplete Group summary (25, pure part) ────────────────────────────

    [Fact]  // (25) unscanned leaves are reported without discarding usable results
    public void UnscannedLeaves_ReportedAlongsideResults()
    {
        var m = DatArchiveEntrySummary.Merge(new[] { OneZip() }, new[] { "leaf-z", "leaf-y", "leaf-z" });

        Assert.Equal(1, m.EntryCount);
        Assert.Equal(new[] { "leaf-y", "leaf-z" }, m.UnscannedLeafIds);
        var w = DatArchiveEntryWarning.Describe(m, "all_files", isGroup: true);
        Assert.Equal(DatArchiveWarningSeverity.Info, w.Severity);
        Assert.Equal("2 leaf DATs could not be scanned — this summary may be incomplete.", w.IncompleteNote);
    }

    [Fact]  // no archive entries found, but some leaves unscanned → small Info diagnostic, never silent
    public void NoEntriesButUnscanned_IsIncompleteInfo()
    {
        var m = DatArchiveEntrySummary.Merge(new[] { DatArchiveEntrySummary.Empty }, new[] { "leaf-x" });
        var w = DatArchiveEntryWarning.Describe(m, "archives_pre_extraction", isGroup: true);

        Assert.Equal(DatArchiveWarningSeverity.Info, w.Severity);
        Assert.Equal(DatArchiveEntryWarning.IncompleteTitle, w.Title);
        Assert.Equal("1 leaf DAT could not be scanned — this summary may be incomplete.", w.IncompleteNote);
    }
}

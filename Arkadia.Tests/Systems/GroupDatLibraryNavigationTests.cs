using System;
using System.Collections.Generic;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// M7 tests: navigating from Group Details to the existing Single-DAT Library, scoped to ONE leaf resolved
/// by the authoritative <c>DatLineId</c> (never a group id, label, or path). Pure helpers only — no UI
/// automation, no catalog/filesystem mutation.
/// </summary>
public sealed class GroupDatLibraryNavigationTests
{
    private static GroupLeafRecord Leaf(string id, string relPath, string sourceName, string mediaId, int releases)
        => new(
            new DatLineRecord { Id = id, HardwareFamilyId = "c64", MediaTypeId = mediaId, Version = "1", ReleaseCount = releases },
            new DatLineGroupMetadataRecord { DatLineId = id, GroupId = "c64-tosec", RelativeDatPath = relPath, SourceDatName = sourceName });

    private static LibraryEntry Entry(string datLineId, string name)
        => new() { Name = name, Platform = "Commodore 64", Status = "Missing", Region = "", Languages = "",
                   Format = "", Size = "", Tier = "", DatLineId = datLineId };

    // ── Details rows carry the authoritative DatLineId ──────────────────────────

    [Fact]  // (1) row keeps DatLineId; (7) it is the dat_line id, not the group id; (8) path is not the identifier
    public void DetailsRow_LeafId_IsDatLineId_NotGroupOrPath()
    {
        var rows = GroupDatDetails.BuildRows(
            new[] { Leaf("c64-tosec-apps", "Applications/[D64].dat", "[D64].dat", "floppy", 65) },
            new Dictionary<string, string> { ["floppy"] = "Floppy Disk" });

        var row = Assert.Single(rows);
        Assert.Equal("c64-tosec-apps", row.LeafId);                 // dat_line id
        Assert.NotEqual("c64-tosec", row.LeafId);                   // (7) not the group id
        Assert.Equal("Applications/[D64].dat", row.SourcePath);     // (8) path is a separate field, not the id
        Assert.NotEqual(row.SourcePath, row.LeafId);
    }

    [Fact]  // (5) two leaves with the SAME source_dat_name stay distinguishable via DatLineId
    public void DetailsRows_SameSourceName_DistinctByDatLineId()
    {
        var rows = GroupDatDetails.BuildRows(
            new[]
            {
                Leaf("c64-tosec-a-games", "A/games.dat", "games.dat", "floppy", 10),
                Leaf("c64-tosec-b-games", "B/games.dat", "games.dat", "floppy", 20),
            },
            new Dictionary<string, string> { ["floppy"] = "Floppy Disk" });

        Assert.Equal("games.dat", rows[0].SourcePath.Split('/')[^1]);
        Assert.Equal("games.dat", rows[1].SourcePath.Split('/')[^1]);
        Assert.NotEqual(rows[0].LeafId, rows[1].LeafId);            // still uniquely addressable
    }

    // ── Library navigation resolves the correct dataset by DatLineId ────────────

    [Fact]  // (6) resolves the exact dat_line; (5) even when labels/paths collide
    public void Navigate_SelectsDatasetByDatLineId_NotLabel()
    {
        // Two datasets with the SAME display label ("TOSEC · OTHER" style) but different dat_line ids.
        var datasets = new List<LibraryDataset>
        {
            new("Commodore 64", "TOSEC · OTHER", new[] { Entry("c64-tosec-a", "Game A") }),
            new("Commodore 64", "TOSEC · OTHER", new[] { Entry("c64-tosec-b", "Game B") }),
        };

        var picked = LibraryDatasetSelector.ByDatLineId(datasets, "c64-tosec-b");
        Assert.NotNull(picked);
        Assert.Equal("c64-tosec-b", picked!.Entries[0].DatLineId);  // the RIGHT leaf, not the first label match
    }

    [Fact]  // (9) missing target → null; caller must NOT fall back to another leaf
    public void Navigate_MissingDatLine_ReturnsNull_NoFallback()
    {
        var datasets = new List<LibraryDataset>
        {
            new("Commodore 64", "TOSEC · OTHER", new[] { Entry("c64-tosec-a", "Game A") }),
        };

        Assert.Null(LibraryDatasetSelector.ByDatLineId(datasets, "c64-tosec-does-not-exist"));
    }

    [Fact]  // (7) a group id must never resolve as a dat_line id
    public void Navigate_GroupId_IsNotADatLine()
        => Assert.Null(LibraryDatasetSelector.ByDatLineId(
            new List<LibraryDataset> { new("Commodore 64", "TOSEC · OTHER", new[] { Entry("c64-tosec-a", "G") }) },
            "c64-tosec"));   // the group id, not a leaf id → no match

    // ── "Go to Library" enablement gate ─────────────────────────────────────────

    [Theory]  // (2) no selection → disabled; (3) selection + loaded → enabled
    [InlineData(false, false, false)]   // not loaded, no selection
    [InlineData(true,  false, false)]   // loaded, no selection → disabled
    [InlineData(false, true,  false)]   // selection before load → disabled
    [InlineData(true,  true,  true)]    // loaded + selection → enabled
    public void GoToLibraryGate(bool rowsLoaded, bool hasSelection, bool expected)
        => Assert.Equal(expected, GroupDatDetailsGate.CanGoToLibrary(rowsLoaded, hasSelection));
}

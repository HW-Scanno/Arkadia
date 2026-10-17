using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.Library;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Library;

/// <summary>
/// DAT-NAMING-2 identity-safe routing: a display label is never an identity. Two standalone DATs sharing the same
/// system/authority/media (and even the same label) stay distinguishable and independently selectable in the
/// Library/Catalog selectors, the volume owner/collection labels; Group presentation is unchanged.
/// </summary>
public sealed class SingleDatRoutingTests
{
    private static LibraryEntry Entry(string datLineId, string name = "R") => new()
    {
        Name = name, Platform = "Nintendo DS", Status = "Present", Region = "", Languages = "",
        Format = "", Size = "", Tier = "", DatLineId = datLineId,
    };

    private static LibraryDataset Ds(string label, string datLineId, string platform = "Nintendo DS", params string[] releases)
        => new(platform, label, (releases.Length == 0 ? new[] { "R" } : releases).Select(r => Entry(datLineId, r)).ToList());

    private static readonly ISet<string> NoLeaves = new HashSet<string>();

    // ── 11. Selection by id regardless of label similarity ──────────────────────

    [Fact]
    public void DatasetSelector_ResolvesTheRightDat_WhenLabelsAreIdentical()
    {
        var a = Ds("No-Intro · CARTRIDGE", "nds-nointro-cartridge-main",  releases: ["A1", "A2"]);
        var b = Ds("No-Intro · CARTRIDGE", "nds-nointro-cartridge-extra", releases: ["B1"]);
        var datasets = new[] { a, b };

        Assert.Same(a, LibraryDatasetSelector.ByDatLineId(datasets, "nds-nointro-cartridge-main"));
        Assert.Same(b, LibraryDatasetSelector.ByDatLineId(datasets, "nds-nointro-cartridge-extra"));
        Assert.Null(LibraryDatasetSelector.ByDatLineId(datasets, "nds-nointro-cartridge-missing"));   // no fallback
    }

    [Fact]
    public void DatasetSelector_IsOrdinalOnId()
    {
        var a = Ds("L", "nds-nointro-cartridge-main");
        Assert.Null(LibraryDatasetSelector.ByDatLineId(new[] { a }, "NDS-NOINTRO-CARTRIDGE-MAIN"));
    }

    [Fact]
    public void Builder_SameLabelStandaloneDats_AreDistinguishableAndIndependentlySelectable()
    {
        var datasets = new[]
        {
            Ds("No-Intro · CARTRIDGE", "nds-nointro-cartridge-main"),
            Ds("No-Intro · CARTRIDGE", "nds-nointro-cartridge-extra"),
            Ds("ReDump · DVD",         "nds-redump-dvd-main"),
        };

        foreach (var mark in new[] { true, false })
        {
            var items = LibrarySelectorItemBuilder.Build(datasets, "Nintendo DS", NoLeaves, markGroupLeaves: mark);
            Assert.Equal(3, items.Count);
            Assert.Equal(3, items.Select(i => i.Display).Distinct().Count());                  // no two look alike
            Assert.EndsWith("— nds-nointro-cartridge-main",  items[0].Display);
            Assert.EndsWith("— nds-nointro-cartridge-extra", items[1].Display);
            Assert.Equal("ReDump · DVD", items[2].Display);                                   // unique label unchanged
            Assert.Equal(1, LibrarySelectorItem.IndexOfDatLineId(items, "nds-nointro-cartridge-extra"));
        }
    }

    [Fact]
    public void Builder_DifferentDisplayNames_NeedNoSuffix()
    {
        var items = LibrarySelectorItemBuilder.Build(new[]
        {
            Ds("NoIntro - Main",  "nds-nointro-cartridge-main"),
            Ds("NoIntro - Extra", "nds-nointro-cartridge-extra"),
        }, "Nintendo DS", NoLeaves, markGroupLeaves: true);

        Assert.Equal(new[] { "NoIntro - Main", "NoIntro - Extra" }, items.Select(i => i.Display));
        Assert.All(items, i => Assert.False(i.ShowId));
    }

    [Fact]
    public void Builder_FiltersByPlatform_AndKeepsIdentityFromTheEntries()
    {
        var items = LibrarySelectorItemBuilder.Build(new[]
        {
            Ds("X", "nds-a-b-main", "Nintendo DS"),
            Ds("X", "gba-a-b-main", "Game Boy Advance"),
        }, "Game Boy Advance", NoLeaves, markGroupLeaves: true);

        var only = Assert.Single(items);
        Assert.Equal("gba-a-b-main", only.DatLineId);
        Assert.False(only.ShowId);
    }

    // ── 12. Group presentation unchanged ────────────────────────────────────────

    [Fact]
    public void Builder_Library_GroupLeavesKeepTheirIdSuffix_SinglesUnchanged()
    {
        var leaves = new HashSet<string> { "c64-tosec-games-d64", "c64-tosec-apps-d64" };
        var items = LibrarySelectorItemBuilder.Build(new[]
        {
            Ds("TOSEC · FLOPPY", "c64-tosec-games-d64", "C64"),
            Ds("TOSEC · FLOPPY", "c64-tosec-apps-d64",  "C64"),
            Ds("Redump · DISC",  "c64-redump-disc-main", "C64"),
        }, "C64", leaves, markGroupLeaves: true);

        Assert.True(items[0].IsGroupLeaf);
        Assert.Equal("TOSEC · FLOPPY — c64-tosec-games-d64", items[0].Display);   // exactly as before
        Assert.Equal("TOSEC · FLOPPY — c64-tosec-apps-d64",  items[1].Display);
        Assert.Equal("Redump · DISC", items[2].Display);
        Assert.False(items[2].ShowId);
    }

    [Fact]
    public void Builder_Library_SingleSharingALabelWithAGroupLeaf_StaysVerbatim()
    {
        // The leaf is already disambiguated by its suffix, so the Single's label must not change.
        var items = LibrarySelectorItemBuilder.Build(new[]
        {
            Ds("TOSEC · FLOPPY", "c64-tosec-games-d64", "C64"),
            Ds("TOSEC · FLOPPY", "c64-tosec-floppy-main", "C64"),
        }, "C64", new HashSet<string> { "c64-tosec-games-d64" }, markGroupLeaves: true);

        Assert.Equal("TOSEC · FLOPPY", items[1].Display);
    }

    [Fact]
    public void Builder_Catalog_GroupLeavesKeepTheirHistoricalPlainLabel()
    {
        var leaves = new HashSet<string> { "c64-tosec-games-d64", "c64-tosec-apps-d64" };
        var items = LibrarySelectorItemBuilder.Build(new[]
        {
            Ds("TOSEC · FLOPPY", "c64-tosec-games-d64", "C64"),
            Ds("TOSEC · FLOPPY", "c64-tosec-apps-d64",  "C64"),
        }, "C64", leaves, markGroupLeaves: false);

        Assert.All(items, i => { Assert.False(i.IsGroupLeaf); Assert.False(i.ShowId); Assert.Equal("TOSEC · FLOPPY", i.Display); });
        // ...yet each is still independently selectable by id (previously the first label match always won).
        Assert.Equal(1, LibrarySelectorItem.IndexOfDatLineId(items, "c64-tosec-apps-d64"));
    }

    [Fact]
    public void Builder_Catalog_StandaloneCollidingWithAnUnmarkedLeaf_GetsTheId()
    {
        var items = LibrarySelectorItemBuilder.Build(new[]
        {
            Ds("TOSEC · FLOPPY", "c64-tosec-games-d64",   "C64"),
            Ds("TOSEC · FLOPPY", "c64-tosec-floppy-main", "C64"),
        }, "C64", new HashSet<string> { "c64-tosec-games-d64" }, markGroupLeaves: false);

        Assert.Equal("TOSEC · FLOPPY", items[0].Display);                       // leaf unchanged
        Assert.Equal("TOSEC · FLOPPY — c64-tosec-floppy-main", items[1].Display);
    }

    [Fact]
    public void SelectorItem_ExistingThreeArgConstruction_BehavesAsBefore()
    {
        Assert.Equal("L — id", new LibrarySelectorItem("id", "L", true).Display);
        Assert.Equal("L",      new LibrarySelectorItem("id", "L", false).Display);
    }

    // ── Volumes: collection label and owner selector ────────────────────────────

    [Fact]
    public void CollectionLabel_PersistedDisplayNameWins_LegacyFallsBack()
    {
        Assert.Equal("NoIntro - Main",
            VolumeCollectionLabel.ForSingle("No-Intro", "Cartridge", "cartridge", "nds-nointro-cartridge-main", "NoIntro - Main"));
        Assert.Equal("No-Intro · Cartridge",
            VolumeCollectionLabel.ForSingle("No-Intro", "Cartridge", "cartridge", "nds-nointro-cartridge", null));
        Assert.Equal("No-Intro · Cartridge",
            VolumeCollectionLabel.ForSingle("No-Intro", "Cartridge", "cartridge", "nds-nointro-cartridge", "  "));
        // Existing 4-argument callers (Group-independent legacy path) are unchanged.
        Assert.Equal("ReDump · DVD", VolumeCollectionLabel.ForSingle("ReDump", "DVD", "dvd", "id"));
        Assert.Equal("My Group", VolumeCollectionLabel.ForGroup("My Group", "g"));
    }

    private static DatLineRecord Dl(string id, string display, string authority = "nointro", string name = "cartridge")
        => new() { Id = id, HardwareFamilyId = "nds", Name = name, Authority = authority, MediaTypeId = name, DisplayName = display };

    [Fact]
    public void OwnerSelector_TwoSinglesSameTriple_AreDistinctByDisplayAndByIdentity()
    {
        var owners = VolumeOwnerSelectionBuilder.Build("nds",
            new[] { Dl("nds-nointro-cartridge-main", null!), Dl("nds-nointro-cartridge-extra", null!) },
            [], NoLeaves, new Dictionary<string, string> { ["nointro"] = "No-Intro" });

        Assert.Equal(2, owners.Count);
        Assert.Equal(2, owners.Select(o => o.Display).Distinct().Count());           // legacy-style labels collide → ids appended
        Assert.Equal(new[] { "nds-nointro-cartridge-extra", "nds-nointro-cartridge-main" }, owners.Select(o => o.OwnerId));
        Assert.All(owners, o => Assert.Contains(o.OwnerId, o.Display));
    }

    [Fact]
    public void OwnerSelector_DisplayNames_AreUsed_AndLegacyRowsKeepTheOldLabel()
    {
        var owners = VolumeOwnerSelectionBuilder.Build("nds",
            new[]
            {
                Dl("nds-nointro-cartridge",       null!),
                Dl("nds-nointro-cartridge-main",  "NoIntro - Main"),
            },
            [], NoLeaves, new Dictionary<string, string> { ["nointro"] = "No-Intro" });

        Assert.Contains(owners, o => o.OwnerId == "nds-nointro-cartridge"      && o.Display == "Single · No-Intro · cartridge");
        Assert.Contains(owners, o => o.OwnerId == "nds-nointro-cartridge-main" && o.Display == "Single · NoIntro - Main");
    }

    [Fact]
    public void OwnerSelector_GroupLeavesStillExcluded_GroupRowUnchanged()
    {
        var group = new DatGroupRecord
        {
            Id = DatGroupId.TryCreateNew("nds-tosec", out var g, out _, out _) ? g : throw new InvalidOperationException(),
            HardwareFamilyId = "nds", DisplayName = "NDS TOSEC", Authority = "tosec",
        };
        var owners = VolumeOwnerSelectionBuilder.Build("nds",
            new[] { Dl("nds-nointro-cartridge-main", "NoIntro - Main"), Dl("nds-tosec-games", null!, "tosec", "other") },
            new[] { group }, new HashSet<string> { "nds-tosec-games" }, new Dictionary<string, string>());

        Assert.Equal(2, owners.Count);
        Assert.DoesNotContain(owners, o => o.OwnerId == "nds-tosec-games");
        Assert.Contains(owners, o => o.Kind == VolumeOwnerKind.Group && o.OwnerId == "nds-tosec" && o.Display == "Group · NDS TOSEC");
    }

    // ── Source contract for the navigation paths that need a WPF-less pin ───────

    [Fact]
    public void CatalogTab_And_AmpExport_RouteByDatLineId_NotByLabel()
    {
        var root = AppContext.BaseDirectory;
        var dir  = new DirectoryInfo(root);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "MainWindow.axaml.cs"));

        Assert.DoesNotContain("d.Platform == platform && d.DatLine == datLine", src);        // old label routing
        Assert.DoesNotContain("CatalogContextDatLine.SelectedItem as string", src);
        Assert.DoesNotContain(".Find(d => d.Name == datLineName)", src);
        Assert.DoesNotContain("string.Equals(i.Label, datLine", src);
        Assert.Contains("LibraryDatasetSelector.ByDatLineId(_activeLibraryDatasets, datLineId)", src);
    }
}

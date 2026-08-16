using System.Collections.Generic;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Library;

/// <summary>
/// The typed Library DAT-line selector item: Display carries the full DatLineId for Group leaves (so
/// visually-identical labels stay distinguishable), Single labels are unchanged, and all selection resolves
/// by the authoritative DatLineId — never the label or list position.
/// </summary>
public sealed class LibrarySelectorItemTests
{
    [Fact] // 1: a Group leaf Display includes the exact, unabbreviated DatLineId
    public void GroupLeaf_Display_IncludesFullDatLineId()
    {
        var item = new LibrarySelectorItem("c64-tosec-games-strategy-d64", "TOSEC · FLOPPY", IsGroupLeaf: true);
        Assert.Equal("TOSEC · FLOPPY — c64-tosec-games-strategy-d64", item.Display);
        Assert.Equal(item.Display, item.ToString());
    }

    [Fact] // 2: two Group leaves with identical human labels produce distinct Display strings
    public void GroupLeaves_SameLabel_DistinctDisplay()
    {
        var a = new LibrarySelectorItem("c64-tosec-games-action-d64",   "TOSEC · FLOPPY", true);
        var b = new LibrarySelectorItem("c64-tosec-games-strategy-d64", "TOSEC · FLOPPY", true);
        Assert.NotEqual(a.Display, b.Display);
        Assert.EndsWith("c64-tosec-games-action-d64",   a.Display);
        Assert.EndsWith("c64-tosec-games-strategy-d64", b.Display);
    }

    [Fact] // 3 + 4: typed selection resolves by DatLineId, picking the intended duplicate-label leaf
    public void IndexOfDatLineId_ResolvesByIdAcrossDuplicateLabels()
    {
        var items = new List<LibrarySelectorItem>
        {
            new("c64-tosec-games-action-d64",   "TOSEC · FLOPPY", true),
            new("c64-tosec-games-strategy-d64", "TOSEC · FLOPPY", true),
            new("c64-tosec-firmware-crt",       "TOSEC · OTHER",  true),
        };

        Assert.Equal(1, LibrarySelectorItem.IndexOfDatLineId(items, "c64-tosec-games-strategy-d64"));
        Assert.Equal(0, LibrarySelectorItem.IndexOfDatLineId(items, "c64-tosec-games-action-d64"));
        Assert.Equal(2, LibrarySelectorItem.IndexOfDatLineId(items, "c64-tosec-firmware-crt"));
        Assert.Equal(-1, LibrarySelectorItem.IndexOfDatLineId(items, "does-not-exist"));
        Assert.Equal(-1, LibrarySelectorItem.IndexOfDatLineId(items, null));
    }

    [Fact] // 5: a Single (non-group) entry Display is exactly its historical human label
    public void SingleEntry_Display_IsHumanLabelVerbatim()
    {
        var item = new LibrarySelectorItem("redump-c64-disc", "Redump · DISC", IsGroupLeaf: false);
        Assert.Equal("Redump · DISC", item.Display);
        Assert.DoesNotContain("redump-c64-disc", item.Display);   // id not appended for Single
    }

    [Fact] // 6: Single selection still resolves to the correct DatLineId
    public void IndexOfDatLineId_ResolvesSingleEntry()
    {
        var items = new List<LibrarySelectorItem>
        {
            new("redump-c64-disc", "Redump · DISC", false),
            new("nointro-c64-cart", "No-Intro · CART", false),
        };
        Assert.Equal(1, LibrarySelectorItem.IndexOfDatLineId(items, "nointro-c64-cart"));
    }

    [Fact] // 7: Label is preserved separately from Display and never receives the id suffix
    public void Label_IsPreserved_SeparateFromDisplay()
    {
        var group  = new LibrarySelectorItem("c64-tosec-firmware-crt", "TOSEC · OTHER", true);
        var single = new LibrarySelectorItem("redump-c64-disc",        "Redump · DISC", false);

        Assert.Equal("TOSEC · OTHER", group.Label);      // human label unchanged
        Assert.NotEqual(group.Label, group.Display);     // Display differs (id appended) for the group leaf
        Assert.Equal(single.Label, single.Display);      // Single: Display == Label
    }
}

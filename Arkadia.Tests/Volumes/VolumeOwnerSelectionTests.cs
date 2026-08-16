using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Unified Create Volume owner selector: standalone Single DATs + Groups as top-level owners (never Group
/// leaves), typed (Kind, OwnerId) identity, shared label numbering, and ownership-dispatched VolumeRecord.
/// </summary>
public sealed class VolumeOwnerSelectionTests
{
    private static readonly Dictionary<string, string> NoAuthorities = new();

    private static DatLineRecord Dl(string id, string family, string name, string authority = "")
        => new() { Id = id, HardwareFamilyId = family, Name = name, Authority = authority };

    private static List<VolumeOwnerSelection> Build(
        string platform, IReadOnlyList<DatLineRecord> datLines, IReadOnlyList<DatGroupRecord> groups,
        ISet<string> leaves, IReadOnlyDictionary<string, string>? authorities = null)
        => VolumeOwnerSelectionBuilder.Build(platform, datLines, groups, leaves, authorities ?? NoAuthorities);

    private static DatGroupRecord Grp(string id, string family, string display)
        => new()
        {
            Id = DatGroupId.TryCreateNew(id, out var g, out _, out _) ? g : throw new InvalidOperationException(),
            HardwareFamilyId = family, DisplayName = display, Authority = "tosec",
        };

    [Fact] // §14 mandatory: exactly the standalone Single + the Group; the three Group leaves are excluded
    public void Build_ExcludesGroupLeaves_ListsSingleAndGroup()
    {
        var datLines = new[]
        {
            Dl("c64-redump", "c64", "Redump · DISC"),
            Dl("c64-tosec-firmware-crt",       "c64", "TOSEC · OTHER"),
            Dl("c64-tosec-games-strategy-d64", "c64", "TOSEC · FLOPPY"),
            Dl("c64-tosec-games-action-d64",   "c64", "TOSEC · FLOPPY"),
        };
        var groups       = new[] { Grp("c64-tosec", "c64", "Commodore 64 TOSEC") };
        var groupLeafIds = new HashSet<string>(new[] { "c64-tosec-firmware-crt", "c64-tosec-games-strategy-d64", "c64-tosec-games-action-d64" });

        var owners = Build("c64", datLines, groups, groupLeafIds);

        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, o => o.Kind == VolumeOwnerKind.SingleDat && o.OwnerId == "c64-redump");   // 1
        Assert.Contains(owners, o => o.Kind == VolumeOwnerKind.Group     && o.OwnerId == "c64-tosec");    // 2
        Assert.DoesNotContain(owners, o => o.OwnerId.StartsWith("c64-tosec-"));                            // 3: no leaves
    }

    [Fact] // §16.9: owner choices are filtered to the selected platform
    public void Build_FiltersByPlatform()
    {
        var datLines = new[] { Dl("c64-redump", "c64", "Redump"), Dl("nes-nointro", "nes", "No-Intro") };
        var owners = Build("c64", datLines, Array.Empty<DatGroupRecord>(), new HashSet<string>());
        Assert.Single(owners);
        Assert.Equal("c64-redump", owners[0].OwnerId);
    }

    [Fact] // deterministic: Single owners first, then Groups
    public void Build_SingleBeforeGroup()
    {
        var datLines = new[] { Dl("c64-redump", "c64", "Redump") };
        var groups   = new[] { Grp("c64-tosec", "c64", "TOSEC") };
        var owners = Build("c64", datLines, groups, new HashSet<string>());
        Assert.Equal(VolumeOwnerKind.SingleDat, owners[0].Kind);
        Assert.Equal(VolumeOwnerKind.Group,     owners[1].Kind);
    }

    [Fact] // §15/§16.13: identical Display text stays distinct because identity is (Kind, OwnerId)
    public void Identity_ByKindAndOwnerId_NotDisplay()
    {
        var a = new VolumeOwnerSelection(VolumeOwnerKind.SingleDat, "c64-a", "c64", "Redump · DISC");
        var b = new VolumeOwnerSelection(VolumeOwnerKind.SingleDat, "c64-b", "c64", "Redump · DISC");
        Assert.NotEqual(a, b);                     // same Display, different OwnerId → distinct
        Assert.Equal("c64-b", b.ToVolumeRecord("x", 1).DatLineId);   // resolves by OwnerId, not label
    }

    [Fact] // §16.4 + 12: Single owner → DatLineId set, GroupId null, header-only
    public void ToVolumeRecord_Single()
    {
        var v = new VolumeOwnerSelection(VolumeOwnerKind.SingleDat, "c64-redump", "c64", "Single · Redump")
            .ToVolumeRecord("c64-redump-001", 42);
        Assert.Equal("c64-redump", v.DatLineId);
        Assert.Null(v.GroupId);
        Assert.True(v.IsDatLineOwned);
        Assert.Equal("init", v.Status);
        Assert.Equal(0, v.ActualSizeBytes);
        Assert.Equal(42, v.PlannedSizeBytes);
    }

    [Fact] // §16.5 + 12: Group owner → GroupId set, DatLineId null, header-only
    public void ToVolumeRecord_Group()
    {
        var v = new VolumeOwnerSelection(VolumeOwnerKind.Group, "c64-tosec", "c64", "Group · TOSEC")
            .ToVolumeRecord("c64-tosec-001", 99);
        Assert.Equal("c64-tosec", v.GroupId);
        Assert.Null(v.DatLineId);
        Assert.True(v.IsGroupOwned);
        Assert.Equal("init", v.Status);
        Assert.Equal(0, v.ActualSizeBytes);
    }

    [Fact] // §16.6/7/8: label numbering dispatches on owner id, same helper for both
    public void LabelSuggestion_DispatchesOnOwnerId()
    {
        var single = new VolumeOwnerSelection(VolumeOwnerKind.SingleDat, "c64-redump", "c64", "Single · Redump");
        var group  = new VolumeOwnerSelection(VolumeOwnerKind.Group,     "c64-tosec",  "c64", "Group · TOSEC");
        var labels = new[] { "c64-redump-001", "c64-tosec-001", "c64-tosec-003" };

        Assert.Equal("c64-redump-002", VolumeLabelNumbering.SuggestLabel(labels, single.LabelPrefix));
        Assert.Equal("c64-tosec-004",  VolumeLabelNumbering.SuggestLabel(labels, group.LabelPrefix));
    }

    // ── Descriptive Single display (UX refinement) ───────────────────────────

    [Fact] // §14.1: Single · <Authority> · <name>
    public void SingleDisplay_Descriptive()
    {
        var datLines  = new[] { Dl("ps2-redump-cd", "ps2", "CD", authority: "redump") };
        var authority = new Dictionary<string, string> { ["redump"] = "Redump" };

        var owners = Build("ps2", datLines, Array.Empty<DatGroupRecord>(), new HashSet<string>(), authority);
        Assert.Equal("Single · Redump · CD", owners.Single().Display);
    }

    [Fact] // §14.2: same media/name, different Authority → distinguishable
    public void SingleDisplay_SameName_DifferentAuthority_Distinct()
    {
        var datLines = new[]
        {
            Dl("ps2-redump-cd",  "ps2", "CD", authority: "redump"),
            Dl("ps2-nointro-cd", "ps2", "CD", authority: "nointro"),
        };
        var authority = new Dictionary<string, string> { ["redump"] = "Redump", ["nointro"] = "No-Intro" };

        var owners = Build("ps2", datLines, Array.Empty<DatGroupRecord>(), new HashSet<string>(), authority);
        Assert.NotEqual(owners[0].Display, owners[1].Display);
        Assert.Contains("Redump",   owners.Single(o => o.OwnerId == "ps2-redump-cd").Display);
        Assert.Contains("No-Intro", owners.Single(o => o.OwnerId == "ps2-nointro-cd").Display);
    }

    [Fact] // §3/§14.3: incomplete metadata never collapses to "Single · cd" while a DatLineId exists
    public void SingleDisplay_IncompleteMetadata_StaysDistinguishable()
    {
        // No authority mapping, weak name "cd" — must still include the technical id.
        var d = VolumeOwnerSelection.SingleDisplay(authorityName: "", name: "cd", datLineId: "ps2-something-cd");
        Assert.Equal("Single · cd · ps2-something-cd", d);
        Assert.NotEqual("Single · cd", d);
    }

    [Fact] // §3 fallback hierarchy: authority+id when name missing; id alone when nothing else
    public void SingleDisplay_FallbackHierarchy()
    {
        Assert.Equal("Single · Redump · ps2-redump-cd", VolumeOwnerSelection.SingleDisplay("Redump", "", "ps2-redump-cd"));
        Assert.Equal("Single · ps2-x",                  VolumeOwnerSelection.SingleDisplay("", "", "ps2-x"));
    }

    [Fact] // §14.4: Group display stays "Group · <DisplayName>" (uncluttered)
    public void GroupDisplay_Unchanged()
    {
        var groups = new[] { Grp("c64-tosec", "c64", "Commodore 64 TOSEC") };
        var owners = Build("c64", Array.Empty<DatLineRecord>(), groups, new HashSet<string>());
        Assert.Equal("Group · Commodore 64 TOSEC", owners.Single().Display);
    }

    // ── Dynamic best-effort label (VolumeLabelSuggestion) ────────────────────

    [Fact] // §14.5/6: prefix comes from the selected owner id (Group→GroupId, Single→DatLineId)
    public void LabelSuggestion_PrefixFromOwner()
    {
        var group  = new VolumeOwnerSelection(VolumeOwnerKind.Group,     "c64-tosec",  "c64", "Group · TOSEC");
        var single = new VolumeOwnerSelection(VolumeOwnerKind.SingleDat, "ps2-redump", "ps2", "Single · Redump");
        Assert.Equal("c64-tosec-001",  VolumeLabelSuggestion.Suggest([], group,  "c64"));
        Assert.Equal("ps2-redump-001", VolumeLabelSuggestion.Suggest([], single, "ps2"));
    }

    [Fact] // §14.7: switching owner A → B recomputes the prefix from B (even after a manual edit, by construction)
    public void LabelSuggestion_RecomputesOnOwnerChange()
    {
        var a = new VolumeOwnerSelection(VolumeOwnerKind.SingleDat, "ps2-redump", "ps2", "Single · Redump");
        var b = new VolumeOwnerSelection(VolumeOwnerKind.Group,     "c64-tosec",  "c64", "Group · TOSEC");
        Assert.Equal("ps2-redump-001", VolumeLabelSuggestion.Suggest([], a, "ps2"));
        Assert.Equal("c64-tosec-001",  VolumeLabelSuggestion.Suggest([], b, "c64"));   // new context → new prefix
    }

    [Fact] // §6: platform-only fallback uses the technical PlatformId when no collection is selected
    public void LabelSuggestion_PlatformFallback()
    {
        Assert.Equal("c64-001", VolumeLabelSuggestion.Suggest([], owner: null, platformId: "c64"));
        Assert.Equal("",        VolumeLabelSuggestion.Suggest([], owner: null, platformId: ""));   // nothing available
    }

    [Fact] // §13: numbering still delegates to VolumeLabelNumbering (max+1, gaps skipped)
    public void LabelSuggestion_UsesVolumeLabelNumbering()
    {
        var group  = new VolumeOwnerSelection(VolumeOwnerKind.Group, "c64-tosec", "c64", "Group · TOSEC");
        var labels = new[] { "c64-tosec-001", "c64-tosec-003" };
        Assert.Equal("c64-tosec-004", VolumeLabelSuggestion.Suggest(labels, group, "c64"));
    }
}

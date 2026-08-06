using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.GroupDats;
using Xunit;

namespace Arkadia.Tests.GroupDats;

/// <summary>
/// Tests for the pure Group-DAT manual reconciliation model with the explicit identity contract:
/// caller-fixed mode (<c>ForNewGroup</c> / <c>ForExistingGroup</c>), distinct Group Name + Group ID,
/// Group-ID suggestion/validation/collision, Group-ID-prefixed leaf ids, and the sequential
/// one-DAT-at-a-time create/associate/absent/undo flow. No UI, no DB.
/// </summary>
public sealed class GroupDatReconciliationTests
{
    // ── Builders ────────────────────────────────────────────────────────────────

    private static DiscoveredDatLeaf Leaf(string rel, string name = "DAT", int games = 1,
        DiscoveredDatLeafStatus status = DiscoveredDatLeafStatus.Parsed)
    {
        var g = Enumerable.Range(0, games)
            .Select(i => new DiscoveredDatGame($"g{i}", "", "", "", "", ImmutableArray<DiscoveredDatRom>.Empty))
            .ToImmutableArray();
        return new DiscoveredDatLeaf
        {
            RelativePath = rel,
            FileName     = Path.GetFileName(rel),
            SourcePath   = "/src/" + rel,
            Status       = status,
            DatName      = name,
            DatVersion   = "TOSEC-v2021-07-26",
            DatDate      = "2021-07-26",
            DatAuthor    = "A",
            Games        = status == DiscoveredDatLeafStatus.Parsed ? g : ImmutableArray<DiscoveredDatGame>.Empty,
        };
    }

    private static DatGroupDiscoveryResult Discovery(string root, IEnumerable<DiscoveredDatLeaf> leaves,
        IEnumerable<DatGroupDiscoveryDiagnostic>? diags = null) => new()
    {
        SourceRoot  = root,
        Leaves      = leaves.ToList(),
        Diagnostics = (diags ?? Array.Empty<DatGroupDiscoveryDiagnostic>()).ToList(),
    };

    private static GroupDatCatalogPreviewData Catalog(
        IEnumerable<string>? occupied = null,
        IEnumerable<GroupDatExistingGroup>? groups = null) => new()
    {
        ExistingGroups   = (groups ?? Array.Empty<GroupDatExistingGroup>()).ToImmutableArray(),
        HardwareFamilies = ImmutableArray.Create(
            new GroupDatOption("c64", "C64"), new GroupDatOption("nes", "NES"), new GroupDatOption("capcom", "Capcom")),
        MediaTypes       = ImmutableArray.Create(
            new GroupDatOption("other", "Other"), new GroupDatOption("cd", "CD"),
            new GroupDatOption("floppy", "Floppy"), new GroupDatOption("tape", "Tape"),
            new GroupDatOption("cartridge", "Cartridge")),
        Authorities      = ImmutableArray.Create(new GroupDatOption("tosec", "TOSEC"), new GroupDatOption("nointro", "No-Intro")),
        OccupiedLeafIds  = (occupied ?? Array.Empty<string>()).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
    };

    private const string SysId = "c64", SysName = "C64", Auth = "tosec", Gid = "c64-tosec", Gname = "TOSEC";

    private static GroupDatExistingLeaf ExLeaf(string id, string media = "other") =>
        new(id, Gid, "old/path.dat", "old.dat", "1", 10, media, SysId, Auth, 0);

    // Create-mode session under System c64/C64, authority tosec, Group Name "TOSEC", Group ID "c64-tosec".
    private static GroupDatReconciliationSession NewGroup(
        DatGroupDiscoveryResult disc, GroupDatCatalogPreviewData? cat = null,
        string groupId = Gid, string groupName = Gname, string authority = Auth)
    {
        var s = GroupDatReconciliationSession.ForNewGroup(cat ?? Catalog(), SysId, SysName, authority, groupName, groupId);
        s.SetDiscovery(disc);
        return s;
    }

    // Create mode: make every implicit proposal valid by giving them all a media type.
    private static void ResolveAllNew(GroupDatReconciliationSession s, string media = "other")
        => s.ApplyDefaultMediaTypeToUnresolved(media);

    // Update-mode session bound to the existing group "c64-tosec" and its leaves.
    private static GroupDatReconciliationSession UpdateGroup(DatGroupDiscoveryResult disc, params string[] leafIds)
    {
        var group = new GroupDatExistingGroup(Gid, Gname, SysId, Auth, 0,
            leafIds.Select(id => ExLeaf(id)).ToImmutableArray());
        var s = GroupDatReconciliationSession.ForExistingGroup(Catalog(leafIds, new[] { group }), Gid);
        s.SetDiscovery(disc);
        return s;
    }

    // ── Leaf-id proposal (Group-ID-prefixed, folder-token driven, filename-free) ─

    [Fact]
    public void IdProposal_FromFolderTokens_ShortAndFilenameFree()
    {
        var file = "Animations/[D64]/Commodore C64 - Animations - [D64] (TOSEC-v2021-07-26_CM).dat";
        var s = NewGroup(Discovery("/src", new[] { Leaf(file) }));
        var c = s.AvailableIncoming.Single();
        var id = s.EffectiveIdFor(c);

        Assert.Equal("c64-tosec-animations-d64", id);
        Assert.DoesNotContain("commodore", id);          // full filename not embedded
        Assert.DoesNotContain("2021", id);               // TOSEC version/date not embedded
        Assert.Equal("", c.DatToken);                    // Dat Suffix starts empty
    }

    [Fact]
    public void IdProposal_LnxFolder()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[LNX]/x.dat") }));
        Assert.Equal("c64-tosec-animations-lnx", s.EffectiveIdFor(s.AvailableIncoming.Single()));
    }

    [Fact]
    public void IdProposal_EmptyDatSuffix_DoesNotChangeId()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        var d = s.AvailableIncoming.Single();
        Assert.Equal("", d.DatToken);
        Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(d));
        d.DatToken = "";
        Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(d));
    }

    [Fact]
    public void IdProposal_ManualDatSuffix_AppendedAtEnd()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        var d = s.AvailableIncoming.Single();
        d.DatToken = "extra";
        Assert.Equal("c64-tosec-animations-d64-extra", s.EffectiveIdFor(d));
    }

    [Fact]
    public void IdProposal_MediaTypeNotInId()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        var d = s.AvailableIncoming.Single();
        s.SetDraftMediaType(d.CandidateId, "cd");
        Assert.DoesNotContain("cd", s.EffectiveIdFor(d));
    }

    [Fact]
    public void IdProposal_SameChainCollision_BlocksContinue_NoAutoHashOrFilename()
    {
        var s = NewGroup(Discovery("/src", new[]
        {
            Leaf("Animations/[D64]/Commodore C64 - Animations - [D64] (a).dat"),
            Leaf("Animations/[D64]/Commodore C64 - Animations - [D64] (b).dat"),
        }));
        s.ApplyDefaultMediaTypeToUnresolved("other");
        var list = s.Proposals.ToList();
        Assert.All(list, c => Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(c)));

        // Both proposals share the id ⇒ duplicate; no automatic hash/filename/truncation resolves it.
        var summary = s.SummarizeProposals();
        Assert.Equal(2, summary.DuplicateFinalId);
        Assert.False(s.CanBuildPlan);

        // Resolvable by a short manual Dat Suffix on one of them.
        list[1].DatToken = "b";
        Assert.Equal("c64-tosec-animations-d64-b", s.EffectiveIdFor(list[1]));
        Assert.Equal(0, s.SummarizeProposals().DuplicateFinalId);
    }

    [Fact]
    public void ManualFinalId_NotOverwrittenByTokenChanges()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/x.dat") }));
        var d = s.AvailableIncoming.Single();
        s.SetManualFinalId(d.CandidateId, "c64-tosec-manual");
        Assert.True(d.IsFinalIdManual);
        d.DatToken = "renamed";
        s.FolderTree!.NodeForFolder("A")!.Token = "alpha";
        Assert.Equal("c64-tosec-manual", s.EffectiveIdFor(d));
        s.ClearManualFinalId(d.CandidateId);
        Assert.Contains("alpha", s.EffectiveIdFor(d));   // tracks tokens again
    }

    [Fact]
    public void FolderTokenSuggestion_IsAtomic_NoInternalSeparators()
    {
        Assert.Equal("animations",   FolderTokenTree.SuggestFolderToken("Animations"));
        Assert.Equal("d64",          FolderTokenTree.SuggestFolderToken("[D64]"));
        Assert.Equal("nbz",          FolderTokenTree.SuggestFolderToken("[NBZ]"));
        Assert.Equal("lnx",          FolderTokenTree.SuggestFolderToken("[LNX]"));
        Assert.Equal("testdisks",    FolderTokenTree.SuggestFolderToken("Test Disks"));
        Assert.Equal("publicdomain", FolderTokenTree.SuggestFolderToken("Public Domain"));
        Assert.Equal("diskimages",   FolderTokenTree.SuggestFolderToken("Disk Images"));
        Assert.Equal("testdisks",    FolderTokenTree.SuggestFolderToken("test_disks"));
        Assert.DoesNotContain("_", FolderTokenTree.SuggestFolderToken("Test_Disks"));
        Assert.DoesNotContain("-", FolderTokenTree.SuggestFolderToken("Test Disks"));
    }

    [Fact]
    public void FolderChainId_AtomicSegments_HyphenOnlyBetweenComponents()
    {
        var file = "Applications/Test Disks/[NBZ]/file.dat";
        var s = NewGroup(Discovery("/src", new[] { Leaf(file) }));
        var id = s.EffectiveIdFor(s.AvailableIncoming.Single());

        Assert.Equal("c64-tosec-applications-testdisks-nbz", id);
        Assert.DoesNotContain("test-disks", id);
        Assert.DoesNotContain("_", id);
        Assert.Equal(new[] { "c64", "tosec", "applications", "testdisks", "nbz" }, id.Split('-'));
    }

    [Fact]
    public void EditingFolderToken_UpdatesOnlyDescendantProposals()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/x.dat"), Leaf("C/z.dat") }));
        var ax = s.AvailableIncoming.Single(c => c.RelativePath == "A/x.dat");
        var cz = s.AvailableIncoming.Single(c => c.RelativePath == "C/z.dat");
        var beforeCz = s.EffectiveIdFor(cz);
        s.FolderTree!.NodeForFolder("A")!.Token = "alpha";
        Assert.Contains("alpha", s.EffectiveIdFor(ax));
        Assert.Equal(beforeCz, s.EffectiveIdFor(cz));
    }

    // ── Explicit identity contract ──────────────────────────────────────────────

    [Fact]  // (1) System ID from the caller and immutable
    public void SystemId_ReceivedFromCaller_AndImmutable()
    {
        var s = GroupDatReconciliationSession.ForNewGroup(Catalog(), "c64", "C64", "tosec", "TOSEC", "c64-tosec");
        Assert.Equal("c64", s.SystemId);
        Assert.Equal("C64", s.SystemName);
        var t = typeof(GroupDatReconciliationSession);
        Assert.Null(t.GetProperty("SystemId")!.SetMethod);
        Assert.Null(t.GetProperty("SystemName")!.SetMethod);
        Assert.Null(t.GetProperty("HardwareFamilyId")!.SetMethod);
        Assert.NotNull(t.GetMethod("ForNewGroup"));
        Assert.NotNull(t.GetMethod("ForExistingGroup"));
    }

    [Fact]  // (2) Create mode requires Group Name and Group ID
    public void CreateMode_RequiresGroupNameAndGroupId()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }), groupName: "", groupId: "");
        Assert.Contains(s.BlockingReasons(), r => r.Contains("Group name is required"));
        Assert.Contains(s.BlockingReasons(), r => r.Contains("Group id is required"));
        s.SetGroupName("TOSEC");
        s.SetGroupId("c64-tosec");
        Assert.DoesNotContain(s.BlockingReasons(), r => r.Contains("Group name is required"));
        Assert.DoesNotContain(s.BlockingReasons(), r => r.Contains("Group id is required"));
    }

    [Fact]  // (3) Group ID suggested from c64 + tosec is c64-tosec
    public void GroupId_SuggestedFromSystemAndAuthority()
    {
        Assert.Equal("c64-tosec", GroupDatReconciliationSession.SuggestGroupId("c64", "tosec"));
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));
        Assert.Equal("c64-tosec", s.GroupId);
        Assert.Equal("TOSEC", s.GroupName);
    }

    [Fact]  // (4) Group ID can be edited before creation
    public void GroupId_EditableBeforeCreation()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));
        Assert.Equal("c64-tosec", s.GroupId);
        s.SetGroupId("c64-tosec-collection");
        Assert.Equal("c64-tosec-collection", s.GroupId);
    }

    [Fact]  // (5) collision (case-insensitive) blocks Create, no auto-suffix, no auto-switch to Update
    public void GroupIdCollision_BlocksCreate_NoAutoSuffix_NoModeSwitch()
    {
        var existing = new GroupDatExistingGroup("C64-TOSEC", "C64 TOSEC", "c64", "tosec", 0,
            ImmutableArray<GroupDatExistingLeaf>.Empty);
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }),
            cat: Catalog(groups: new[] { existing }), groupId: "c64-tosec");

        Assert.Equal(GroupDatReconciliationMode.NewGroup, s.Mode);   // stays New — never auto-switches
        Assert.Equal("c64-tosec", s.GroupId);                        // never auto-suffixed
        Assert.Contains("already exists", s.GroupIdBlockingReason());
        Assert.False(s.CanBuildPlan);

        s.SetGroupId("c64-tosec-2");                                 // user picks a different id
        Assert.Null(s.GroupIdBlockingReason());
    }

    [Fact]  // (6) two groups of the same authority are allowed with different Group IDs
    public void SameAuthority_DifferentGroupIds_Allowed()
    {
        var existing = new GroupDatExistingGroup("c64-tosec", "TOSEC", "c64", "tosec", 0,
            ImmutableArray<GroupDatExistingLeaf>.Empty);
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }),
            cat: Catalog(groups: new[] { existing }), authority: "tosec", groupId: "c64-tosec-demos");

        Assert.Equal(GroupDatReconciliationMode.NewGroup, s.Mode);
        Assert.Equal("tosec", s.Authority);
        Assert.Null(s.GroupIdBlockingReason());   // different id ⇒ allowed even though authority matches
    }

    [Fact]  // (7) Update opened via a specific Group ID, not via authority
    public void UpdateMode_OpenedByExistingGroupId()
    {
        var group = new GroupDatExistingGroup("c64-tosec", "TOSEC", "c64", "tosec", 0,
            ImmutableArray.Create(ExLeaf("c64-tosec-keep")));
        var s = GroupDatReconciliationSession.ForExistingGroup(
            Catalog(new[] { "c64-tosec-keep" }, new[] { group }), "c64-tosec");

        Assert.Equal(GroupDatReconciliationMode.UpdateGroup, s.Mode);
        Assert.Equal("c64-tosec", s.GroupId);
        Assert.Equal("TOSEC", s.GroupName);
        Assert.Equal("c64", s.SystemId);
        Assert.Equal("c64-tosec-keep", s.AvailableLeaves.Single().DatLineId);
        Assert.Throws<InvalidOperationException>(
            () => GroupDatReconciliationSession.ForExistingGroup(Catalog(), "no-such-group"));
    }

    [Fact]  // (8) existing Group ID / identity is read-only
    public void UpdateMode_IdentityReadOnly()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("A/1.dat") }), "c64-tosec-keep");
        Assert.True(s.IsIdentityLocked);
        Assert.Throws<InvalidOperationException>(() => s.SetGroupId("x"));
        Assert.Throws<InvalidOperationException>(() => s.SetGroupName("x"));
        Assert.Throws<InvalidOperationException>(() => s.SetAuthority("x"));
    }

    [Fact]  // (9) Group Name and Group ID stay distinct in the plan
    public void Plan_KeepsGroupNameAndGroupIdDistinct()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));
        s.SetGroupName("My C64 Collection");
        s.SetGroupId("c64-tosec");
        ResolveAllNew(s);
        var plan = s.BuildPlan();
        Assert.Equal("c64-tosec", plan.GroupId);
        Assert.Equal("My C64 Collection", plan.GroupName);
        Assert.NotEqual(plan.GroupId, plan.GroupName);
        Assert.Equal("c64", plan.SystemId);
        Assert.Equal("tosec", plan.Authority);
    }

    [Fact]  // (10) changing Group Name does not change proposed leaf ids
    public void ChangingGroupName_DoesNotChangeLeafIds()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        var c = s.AvailableIncoming.Single();
        var before = s.EffectiveIdFor(c);
        s.SetGroupName("Totally Different Name");
        Assert.Equal(before, s.EffectiveIdFor(c));
        Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(c));
    }

    [Fact]  // (11) changing Group ID updates only non-manually-overridden proposals
    public void ChangingGroupId_UpdatesOnlyAutomaticProposals()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/x.dat"), Leaf("B/y.dat") }));
        var a = s.AvailableIncoming.Single(c => c.RelativePath == "A/x.dat");
        var b = s.AvailableIncoming.Single(c => c.RelativePath == "B/y.dat");
        s.SetManualFinalId(a.CandidateId, "c64-tosec-manual-a");
        Assert.Equal("c64-tosec-b", s.EffectiveIdFor(b));

        s.SetGroupId("c64-demos");
        Assert.Equal("c64-tosec-manual-a", s.EffectiveIdFor(a));   // manual override untouched
        Assert.Equal("c64-demos-b", s.EffectiveIdFor(b));          // automatic proposal re-prefixed
    }

    [Fact]  // (12) leaf id uses the Group ID as prefix
    public void LeafId_UsesGroupIdAsPrefix()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }), groupId: "c64-tosec");
        Assert.StartsWith("c64-tosec-", s.EffectiveIdFor(s.AvailableIncoming.Single()));
        s.SetGroupId("myprefix");
        Assert.StartsWith("myprefix-", s.EffectiveIdFor(s.AvailableIncoming.Single()));
    }

    [Fact]  // (bonus) authority re-suggests Group ID / Group Name only when not manually overridden
    public void ChangingAuthority_ReSuggestsIdentity_UnlessManuallyOverridden()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));
        Assert.Equal("c64-tosec", s.GroupId);
        Assert.Equal("TOSEC", s.GroupName);

        s.SetAuthority("nointro");
        Assert.Equal("c64-nointro", s.GroupId);       // re-suggested from new authority
        Assert.Equal("C64 No-Intro", s.GroupName);    // re-suggested: model (SystemName) + authority

        s.SetGroupId("custom-id");                  // manual override
        s.SetAuthority("tosec");
        Assert.Equal("custom-id", s.GroupId);       // manual id not overwritten
    }

    // ── Group Name suggestion (Manufacturer + Model + Authority) ─────────────────

    private static GroupDatReconciliationSession NewGroupCtx(
        string systemId, string systemName, string manufacturer, string authority)
    {
        var cat = Catalog();
        var authDisplay = cat.Authorities.FirstOrDefault(a => a.Id == authority)?.Name ?? authority;
        var gid   = GroupDatReconciliationSession.SuggestGroupId(systemId, authority);
        var gname = GroupDatReconciliationSession.ComposeGroupName(manufacturer, systemName, authDisplay);
        return GroupDatReconciliationSession.ForNewGroup(cat, systemId, systemName, authority, gname, gid, manufacturer);
    }

    [Fact]  // (1) Commodore + 64 + TOSEC → "Commodore 64 TOSEC"
    public void ComposeGroupName_ManufacturerModelAuthority()
        => Assert.Equal("Commodore 64 TOSEC",
            GroupDatReconciliationSession.ComposeGroupName("Commodore", "64", "TOSEC"));

    [Fact]  // (2) system id + authority id → group id
    public void SuggestGroupId_SystemAndAuthority()
        => Assert.Equal("c64-tosec", GroupDatReconciliationSession.SuggestGroupId("c64", "tosec"));

    [Fact]  // (3) manufacturer absent → Model + Authority
    public void ComposeGroupName_NoManufacturer_UsesModelAndAuthority()
        => Assert.Equal("Amiga 500 TOSEC",
            GroupDatReconciliationSession.ComposeGroupName("", "Amiga 500", "TOSEC"));

    [Fact]  // (4) descriptive data absent → Authority only
    public void ComposeGroupName_NoDescriptiveData_UsesAuthority()
        => Assert.Equal("TOSEC", GroupDatReconciliationSession.ComposeGroupName("", "", "TOSEC"));

    [Fact]  // (5) model already carries the manufacturer → no double manufacturer
    public void ComposeGroupName_NoDoubleManufacturer()
        => Assert.Equal("Commodore 64 TOSEC",
            GroupDatReconciliationSession.ComposeGroupName("Commodore", "Commodore 64", "TOSEC"));

    [Fact]  // (6) whitespace normalized
    public void ComposeGroupName_NormalizesWhitespace()
        => Assert.Equal("Commodore 64 TOSEC",
            GroupDatReconciliationSession.ComposeGroupName("  Commodore ", "  64  ", " TOSEC "));

    [Fact]  // (7) changing authority re-suggests both non-overridden values
    public void GroupNameSuggestion_ChangingAuthority_UpdatesNonOverridden()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        Assert.Equal("Commodore 64 TOSEC", s.GroupName);
        Assert.Equal("c64-tosec", s.GroupId);

        s.SetAuthority("nointro");
        Assert.Equal("Commodore 64 No-Intro", s.GroupName);
        Assert.Equal("c64-nointro", s.GroupId);
    }

    [Fact]  // (8) manual Group Name preserved across authority change
    public void GroupNameSuggestion_ManualNamePreserved()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetGroupName("My Custom Collection");
        s.SetAuthority("nointro");
        Assert.Equal("My Custom Collection", s.GroupName);   // manual name kept
        Assert.Equal("c64-nointro", s.GroupId);              // id still auto
    }

    [Fact]  // (9) manual Group ID preserved; name still re-suggested
    public void GroupIdSuggestion_ManualIdPreserved()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetGroupId("c64-custom");
        s.SetAuthority("nointro");
        Assert.Equal("c64-custom", s.GroupId);                 // manual id kept
        Assert.Equal("Commodore 64 No-Intro", s.GroupName);    // name re-suggested
    }

    [Fact]  // (10) Update mode shows persisted identity, never regenerated
    public void UpdateMode_KeepsPersistedIdentity_NoRegeneration()
    {
        var group = new GroupDatExistingGroup("c64-tosec", "Persisted Group Name", "c64", "tosec", 0,
            ImmutableArray.Create(ExLeaf("c64-tosec-keep")));
        var s = GroupDatReconciliationSession.ForExistingGroup(
            Catalog(new[] { "c64-tosec-keep" }, new[] { group }), "c64-tosec");
        Assert.Equal("Persisted Group Name", s.GroupName);   // persisted display_name, not composed
        Assert.Equal("c64-tosec", s.GroupId);
        Assert.Equal("tosec", s.Authority);
        Assert.Equal("", s.Manufacturer);                    // manufacturer not sourced in Update mode
    }

    [Fact]  // (11) Group Name never affects leaf ids
    public void GroupName_DoesNotAffectLeafIds_WithManufacturerContext()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        var c = s.AvailableIncoming.Single();
        var before = s.EffectiveIdFor(c);
        s.SetGroupName("Whatever New Name");
        Assert.Equal(before, s.EffectiveIdFor(c));
        Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(c));
    }

    [Fact]  // (12) Group ID prefixes the leaf ids
    public void GroupId_PrefixesLeafIds_WithManufacturerContext()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        Assert.StartsWith("c64-tosec-", s.EffectiveIdFor(s.AvailableIncoming.Single()));
    }

    // ── Identity suggestion lifecycle + explicit reset ───────────────────────────

    [Fact]  // (1)(2) initial suggestion applied and NOT marked manual
    public void Init_AppliesSuggestion_NotMarkedManual()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        Assert.Equal("Commodore 64 TOSEC", s.GroupName);
        Assert.Equal("c64-tosec", s.GroupId);
        Assert.False(s.IsGroupNameManual);
        Assert.False(s.IsGroupIdManual);
        Assert.False(s.IsIdentityCustom);
    }

    [Fact]  // (3)(6) auto authority change updates both auto-managed fields and does not mark manual
    public void AuthorityChange_UpdatesAutoManaged_WithoutMarkingManual()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetAuthority("nointro");
        Assert.Equal("Commodore 64 No-Intro", s.GroupName);
        Assert.Equal("c64-nointro", s.GroupId);
        Assert.False(s.IsIdentityCustom);   // programmatic re-suggestion is not a user override
    }

    [Fact]  // (4)(5) manual edits set the flags and survive an authority change
    public void ManualEdits_MarkCustom_AndSurviveAuthorityChange()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetGroupName("My Custom Name");
        s.SetGroupId("c64-custom");
        Assert.True(s.IsGroupNameManual);
        Assert.True(s.IsGroupIdManual);
        Assert.True(s.IsIdentityCustom);

        s.SetAuthority("nointro");
        Assert.Equal("My Custom Name", s.GroupName);   // preserved
        Assert.Equal("c64-custom", s.GroupId);         // preserved
    }

    [Fact]  // (7) Apply suggested identity replaces both overrides and returns to auto-managed
    public void ResetIdentity_ReplacesBothOverrides()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetGroupName("My Custom Name");
        s.SetGroupId("c64-custom");

        s.ResetIdentityToSuggested();
        Assert.Equal("Commodore 64 TOSEC", s.GroupName);
        Assert.Equal("c64-tosec", s.GroupId);
        Assert.False(s.IsIdentityCustom);
    }

    [Fact]  // (8) after reset, a further authority change re-suggests both again
    public void ResetIdentity_ThenAuthorityChange_UpdatesBothAgain()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetGroupId("c64-custom");
        s.ResetIdentityToSuggested();
        s.SetAuthority("nointro");
        Assert.Equal("Commodore 64 No-Intro", s.GroupName);
        Assert.Equal("c64-nointro", s.GroupId);
    }

    [Fact]  // (9) reset re-prefixes leaf proposals that have no manual Final ID
    public void ResetIdentity_UpdatesNonOverriddenLeafProposals()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        s.SetGroupId("c64-custom");
        var c = s.AvailableIncoming.Single();
        Assert.Equal("c64-custom-animations-d64", s.EffectiveIdFor(c));

        s.ResetIdentityToSuggested();
        Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(c));   // re-prefixed to the suggestion
    }

    [Fact]  // (10) reset never overwrites a manual Final ID
    public void ResetIdentity_PreservesManualFinalId()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }));
        s.SetGroupId("c64-custom");
        var c = s.AvailableIncoming.Single();
        s.SetManualFinalId(c.CandidateId, "c64-hand-picked-id");

        s.ResetIdentityToSuggested();
        Assert.Equal("c64-hand-picked-id", s.EffectiveIdFor(c));   // manual Final ID untouched
    }

    [Fact]  // (15) Create mode has no decisions, so identity stays editable/resettable until Continue
    public void CreateMode_IdentityStaysEditable_WithProposals()
    {
        var s = NewGroupCtx("c64", "Commodore 64", "Commodore", "tosec");
        s.SetDiscovery(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat") }));
        s.ApplyDefaultMediaTypeToUnresolved("other");   // proposals fully configured

        Assert.Empty(s.Decisions);          // Create never produces decisions
        Assert.True(s.CanResetIdentity);    // identity never locked by proposal configuration
        s.SetGroupId("c64-custom");         // still editable
        Assert.Equal("c64-custom", s.GroupId);
    }

    [Fact]  // (12) Update mode: identity read-only, no reset
    public void ResetIdentity_UpdateMode_NotAvailable_AndThrows()
    {
        var group = new GroupDatExistingGroup("c64-tosec", "Persisted Name", "c64", "tosec", 0,
            ImmutableArray.Create(ExLeaf("c64-tosec-keep")));
        var s = GroupDatReconciliationSession.ForExistingGroup(
            Catalog(new[] { "c64-tosec-keep" }, new[] { group }), "c64-tosec");
        Assert.False(s.CanResetIdentity);
        Assert.Throws<InvalidOperationException>(() => s.ResetIdentityToSuggested());
    }

    // ── New-Group sequential flow ────────────────────────────────────────────────

    [Fact]  // (1)(3)(4) every discovered DAT is an implicit proposal; nothing consumed; no decisions
    public void CreateMode_EveryDatIsImplicitProposal_NothingConsumed_NoDecisions()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat"), Leaf("C/3.dat") }));
        Assert.Equal(3, s.Proposals.Count);
        Assert.Empty(s.AvailableLeaves);                 // no existing leaves in a new group
        Assert.Empty(s.Decisions);                       // no per-DAT decisions in Create mode

        // Create mode has no per-DAT create/associate/absent actions.
        var first = s.Proposals.First();
        Assert.Throws<InvalidOperationException>(() => s.CreateNewLeaf(first.CandidateId, s.EffectiveIdFor(first), "other"));
        Assert.Throws<InvalidOperationException>(() => s.MarkLeafAbsent("x"));

        // Selecting/editing a proposal never removes it from the list.
        first.DatToken = "z";
        Assert.Equal(3, s.Proposals.Count);
        Assert.Empty(s.Decisions);
    }

    [Fact]  // (7)(8)(9) blocked until all proposals valid; then one NewLeaf per proposal
    public void CreateMode_BlockedUntilAllProposalsValid_ThenPlanHasAllLeaves()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat") }));
        Assert.False(s.CanBuildPlan);
        Assert.Contains(s.BlockingReasons(), r => r.Contains("require attention"));

        ResolveAllNew(s, "other");
        Assert.True(s.CanBuildPlan);

        var plan = s.BuildPlan();
        Assert.Equal(2, plan.NewLeaves.Length);
        Assert.All(plan.NewLeaves, n => Assert.Equal("other", n.MediaTypeId));
        Assert.Contains(plan.NewLeaves, n => n.LeafId == "c64-tosec-a");
        Assert.Contains(plan.NewLeaves, n => n.LeafId == "c64-tosec-b");
    }

    [Fact]  // (1)(9) 410 discovered DATs → 410 proposals → 410 NewLeaf plan entries
    public void CreateMode_ManyDats_ProduceOneLeafPerDat()
    {
        var leaves = Enumerable.Range(0, 410).Select(i => Leaf($"Set{i:D3}/[D64]/game.dat")).ToArray();
        var s = NewGroup(Discovery("/src", leaves));
        Assert.Equal(410, s.Proposals.Count);

        s.ApplyDefaultMediaTypeToUnresolved("floppy");   // one bulk action makes every proposal valid
        Assert.True(s.CanBuildPlan);

        var plan = s.BuildPlan();
        Assert.Equal(410, plan.NewLeaves.Length);
        Assert.All(plan.NewLeaves, n => Assert.Equal("floppy", n.MediaTypeId));
    }

    [Fact]
    public void NewGroup_ParseFailureBlocks()
    {
        var s = NewGroup(Discovery("/src",
            new[] { Leaf("Bad.dat", status: DiscoveredDatLeafStatus.ParseFailed) },
            new[] { new DatGroupDiscoveryDiagnostic("dat-parse-failed", DatGroupDiscoveryDiagnosticSeverity.Error, "bad", "Bad.dat") }));
        Assert.Empty(s.AvailableIncoming);                 // failed DAT is not a candidate
        Assert.False(s.CanBuildPlan);
        Assert.Contains(s.BlockingReasons(), r => r.Contains("blocking errors"));
    }

    [Fact]  // (12) catalog collision (case-insensitive) marks the proposal and blocks Continue
    public void CreateMode_OccupiedCatalogId_CaseInsensitive_BlocksContinue()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }),
            cat: Catalog(new[] { "C64-TOSEC-Animations-D64" }));   // legacy mixed-case occupied leaf id
        s.ApplyDefaultMediaTypeToUnresolved("other");
        var c = s.Proposals.Single();
        Assert.Equal(GroupDatReconciliationSession.LeafProposalIssue.CatalogCollision, s.EvaluateProposal(c));
        Assert.Equal(1, s.SummarizeProposals().CatalogCollision);
        Assert.False(s.CanBuildPlan);
    }

    // ── Update-Group one-to-one flow ─────────────────────────────────────────────

    [Fact]
    public void UpdateGroup_ConsumeUndo_AndNewLeaf()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("Keep.dat"), Leaf("New.dat") }), "c64-tosec-keep");
        var keep = s.AvailableIncoming.Single(c => c.RelativePath == "Keep.dat");
        var d = s.AssociateUpdate(keep.CandidateId, "c64-tosec-keep");
        Assert.Empty(s.AvailableLeaves);
        s.Undo(d.DecisionId);
        Assert.Single(s.AvailableLeaves);

        s.AssociateUpdate(keep.CandidateId, "c64-tosec-keep");
        var newDat = s.AvailableIncoming.Single(c => c.RelativePath == "New.dat");
        var nl = s.CreateNewLeaf(newDat.CandidateId, "c64-tosec-new", "other");
        Assert.Equal(GroupDatDecisionKind.NewLeaf, nl.Kind);
    }

    [Fact]
    public void UpdateGroup_NewLeaf_UsesExistingGroupIdPrefix()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("Extra/[D64]/x.dat") }), "c64-tosec-keep");
        var dat = s.AvailableIncoming.Single();
        Assert.StartsWith("c64-tosec-", s.EffectiveIdFor(dat));   // prefixed by the existing Group ID
    }

    [Fact]
    public void UpdateGroup_UpdateKeepsExistingMediaType_AbsentRetains()
    {
        var group = new GroupDatExistingGroup(Gid, Gname, SysId, Auth, 0,
            ImmutableArray.Create(ExLeaf("c64-tosec-keep", media: "cd"), ExLeaf("c64-tosec-gone")));
        var s = GroupDatReconciliationSession.ForExistingGroup(
            Catalog(new[] { "c64-tosec-keep", "c64-tosec-gone" }, new[] { group }), Gid);
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Keep.dat") }));

        var keep = s.AvailableIncoming.Single();
        var upd = s.AssociateUpdate(keep.CandidateId, "c64-tosec-keep");
        Assert.Equal("cd", upd.MediaTypeId);
        var abs = s.MarkLeafAbsent("c64-tosec-gone");
        Assert.Equal(GroupDatDecisionKind.Absent, abs.Kind);

        var plan = s.BuildPlan();
        Assert.Single(plan.Updates);
        Assert.Single(plan.AbsentLeaves);
        Assert.Equal("c64-tosec-gone", plan.AbsentLeaves[0].ExistingLeafId);
    }

    [Fact]
    public void UpdateGroup_Associate_ConsumesBothSets()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("Keep.dat") }), "c64-tosec-keep");
        var dat = s.AvailableIncoming.Single();
        s.AssociateUpdate(dat.CandidateId, "c64-tosec-keep");
        Assert.Empty(s.AvailableIncoming);
        Assert.Empty(s.AvailableLeaves);
    }

    [Fact]
    public void UpdateGroup_NewLeaf_ConsumesOnlyDat()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("New.dat") }), "c64-tosec-keep");
        var dat = s.AvailableIncoming.Single();
        s.CreateNewLeaf(dat.CandidateId, "c64-tosec-new", "other");
        Assert.Empty(s.AvailableIncoming);
        Assert.Single(s.AvailableLeaves);
    }

    [Fact]
    public void UpdateGroup_Absent_ConsumesOnlyLeaf()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("New.dat") }), "c64-tosec-gone");
        s.MarkLeafAbsent("c64-tosec-gone");
        Assert.Empty(s.AvailableLeaves);
        Assert.Single(s.AvailableIncoming);
    }

    [Fact]
    public void UpdateGroup_DoubleConsume_Rejected()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("A.dat"), Leaf("B.dat") }), "c64-tosec-x", "c64-tosec-y");
        var a = s.AvailableIncoming.Single(c => c.RelativePath == "A.dat");
        var b = s.AvailableIncoming.Single(c => c.RelativePath == "B.dat");
        s.AssociateUpdate(a.CandidateId, "c64-tosec-x");
        Assert.Throws<InvalidOperationException>(() => s.AssociateUpdate(a.CandidateId, "c64-tosec-y"));
        Assert.Throws<InvalidOperationException>(() => s.AssociateUpdate(b.CandidateId, "c64-tosec-x"));
    }

    [Fact]
    public void UpdateGroup_ForeignElements_Rejected()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("A.dat") }), "c64-tosec-keep");
        Assert.Throws<InvalidOperationException>(() => s.CreateNewLeaf("not-a-candidate", "c64-tosec-z", "other"));
        Assert.Throws<InvalidOperationException>(() => s.AssociateUpdate("not-a-candidate", "c64-tosec-keep"));
        Assert.Throws<InvalidOperationException>(() => s.MarkLeafAbsent("no-such-leaf"));
    }

    [Fact]
    public void UpdateGroup_ChangingSource_ClearsDecisions()
    {
        var s = UpdateGroup(Discovery("/src1", new[] { Leaf("Keep.dat") }), "c64-tosec-keep");
        var dat = s.AvailableIncoming.Single();
        s.AssociateUpdate(dat.CandidateId, "c64-tosec-keep");
        Assert.Single(s.Decisions);

        s.SetDiscovery(Discovery("/src2", new[] { Leaf("Other.dat") }));
        Assert.Empty(s.Decisions);
        Assert.Single(s.AvailableLeaves);
        Assert.Equal("Other.dat", s.AvailableIncoming.Single().RelativePath);
    }

    // ── Reset / independence ─────────────────────────────────────────────────────

    [Fact]
    public void ChangingSource_RecreatesIncoming_OnlyNewSource()
    {
        var s = NewGroup(Discovery("/src1", new[] { Leaf("Old/A.dat") }));
        Assert.Single(s.AvailableIncoming);

        s.SetDiscovery(Discovery("/src2", new[] { Leaf("New/B.dat"), Leaf("New/C.dat") }));
        Assert.Equal(2, s.AvailableIncoming.Count);
        Assert.DoesNotContain(s.AvailableIncoming, c => c.RelativePath.Contains("Old"));
        Assert.Null(s.FolderTree!.NodeForFolder("Old"));
    }

    [Fact]
    public void DifferentGroups_IndependentSessions_NoLeakage()
    {
        var disc = Discovery("/src", new[] { Leaf("Games.dat") });
        var groupA = new GroupDatExistingGroup("c64-tosec", "TOSEC", "c64", "tosec", 0,
            ImmutableArray.Create(ExLeaf("c64-tosec-a")));
        var a = GroupDatReconciliationSession.ForExistingGroup(Catalog(new[] { "c64-tosec-a" }, new[] { groupA }), "c64-tosec");
        a.SetDiscovery(disc);
        a.MarkLeafAbsent("c64-tosec-a");
        Assert.Single(a.Decisions);

        var groupB = new GroupDatExistingGroup("nes-nointro", "No-Intro", "nes", "nointro", 0,
            ImmutableArray.Create(new GroupDatExistingLeaf("nes-nointro-b", "nes-nointro", null, null, "1", 5, "other", "nes", "nointro", 0)));
        var b = GroupDatReconciliationSession.ForExistingGroup(Catalog(new[] { "nes-nointro-b" }, new[] { groupB }), "nes-nointro");
        b.SetDiscovery(disc);
        Assert.Empty(b.Decisions);
        Assert.Equal("nes-nointro-b", b.AvailableLeaves.Single().DatLineId);
        Assert.Throws<InvalidOperationException>(() => b.MarkLeafAbsent("c64-tosec-a"));
    }

    // ── Plan immutability / gating ───────────────────────────────────────────────

    [Fact]
    public void Plan_IsImmutable_CarriesSnapshotAndSourcePaths()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));
        ResolveAllNew(s, "other");
        var plan = s.BuildPlan();
        Assert.IsType<ImmutableArray<GroupDatNewLeafPlan>>(plan.NewLeaves);
        Assert.Equal("/src/A/1.dat", plan.NewLeaves[0].SourcePath);
        Assert.Equal("A/1.dat", plan.NewLeaves[0].SourceRelativePath);
        Assert.IsType<ImmutableArray<DiscoveredDatGame>>(plan.DiscoverySnapshot[0].Games);
    }

    [Fact]
    public void BuildPlan_ThrowsWhenIncomplete()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));   // DAT not resolved
        Assert.Throws<InvalidOperationException>(() => s.BuildPlan());
    }

    [Fact]
    public void FinalId_Over64_IsInvalid_NotTruncated()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));
        var longId = "c64-tosec-" + new string('a', 70);
        var ev = s.EvaluateNewLeafId(longId);
        Assert.False(ev.IsValid);
        Assert.Equal(DatTechnicalIdError.TooLong, ev.PolicyError);
        Assert.Equal(longId, ev.Id);
    }

    [Fact]
    public void FolderTree_EmptyToken_ExcludesFolder_NodeKept()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Commodore/C64/x.dat") }));
        var d = s.AvailableIncoming.Single();
        Assert.Equal("c64-tosec-commodore-c64", s.EffectiveIdFor(d));
        s.FolderTree!.NodeForFolder("Commodore")!.Token = "";
        Assert.Equal("c64-tosec-c64", s.EffectiveIdFor(d));
        Assert.NotNull(s.FolderTree.NodeForFolder("Commodore"));
    }

    // ── Apply default media type to remaining DATs ───────────────────────────────

    [Fact]  // (1) no separate default-media combo/state; single leaf combo + adjacent apply button
    public void Dialog_HasNoSeparateDefaultMediaCombo_ButtonSitsWithLeafCombo()
    {
        var fields = typeof(GroupDatReconciliationDialog)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("DefaultMediaTypeCombo", fields);   // redundant second combo removed
        Assert.DoesNotContain("DefaultMediaStatusText", fields);  // left-column status line removed
        Assert.Null(typeof(GroupDatReconciliationDialog).GetMethod(
            "OnDefaultMediaTypeChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));

        Assert.Contains("MediaTypeCombo", fields);                // the leaf editor combo (value source)
        Assert.Contains("ApplyDefaultMediaButton", fields);       // apply button lives beside it
    }

    [Fact]  // (5)(6) apply propagates the selected leaf's value to OTHER proposals; source kept; no decision/consume
    public void Apply_FromSelectedLeaf_PropagatesToOthers_SourceKept_NoDecision()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat"), Leaf("C/3.dat") }));
        var source = s.Proposals.Single(c => c.RelativePath == "A/1.dat");
        s.SetDraftMediaType(source.CandidateId, "floppy");   // value shown in the selected leaf's combo

        var (updated, preserved) = s.ApplyDefaultMediaTypeToUnresolved("floppy", source.CandidateId);

        Assert.Equal(2, updated);      // B and C — the source is NOT counted
        Assert.Equal(0, preserved);
        Assert.Equal("floppy", source.DraftMediaTypeId);   // source leaf keeps its value
        foreach (var other in s.Proposals.Where(c => c.CandidateId != source.CandidateId))
            Assert.Equal("floppy", other.DraftMediaTypeId);
        Assert.Empty(s.Decisions);                 // no decision created
        Assert.Equal(3, s.Proposals.Count);        // nothing consumed
        Assert.True(s.CanBuildPlan);               // all proposals now valid ⇒ Continue enabled
    }

    [Fact]  // (7)(16) other manual overrides preserved; source excluded from both counts (PO example)
    public void Apply_PreservesOtherManualOverrides_ExcludesSourceFromCounts()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat"), Leaf("C/3.dat") }));
        var a = s.AvailableIncoming.Single(c => c.RelativePath == "A/1.dat");   // selected source
        var b = s.AvailableIncoming.Single(c => c.RelativePath == "B/2.dat");
        var c = s.AvailableIncoming.Single(x => x.RelativePath == "C/3.dat");
        s.SetDraftMediaType(a.CandidateId, "floppy");   // source value
        s.SetDraftMediaType(b.CandidateId, "tape");     // manual override on another leaf

        var (updated, preserved) = s.ApplyDefaultMediaTypeToUnresolved("floppy", a.CandidateId);

        Assert.Equal(1, updated);            // only C
        Assert.Equal(1, preserved);          // only B (source A not counted)
        Assert.Equal("floppy", a.DraftMediaTypeId);   // source kept
        Assert.Equal("tape",   b.DraftMediaTypeId);   // manual override preserved
        Assert.Equal("floppy", c.DraftMediaTypeId);   // re-defaulted
    }

    [Fact]  // media type flows from the proposal into the plan (no per-DAT decision in Create)
    public void CreateMode_MediaType_EntersPlanFromProposal()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat") }));
        s.ApplyDefaultMediaTypeToUnresolved("floppy");   // all proposals
        Assert.Empty(s.Decisions);              // no decisions in Create mode

        var plan = s.BuildPlan();
        Assert.All(plan.NewLeaves, n => Assert.Equal("floppy", n.MediaTypeId));
        Assert.Contains(plan.NewLeaves, n => n.LeafId == "c64-tosec-b");
    }

    [Fact]  // Update mode: a confirmed new-leaf decision does not change after a later apply
    public void UpdateGroup_ConfirmedDecision_UnchangedByLaterApply()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("New.dat"), Leaf("Extra.dat") }), "c64-tosec-keep");
        var newDat = s.AvailableIncoming.Single(c => c.RelativePath == "New.dat");
        var decision = s.CreateNewLeaf(newDat.CandidateId, "c64-tosec-new", "floppy");

        var extra = s.AvailableIncoming.Single(c => c.RelativePath == "Extra.dat");
        s.ApplyDefaultMediaTypeToUnresolved("cartridge", extra.CandidateId);
        Assert.Equal("floppy", decision.MediaTypeId);       // confirmed decision unchanged
    }

    [Fact]  // Update mode: Undo restores the decision's media type as a preserved manual choice
    public void UpdateGroup_Undo_RestoresMediaType_ProtectedAsManual()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("New.dat"), Leaf("Extra.dat") }), "c64-tosec-keep");
        var newDat = s.AvailableIncoming.Single(c => c.RelativePath == "New.dat");
        var d = s.CreateNewLeaf(newDat.CandidateId, "c64-tosec-new", "tape");

        s.Undo(d.DecisionId);
        var restored = s.AvailableIncoming.Single(c => c.RelativePath == "New.dat");
        Assert.Equal("tape", restored.DraftMediaTypeId);    // media preserved from the undone decision
        Assert.True(restored.IsMediaTypeManual);

        var extra = s.AvailableIncoming.Single(c => c.RelativePath == "Extra.dat");
        s.ApplyDefaultMediaTypeToUnresolved("floppy", extra.CandidateId);   // must not overwrite the restored value
        Assert.Equal("tape", restored.DraftMediaTypeId);
    }

    [Fact]  // Update mode: apply never touches existing-leaf media or associations
    public void Apply_UpdateMode_DoesNotChangeExistingLeafMedia()
    {
        var s = UpdateGroup(Discovery("/src", new[] { Leaf("Keep.dat"), Leaf("New.dat"), Leaf("Extra.dat") }), "c64-tosec-keep");
        var keep = s.AvailableIncoming.Single(c => c.RelativePath == "Keep.dat");
        var assoc = s.AssociateUpdate(keep.CandidateId, "c64-tosec-keep");   // existing media = "other"

        var newCand = s.AvailableIncoming.Single(c => c.RelativePath == "New.dat");
        s.ApplyDefaultMediaTypeToUnresolved("floppy", newCand.CandidateId);

        Assert.Equal("other", assoc.MediaTypeId);            // existing-leaf media stays authoritative
        var extra = s.AvailableIncoming.Single(c => c.RelativePath == "Extra.dat");
        Assert.Equal("floppy", extra.DraftMediaTypeId);      // propagated to the other unresolved incoming
    }

    [Fact]  // apply changes only media — never Final ID, folder tokens, or Dat Suffix
    public void Apply_DoesNotChangeIdOrTokensOrSuffix()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat"), Leaf("B/2.dat") }));
        var src = s.AvailableIncoming.Single(c => c.RelativePath == "B/2.dat");
        var c   = s.AvailableIncoming.Single(x => x.RelativePath == "Animations/[D64]/x.dat");
        var idBefore     = s.EffectiveIdFor(c);
        var suffixBefore = c.DatToken;

        s.ApplyDefaultMediaTypeToUnresolved("floppy", src.CandidateId);

        Assert.Equal(idBefore, s.EffectiveIdFor(c));
        Assert.Equal(suffixBefore, c.DatToken);
        Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(c));
    }

    [Fact]  // Create mode: apply never creates decisions or consumes proposals
    public void Apply_CreateMode_KeepsAllProposals_NoDecisions()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat") }));
        var a = s.Proposals.Single(c => c.RelativePath == "A/1.dat");
        s.ApplyDefaultMediaTypeToUnresolved("floppy", a.CandidateId);

        Assert.Equal(2, s.Proposals.Count);     // all rows retained
        Assert.Empty(s.Decisions);              // no decisions
    }

    [Fact]  // guard: an invalid media type is rejected
    public void Apply_InvalidMediaType_Throws()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat") }));
        Assert.Throws<InvalidOperationException>(() => s.ApplyDefaultMediaTypeToUnresolved("not-a-media-type"));
        Assert.Throws<InvalidOperationException>(() => s.ApplyDefaultMediaTypeToUnresolved(""));
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
        MediaTypes       = ImmutableArray.Create(new GroupDatOption("other", "Other"), new GroupDatOption("cd", "CD")),
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

    // Sequentially resolve every remaining incoming DAT into a new leaf (uses the live proposal).
    private static void ResolveAllNew(GroupDatReconciliationSession s, string media = "other")
    {
        foreach (var c in s.AvailableIncoming.ToList())
            s.CreateNewLeaf(c.CandidateId, s.EffectiveIdFor(c), media);
    }

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
    public void IdProposal_SameChainCollision_BlockedAtConfirm_NoAutoHashOrFilename()
    {
        var s = NewGroup(Discovery("/src", new[]
        {
            Leaf("Animations/[D64]/Commodore C64 - Animations - [D64] (a).dat"),
            Leaf("Animations/[D64]/Commodore C64 - Animations - [D64] (b).dat"),
        }));
        var list = s.AvailableIncoming.ToList();
        Assert.All(list, c => Assert.Equal("c64-tosec-animations-d64", s.EffectiveIdFor(c)));

        s.CreateNewLeaf(list[0].CandidateId, s.EffectiveIdFor(list[0]), "other");
        // second DAT proposes the same id → collision at confirm, no automatic hash/filename/truncation
        Assert.True(s.Collides("c64-tosec-animations-d64"));
        Assert.Throws<InvalidOperationException>(
            () => s.CreateNewLeaf(list[1].CandidateId, s.EffectiveIdFor(list[1]), "other"));
        // resolvable by a short manual Dat Suffix
        list[1].DatToken = "b";
        var nl = s.CreateNewLeaf(list[1].CandidateId, s.EffectiveIdFor(list[1]), "other");
        Assert.Equal("c64-tosec-animations-d64-b", nl.FinalId);
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
        Assert.Equal("c64-nointro", s.GroupId);     // re-suggested from new authority
        Assert.Equal("No-Intro", s.GroupName);      // re-suggested from authority display name

        s.SetGroupId("custom-id");                  // manual override
        s.SetAuthority("tosec");
        Assert.Equal("custom-id", s.GroupId);       // manual id not overwritten
    }

    // ── New-Group sequential flow ────────────────────────────────────────────────

    [Fact]
    public void NewGroup_SequentialCreate_OneLeafConsumesOneDat()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat"), Leaf("C/3.dat") }));
        Assert.Equal(3, s.AvailableIncoming.Count);
        Assert.Empty(s.AvailableLeaves);                 // no existing leaves in a new group
        // associate/absent are update-only
        Assert.Throws<InvalidOperationException>(() => s.MarkLeafAbsent("x"));

        var first = s.AvailableIncoming.First();
        s.CreateNewLeaf(first.CandidateId, s.EffectiveIdFor(first), "other");
        Assert.Equal(2, s.AvailableIncoming.Count);      // one consumed
        Assert.Single(s.Decisions);
    }

    [Fact]
    public void NewGroup_PlanBlockedUntilAllResolved()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat") }));
        Assert.False(s.CanBuildPlan);
        Assert.Contains(s.BlockingReasons(), r => r.Contains("not yet resolved"));

        ResolveAllNew(s, "other");
        Assert.True(s.CanBuildPlan);

        var plan = s.BuildPlan();
        Assert.Equal(2, plan.NewLeaves.Length);
        Assert.All(plan.NewLeaves, n => Assert.Equal("other", n.MediaTypeId));
        Assert.Contains(plan.NewLeaves, n => n.LeafId == "c64-tosec-a");
        Assert.Contains(plan.NewLeaves, n => n.LeafId == "c64-tosec-b");
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

    [Fact]  // (14) sequential flow + Undo unchanged
    public void NewGroup_SequentialFlow_UndoRestoresIncoming()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("A/1.dat"), Leaf("B/2.dat") }));
        var first = s.AvailableIncoming.First();
        var d = s.CreateNewLeaf(first.CandidateId, s.EffectiveIdFor(first), "other");
        Assert.Single(s.Decisions);
        Assert.Single(s.AvailableIncoming);
        s.Undo(d.DecisionId);
        Assert.Empty(s.Decisions);
        Assert.Equal(2, s.AvailableIncoming.Count);
    }

    [Fact]
    public void NewGroup_OccupiedCatalogId_CaseInsensitive_BlocksCreate()
    {
        var s = NewGroup(Discovery("/src", new[] { Leaf("Animations/[D64]/x.dat") }),
            cat: Catalog(new[] { "C64-TOSEC-Animations-D64" }));   // legacy mixed-case occupied leaf id
        var c = s.AvailableIncoming.Single();
        Assert.True(s.Collides(s.EffectiveIdFor(c)));
        Assert.Throws<InvalidOperationException>(
            () => s.CreateNewLeaf(c.CandidateId, s.EffectiveIdFor(c), "other"));
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
}

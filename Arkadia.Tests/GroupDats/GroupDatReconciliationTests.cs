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
/// Phase-vertical tests for the pure Group-DAT manual reconciliation model, session, folder-token
/// tree, id composer, and frozen plan. No UI, no DB — the session operates only on an immutable
/// catalog snapshot and a pure discovery result.
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
            DatVersion   = "1",
            DatDate      = "2026-01-01",
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
        HardwareFamilies = ImmutableArray.Create(new GroupDatOption("capcom", "Capcom")),
        MediaTypes       = ImmutableArray.Create(new GroupDatOption("other", "Other"), new GroupDatOption("cd", "CD")),
        Authorities      = ImmutableArray.Create(new GroupDatOption("tosec", "TOSEC")),
        OccupiedLeafIds  = (occupied ?? Array.Empty<string>()).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
    };

    private static GroupDatExistingLeaf ExLeaf(string id, string media = "other") =>
        new(id, "tosec-c64", "old/path.dat", "old.dat", "1", 10, media, "capcom", "tosec", 0);

    private static GroupDatReconciliationSession NewGroupSession(DatGroupDiscoveryResult disc, GroupDatCatalogPreviewData? cat = null)
    {
        var s = GroupDatReconciliationSession.ForNewGroup(cat ?? Catalog());
        s.NewGroupId = "tosec-c64"; s.NewGroupDisplayName = "C64"; s.NewGroupHardwareFamilyId = "capcom"; s.NewGroupAuthority = "tosec";
        s.SetDiscovery(disc);
        return s;
    }

    // ── Consume / undo invariants ───────────────────────────────────────────────

    [Fact]
    public void Update_ConsumesDatAndLeaf()
    {
        var group = new GroupDatExistingGroup("tosec-c64", "C64", "capcom", "tosec", 0,
            ImmutableArray.Create(ExLeaf("tosec-c64-games")));
        var s = GroupDatReconciliationSession.ForExistingGroup(Catalog(new[] { "tosec-c64-games" }), group);
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Games.dat") }));

        var dat = s.AvailableIncoming.Single();
        var leaf = s.AvailableLeaves.Single();
        s.AssociateUpdate(dat.CandidateId, leaf.DatLineId);

        Assert.Empty(s.AvailableIncoming);
        Assert.Empty(s.AvailableLeaves);
        Assert.Single(s.Decisions);
        Assert.Equal(GroupDatDecisionKind.Update, s.Decisions.Single().Kind);
        Assert.Equal("other", s.Decisions.Single().MediaTypeId);   // update keeps existing media type
    }

    [Fact]
    public void NewLeaf_ConsumesOnlyDat()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("A/Games.dat") }));
        var dat = s.AvailableIncoming.Single();
        s.CreateNewLeaf(dat.CandidateId, s.ProposeIdFor(dat), "other");

        Assert.Empty(s.AvailableIncoming);
        Assert.Empty(s.AvailableLeaves);   // new group has none anyway
        Assert.Equal(GroupDatDecisionKind.NewLeaf, s.Decisions.Single().Kind);
    }

    [Fact]
    public void Absent_ConsumesOnlyLeaf()
    {
        var group = new GroupDatExistingGroup("tosec-c64", "C64", "capcom", "tosec", 0,
            ImmutableArray.Create(ExLeaf("tosec-c64-old")));
        var s = GroupDatReconciliationSession.ForExistingGroup(Catalog(new[] { "tosec-c64-old" }), group);
        s.SetDiscovery(Discovery("/src", Array.Empty<DiscoveredDatLeaf>()));

        s.MarkLeafAbsent("tosec-c64-old");
        Assert.Empty(s.AvailableLeaves);
        Assert.Equal(GroupDatDecisionKind.Absent, s.Decisions.Single().Kind);
    }

    [Fact]
    public void Undo_RestoresAvailability()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }));
        var dat = s.AvailableIncoming.Single();
        var d = s.CreateNewLeaf(dat.CandidateId, "tosec-c64-games", "other");
        Assert.Empty(s.AvailableIncoming);

        s.Undo(d.DecisionId);
        Assert.Single(s.AvailableIncoming);
        Assert.Empty(s.Decisions);
    }

    [Fact]
    public void DoubleResolvingSameDat_Throws()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }));
        var dat = s.AvailableIncoming.Single();
        s.CreateNewLeaf(dat.CandidateId, "tosec-c64-games", "other");
        Assert.Throws<InvalidOperationException>(() => s.CreateNewLeaf(dat.CandidateId, "tosec-c64-other", "other"));
    }

    [Fact]
    public void ForeignElement_Rejected()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }));
        Assert.Throws<InvalidOperationException>(() => s.CreateNewLeaf("not-a-candidate", "tosec-c64-x", "other"));
        Assert.Throws<InvalidOperationException>(() => s.MarkLeafAbsent("no-such-leaf"));
    }

    // ── Folder token tree ───────────────────────────────────────────────────────

    [Fact]
    public void FolderTree_BuildsFromRelativePaths_RootHasEmptyToken()
    {
        var tree = FolderTokenTree.Build(new[] { "Commodore/C64/Games.dat", "Root.dat" });
        Assert.True(tree.Root.IsRoot);
        Assert.Equal("", tree.Root.Token);
        Assert.Equal("commodore", tree.NodeForFolder("Commodore")!.Token);   // suggested
        Assert.Equal("c64", tree.NodeForFolder("Commodore/C64")!.Token);
    }

    [Fact]
    public void FolderTree_EmptyToken_ExcludedFromProposal_NodeKept()
    {
        var tree = FolderTokenTree.Build(new[] { "Commodore/C64/Games.dat" });
        tree.NodeForFolder("Commodore")!.Token = "";   // exclude this folder
        var tokens = tree.FolderTokensForFile("Commodore/C64/Games.dat");
        Assert.Equal(new[] { "c64" }, tokens.ToArray());          // Commodore dropped, C64 kept
        Assert.NotNull(tree.NodeForFolder("Commodore"));          // node still present
    }

    [Fact]
    public void EditingFolderToken_UpdatesOnlyDescendantProposals()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("A/x.dat"), Leaf("A/B/y.dat"), Leaf("C/z.dat") }));
        var ax = s.AvailableIncoming.Single(c => c.RelativePath == "A/x.dat");
        var cz = s.AvailableIncoming.Single(c => c.RelativePath == "C/z.dat");
        var beforeCz = s.ProposeIdFor(cz);

        s.FolderTree!.NodeForFolder("A")!.Token = "alpha";
        Assert.Contains("alpha", s.ProposeIdFor(ax));            // descendant updated
        Assert.Equal(beforeCz, s.ProposeIdFor(cz));             // sibling unchanged
    }

    [Fact]
    public void FolderTokenSuggestion_IsNormalizeSuggestionConsistent()
    {
        var tree = FolderTokenTree.Build(new[] { "日本/Démos/x.dat" });
        Assert.Equal(DatTechnicalIdPolicy.NormalizeSuggestion("日本"), tree.NodeForFolder("日本")!.Token);
        Assert.Equal(DatTechnicalIdPolicy.NormalizeSuggestion("Démos"), tree.NodeForFolder("日本/Démos")!.Token);
    }

    // ── ID composer ─────────────────────────────────────────────────────────────

    [Fact]
    public void Compose_JoinsGroupFolderDat_NoMediaOrHash()
    {
        var id = DatLineIdComposer.Compose("tosec-c64", new[] { "games", "" , "prg" }, "disk");
        Assert.Equal("tosec-c64-games-prg-disk", id);   // empty token skipped; no media/authority/hash
        Assert.DoesNotContain("cd", id);
    }

    [Fact]
    public void Evaluate_ValidId_Passes()
        => Assert.True(DatLineIdComposer.Evaluate("tosec-c64-games", _ => false).IsValid);

    [Fact]
    public void Evaluate_Over64_IsTooLong_NotTruncated()
    {
        var id = "g-" + new string('a', 70);
        var e = DatLineIdComposer.Evaluate(id, _ => false);
        Assert.False(e.IsValid);
        Assert.Equal(DatTechnicalIdError.TooLong, e.PolicyError);
        Assert.Equal(id, e.Id);   // returned verbatim, not truncated
    }

    [Fact]
    public void Evaluate_CollisionWithCatalog_CaseInsensitive()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }),
            Catalog(new[] { "Legacy-Id" }));   // legacy mixed-case occupied id
        Assert.True(s.EvaluateNewLeafId("legacy-id").Collides);   // case-insensitive collision
    }

    [Fact]
    public void Evaluate_CollisionBetweenNewLeaves()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("A/x.dat"), Leaf("B/y.dat") }));
        var a = s.AvailableIncoming.Single(c => c.RelativePath == "A/x.dat");
        s.CreateNewLeaf(a.CandidateId, "tosec-c64-dup", "other");
        Assert.True(s.EvaluateNewLeafId("tosec-c64-dup").Collides);

        var b = s.AvailableIncoming.Single(c => c.RelativePath == "B/y.dat");
        Assert.Throws<InvalidOperationException>(() => s.CreateNewLeaf(b.CandidateId, "tosec-c64-dup", "other"));
    }

    [Fact]
    public void CreateNewLeaf_ManualFinalId_IsUsedVerbatim()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("A/Games.dat") }));
        var dat = s.AvailableIncoming.Single();
        var manual = "tosec-c64-hand-picked";
        Assert.NotEqual(manual, s.ProposeIdFor(dat));
        var d = s.CreateNewLeaf(dat.CandidateId, manual, "other");
        Assert.Equal(manual, d.FinalId);
    }

    [Fact]
    public void CreateNewLeaf_MissingMediaType_Throws()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }));
        var dat = s.AvailableIncoming.Single();
        Assert.Throws<InvalidOperationException>(() => s.CreateNewLeaf(dat.CandidateId, "tosec-c64-games", ""));
        Assert.Throws<InvalidOperationException>(() => s.CreateNewLeaf(dat.CandidateId, "tosec-c64-games", "nonexistent"));
    }

    // ── New group vs update group ───────────────────────────────────────────────

    [Fact]
    public void NewGroup_HasNoRightSet()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }));
        Assert.Empty(s.AvailableLeaves);
        Assert.Equal(GroupDatReconciliationMode.NewGroup, s.Mode);
    }

    [Fact]
    public void UpdateGroup_LoadsExistingLeaves_AndKeepsMediaTypeOnUpdate()
    {
        var group = new GroupDatExistingGroup("tosec-c64", "C64", "capcom", "tosec", 0,
            ImmutableArray.Create(ExLeaf("tosec-c64-games", media: "cd")));
        var s = GroupDatReconciliationSession.ForExistingGroup(Catalog(new[] { "tosec-c64-games" }), group);
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Games.dat") }));

        Assert.Single(s.AvailableLeaves);
        var dat = s.AvailableIncoming.Single();
        var d = s.AssociateUpdate(dat.CandidateId, "tosec-c64-games");
        Assert.Equal("cd", d.MediaTypeId);   // update did not change media type
    }

    // ── Completion gating ───────────────────────────────────────────────────────

    [Fact]
    public void Gating_BlocksUntilAllResolved()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }));
        Assert.False(s.CanBuildPlan);
        Assert.Contains(s.BlockingReasons(), r => r.Contains("not yet resolved"));

        var dat = s.AvailableIncoming.Single();
        s.CreateNewLeaf(dat.CandidateId, "tosec-c64-games", "other");
        Assert.True(s.CanBuildPlan);
    }

    [Fact]
    public void Gating_BlocksOnDiscoveryErrors()
    {
        var s = NewGroupSession(Discovery("/src",
            new[] { Leaf("Bad.dat", status: DiscoveredDatLeafStatus.ParseFailed) },
            new[] { new DatGroupDiscoveryDiagnostic("dat-parse-failed", DatGroupDiscoveryDiagnosticSeverity.Error, "bad", "Bad.dat") }));
        Assert.False(s.CanBuildPlan);
        Assert.Contains(s.BlockingReasons(), r => r.Contains("blocking errors"));
    }

    [Fact]
    public void Gating_NewGroup_RequiresGroupFields()
    {
        var s = GroupDatReconciliationSession.ForNewGroup(Catalog());
        s.SetDiscovery(Discovery("/src", new[] { Leaf("Games.dat") }));
        var dat = s.AvailableIncoming.Single();
        s.CreateNewLeaf(dat.CandidateId, "g-games", "other");
        // group fields not set → still blocked
        Assert.False(s.CanBuildPlan);
        s.NewGroupId = "tosec-c64"; s.NewGroupDisplayName = "C64"; s.NewGroupHardwareFamilyId = "capcom"; s.NewGroupAuthority = "tosec";
        Assert.True(s.CanBuildPlan);
    }

    // ── Frozen plan ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildPlan_ThrowsWhenIncomplete()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("Games.dat") }));
        Assert.Throws<InvalidOperationException>(() => s.BuildPlan());
    }

    [Fact]
    public void BuildPlan_IsImmutableAndCarriesSnapshotAndSourcePaths()
    {
        var group = new GroupDatExistingGroup("tosec-c64", "C64", "capcom", "tosec", 0,
            ImmutableArray.Create(ExLeaf("tosec-c64-old"), ExLeaf("tosec-c64-keep")));
        var s = GroupDatReconciliationSession.ForExistingGroup(
            Catalog(new[] { "tosec-c64-old", "tosec-c64-keep" }), group);
        s.SetDiscovery(Discovery("/src", new[] { Leaf("A/New.dat"), Leaf("Keep.dat") }));

        var newDat  = s.AvailableIncoming.Single(c => c.RelativePath == "A/New.dat");
        var keepDat = s.AvailableIncoming.Single(c => c.RelativePath == "Keep.dat");
        s.CreateNewLeaf(newDat.CandidateId, "tosec-c64-new", "other");
        s.AssociateUpdate(keepDat.CandidateId, "tosec-c64-keep");
        s.MarkLeafAbsent("tosec-c64-old");

        Assert.True(s.CanBuildPlan);
        var plan = s.BuildPlan();

        Assert.Equal(GroupDatReconciliationMode.UpdateGroup, plan.Mode);
        Assert.Equal("tosec-c64", plan.ExistingGroupId);
        Assert.IsType<ImmutableArray<GroupDatNewLeafPlan>>(plan.NewLeaves);
        Assert.Single(plan.NewLeaves);
        Assert.Equal("/src/A/New.dat", plan.NewLeaves[0].SourcePath);
        Assert.Equal("A/New.dat", plan.NewLeaves[0].SourceRelativePath);
        Assert.Single(plan.Updates);
        Assert.Equal("tosec-c64-keep", plan.Updates[0].ExistingLeafId);
        Assert.Single(plan.AbsentLeaves);
        Assert.Equal("tosec-c64-old", plan.AbsentLeaves[0].ExistingLeafId);
        // frozen immutable discovery snapshot present; exposes only immutable snapshot games
        Assert.Equal(2, plan.DiscoverySnapshot.Length);
        Assert.IsType<ImmutableArray<DiscoveredDatGame>>(plan.DiscoverySnapshot[0].Games);
    }

    // ── §1 Source-root change resets the session ────────────────────────────────

    [Fact]
    public void ChangingDiscovery_ResetsAllDecisionsAndReferences_AndPlanOnlyHasNewSource()
    {
        var s = NewGroupSession(Discovery("/src1", new[] { Leaf("Old/A.dat") }));
        var oldDat = s.AvailableIncoming.Single();
        s.CreateNewLeaf(oldDat.CandidateId, "tosec-c64-a", "other");
        Assert.Single(s.Decisions);

        // Select a new source folder.
        s.SetDiscovery(Discovery("/src2", new[] { Leaf("New/B.dat") }));

        Assert.Empty(s.Decisions);                                   // all decisions cleared
        Assert.Single(s.AvailableIncoming);
        Assert.Equal("New/B.dat", s.AvailableIncoming.Single().RelativePath);
        Assert.DoesNotContain(s.AvailableIncoming, c => c.RelativePath == "Old/A.dat");
        Assert.Null(s.FolderTree!.NodeForFolder("Old"));             // old folder tree gone
        Assert.NotNull(s.FolderTree.NodeForFolder("New"));

        var newDat = s.AvailableIncoming.Single();
        s.CreateNewLeaf(newDat.CandidateId, "tosec-c64-b", "other");
        var plan = s.BuildPlan();
        Assert.Equal("/src2", plan.SourceRoot);
        Assert.Single(plan.NewLeaves);
        Assert.Equal("New/B.dat", plan.NewLeaves[0].SourceRelativePath);
        Assert.Contains("New/B.dat", plan.NewLeaves[0].SourcePath);
        Assert.DoesNotContain(plan.NewLeaves, n => n.SourceRelativePath.Contains("Old"));
    }

    // ── §2 Target change discards decisions and rebuilds the right set ──────────

    [Fact]
    public void SwitchingTarget_ProducesIndependentSession_NoLeakage()
    {
        var disc = Discovery("/src", new[] { Leaf("Games.dat") });
        var groupA = new GroupDatExistingGroup("grp-a", "A", "capcom", "tosec", 0,
            ImmutableArray.Create(ExLeaf("grp-a-leaf")));
        var groupB = new GroupDatExistingGroup("grp-b", "B", "capcom", "tosec", 0,
            ImmutableArray.Create(ExLeaf("grp-b-leaf")));
        var cat = Catalog(new[] { "grp-a-leaf", "grp-b-leaf" }, new[] { groupA, groupB });

        // existing A → make an absent decision on A's leaf
        var a = GroupDatReconciliationSession.ForExistingGroup(cat, groupA);
        a.SetDiscovery(disc);
        a.MarkLeafAbsent("grp-a-leaf");
        Assert.Single(a.Decisions);

        // switch to existing B (window builds a fresh session) → no A leaf/decision survives
        var b = GroupDatReconciliationSession.ForExistingGroup(cat, groupB);
        b.SetDiscovery(disc);
        Assert.Empty(b.Decisions);
        Assert.Single(b.AvailableLeaves);
        Assert.Equal("grp-b-leaf", b.AvailableLeaves.Single().DatLineId);
        Assert.DoesNotContain(b.AvailableLeaves, l => l.DatLineId == "grp-a-leaf");
        Assert.Throws<InvalidOperationException>(() => b.MarkLeafAbsent("grp-a-leaf"));   // foreign leaf

        // resolve B and confirm the plan references only B
        var dat = b.AvailableIncoming.Single();
        b.AssociateUpdate(dat.CandidateId, "grp-b-leaf");
        var plan = b.BuildPlan();
        Assert.Equal("grp-b", plan.ExistingGroupId);
        Assert.All(plan.Updates, u => Assert.Equal("grp-b-leaf", u.ExistingLeafId));

        // switch to new group → no existing leaves, all DATs must become new leaves
        var n = GroupDatReconciliationSession.ForNewGroup(cat);
        n.SetDiscovery(disc);
        Assert.Empty(n.AvailableLeaves);
    }

    // ── §3 Auto proposal vs manual override ─────────────────────────────────────

    [Fact]
    public void AutoFinalId_RecomputesWhenTokensChange()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("A/Games.dat") }));
        var dat = s.AvailableIncoming.Single();
        Assert.False(dat.IsFinalIdManual);
        var before = s.EffectiveIdFor(dat);

        dat.DatToken = "renamed";                                    // DAT token changed
        Assert.NotEqual(before, s.EffectiveIdFor(dat));
        Assert.Contains("renamed", s.EffectiveIdFor(dat));

        s.FolderTree!.NodeForFolder("A")!.Token = "alpha";           // ancestor folder token changed
        Assert.Contains("alpha", s.EffectiveIdFor(dat));
    }

    [Fact]
    public void ManualFinalId_NotOverwrittenByTokenChanges()
    {
        var s = NewGroupSession(Discovery("/src", new[] { Leaf("A/Games.dat") }));
        var dat = s.AvailableIncoming.Single();

        s.SetManualFinalId(dat.CandidateId, "tosec-c64-manual");
        Assert.True(dat.IsFinalIdManual);
        Assert.Equal("tosec-c64-manual", s.EffectiveIdFor(dat));

        dat.DatToken = "renamed";                                    // must NOT overwrite manual id
        s.FolderTree!.NodeForFolder("A")!.Token = "alpha";
        Assert.Equal("tosec-c64-manual", s.EffectiveIdFor(dat));

        s.ClearManualFinalId(dat.CandidateId);                       // back to auto
        Assert.False(dat.IsFinalIdManual);
        Assert.Contains("alpha", s.EffectiveIdFor(dat));            // now tracks tokens again
    }
}

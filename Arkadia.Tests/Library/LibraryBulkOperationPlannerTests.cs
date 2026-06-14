using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Library;

/// <summary>
/// Tests for LibraryBulkOperationPlanner — 36 tests.
/// All tests use a temp-dir SQLite DB and call the planner with a real DatLineStore.
/// </summary>
public sealed class LibraryBulkOperationPlannerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _dbPath;

    public LibraryBulkOperationPlannerTests()
    {
        _tmp    = Path.Combine(Path.GetTempPath(), "ArkBulkPlan_" + Guid.NewGuid().ToString("N")[..8]);
        _dbPath = Path.Combine(_tmp, "test.db");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore Open() => new(_dbPath);

    private LibraryBulkOperationPlanner Planner() => new(Open());

    private const string DatId = "dl1";

    private ReleaseRecord Rel(string id, string name, string status = "missing",
        bool show = true)
        => new()
        {
            Id            = id,
            DatLineId     = DatId,
            Name          = name,
            Status        = status,
            ShowInCatalog = show,
            Tier = "", Region = "", Languages = "", Format = "", Size = "",
            ReleaseContentKey = "", ContentCategoryId = "games",
        };

    private void InsertReleases(params ReleaseRecord[] releases)
        => Open().SaveReleases(new List<ReleaseRecord>(releases));

    private string InsertDerived(DatLineStore store, string releaseId, string fileName, long bytes = 1024)
    {
        var cik = $"cik:{releaseId}:{fileName}";
        var id  = store.IngestDerivedArtifact(
            contentIdentityKey: cik,
            sourceArtifactId:   "",
            storageStrategyId:  "chd",
            fileName:           fileName,
            relativePath:       $"archive/test/{DatId}/{fileName}",
            derivedSizeBytes:   bytes,
            hashedDerivedSha1:  "aabbccdd");
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id                 = Guid.NewGuid().ToString("N"),
            ReleaseId          = releaseId,
            ContentIdentityKey = cik,
            CreatedAtUtc       = DateTime.UtcNow,
        });
        return id;
    }

    // ─── FILTER TESTS (1–8) ─────────────────────────────────────────────────

    [Fact] // Test 1
    public void Filter_ExactMatch_SingleResult()
    {
        InsertReleases(Rel("r1", "Super Mario World"), Rel("r2", "Donkey Kong"));
        var plan = Planner().Plan(DatId, "Test", "Super Mario World",
            LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(1, plan.TotalMatches);
        Assert.Equal("Super Mario World", plan.Rows[0].ReleaseName);
    }

    [Fact] // Test 2
    public void Filter_PartialMatch_MultipleResults()
    {
        InsertReleases(Rel("r1", "Mario Bros"), Rel("r2", "Super Mario World"),
                       Rel("r3", "Donkey Kong"));
        var plan = Planner().Plan(DatId, "Test", "Mario",
            LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(2, plan.TotalMatches);
    }

    [Fact] // Test 3
    public void Filter_CaseInsensitive_Matches()
    {
        InsertReleases(Rel("r1", "Super Mario World"));
        var plan = Planner().Plan(DatId, "Test", "SUPER MARIO",
            LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(1, plan.TotalMatches);
    }

    [Fact] // Test 4
    public void Filter_NoMatch_EmptyPlan()
    {
        InsertReleases(Rel("r1", "Donkey Kong"));
        var plan = Planner().Plan(DatId, "Test", "zelda",
            LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(0, plan.TotalMatches);
        Assert.Empty(plan.Rows);
    }

    [Fact] // Test 5
    public void Filter_EmptyMatchText_ReturnsEmptyPlan()
    {
        InsertReleases(Rel("r1", "Any Game"));
        var plan = Planner().Plan(DatId, "Test", "",
            LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(0, plan.TotalMatches);
    }

    [Fact] // Test 6
    public void Filter_WhitespaceOnlyMatchText_ReturnsEmptyPlan()
    {
        InsertReleases(Rel("r1", "Any Game"));
        var plan = Planner().Plan(DatId, "Test", "   ",
            LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(0, plan.TotalMatches);
    }

    [Fact] // Test 7
    public void Filter_MatchesSubstringAnywhere()
    {
        InsertReleases(Rel("r1", "The Legend of Zelda"));
        var plan = Planner().Plan(DatId, "Test", "Legend",
            LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(1, plan.TotalMatches);
    }

    [Fact] // Test 8
    public void Filter_PlanContainsCorrectDatLineInfo()
    {
        InsertReleases(Rel("r1", "Game A"));
        var plan = Planner().Plan(DatId, "My DAT Label", "Game A",
            LibraryBulkOperationType.ShowInCatalog);
        Assert.Equal(DatId,          plan.DatLineId);
        Assert.Equal("My DAT Label", plan.DatLineLabel);
        Assert.Equal("Game A",       plan.MatchText);
        Assert.Equal(LibraryBulkOperationType.ShowInCatalog, plan.Operation);
    }

    // ─── HIDE FROM CATALOG TESTS (9–12) ────────────────────────────────────

    [Fact] // Test 9
    public void Hide_VisibleRelease_IsActionable()
    {
        InsertReleases(Rel("r1", "Game A", show: true));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.HideFromCatalog);
        var row  = Assert.Single(plan.Rows);
        Assert.False(row.IsNoOp);
        Assert.Equal(1, plan.ActionableCount);
    }

    [Fact] // Test 10
    public void Hide_AlreadyHiddenRelease_IsNoOp()
    {
        InsertReleases(Rel("r1", "Game A", show: false));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.HideFromCatalog);
        var row  = Assert.Single(plan.Rows);
        Assert.True(row.IsNoOp);
        Assert.Equal(0, plan.ActionableCount);
    }

    [Fact] // Test 11
    public void Hide_MixedVisibility_CorrectCounts()
    {
        InsertReleases(Rel("r1", "Game A", show: true), Rel("r2", "Game B", show: false));
        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(2, plan.TotalMatches);
        Assert.Equal(1, plan.ActionableCount);
        Assert.Equal(1, plan.NoOpCount);
    }

    [Fact] // Test 12
    public void Hide_ActionableRow_NoteContainsExpectedText()
    {
        InsertReleases(Rel("r1", "Game A", show: true));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.HideFromCatalog);
        var row  = Assert.Single(plan.Rows);
        Assert.Contains("hidden", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ─── SHOW IN CATALOG TESTS (13–16) ─────────────────────────────────────

    [Fact] // Test 13
    public void Show_HiddenRelease_IsActionable()
    {
        InsertReleases(Rel("r1", "Game A", show: false));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.ShowInCatalog);
        var row  = Assert.Single(plan.Rows);
        Assert.False(row.IsNoOp);
        Assert.Equal(1, plan.ActionableCount);
    }

    [Fact] // Test 14
    public void Show_AlreadyVisibleRelease_IsNoOp()
    {
        InsertReleases(Rel("r1", "Game A", show: true));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.ShowInCatalog);
        var row  = Assert.Single(plan.Rows);
        Assert.True(row.IsNoOp);
        Assert.Equal(0, plan.ActionableCount);
    }

    [Fact] // Test 15
    public void Show_MixedVisibility_CorrectCounts()
    {
        InsertReleases(Rel("r1", "Game A", show: false), Rel("r2", "Game B", show: true));
        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.ShowInCatalog);
        Assert.Equal(1, plan.ActionableCount);
        Assert.Equal(1, plan.NoOpCount);
    }

    [Fact] // Test 16
    public void Show_ActionableRow_NoteContainsExpectedText()
    {
        InsertReleases(Rel("r1", "Game A", show: false));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.ShowInCatalog);
        var row  = Assert.Single(plan.Rows);
        Assert.Contains("shown", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ─── PURGE AND MARK UNWANTED TESTS (17–22) ─────────────────────────────

    [Fact] // Test 17
    public void Purge_NonUnwantedRelease_IsActionable()
    {
        InsertReleases(Rel("r1", "Game A", status: "present"));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.PurgeAndMarkUnwanted);
        var row  = Assert.Single(plan.Rows);
        Assert.False(row.IsNoOp);
        Assert.Equal(1, plan.ActionableCount);
    }

    [Fact] // Test 18
    public void Purge_UnwantedRelease_IsNoOp()
    {
        InsertReleases(Rel("r1", "Game A", status: "unwanted"));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.PurgeAndMarkUnwanted);
        var row  = Assert.Single(plan.Rows);
        Assert.True(row.IsNoOp);
        Assert.Equal(0, plan.ActionableCount);
    }

    [Fact] // Test 19
    public void Purge_IncludesArchiveFileCount()
    {
        var store = Open();
        store.SaveReleases(new List<ReleaseRecord> { Rel("r1", "Game A", status: "present") });
        InsertDerived(store, "r1", "Game A.chd");
        InsertDerived(store, "r1", "Game A.bin");

        var plan = new LibraryBulkOperationPlanner(store)
            .Plan(DatId, "L", "Game A", LibraryBulkOperationType.PurgeAndMarkUnwanted);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(2, row.ArchiveFileCount);
        Assert.Equal(2, plan.TotalArchiveFiles);
    }

    [Fact] // Test 20
    public void Purge_IncludesArchiveBytes()
    {
        var store = Open();
        store.SaveReleases(new List<ReleaseRecord> { Rel("r1", "Game A", status: "present") });
        InsertDerived(store, "r1", "Game A.chd", bytes: 2000);

        var plan = new LibraryBulkOperationPlanner(store)
            .Plan(DatId, "L", "Game A", LibraryBulkOperationType.PurgeAndMarkUnwanted);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(2000, row.ArchiveBytes);
        Assert.Equal(2000, plan.TotalArchiveBytes);
    }

    [Fact] // Test 21
    public void Purge_MixedStatuses_CorrectCounts()
    {
        InsertReleases(
            Rel("r1", "Game A", status: "missing"),
            Rel("r2", "Game B", status: "unwanted"),
            Rel("r3", "Game C", status: "present"));
        var plan = Planner().Plan(DatId, "L", "Game",
            LibraryBulkOperationType.PurgeAndMarkUnwanted);
        Assert.Equal(2, plan.ActionableCount); // missing + present
        Assert.Equal(1, plan.NoOpCount);       // unwanted
    }

    [Fact] // Test 22
    public void Purge_ZeroArtifacts_NoteIndicatesNoFiles()
    {
        InsertReleases(Rel("r1", "Game A", status: "missing"));
        var plan = Planner().Plan(DatId, "L", "Game A",
            LibraryBulkOperationType.PurgeAndMarkUnwanted);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(0, row.ArchiveFileCount);
        Assert.Contains("no archive", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ─── RESTORE WANTED TESTS (23–26) ──────────────────────────────────────

    [Fact] // Test 23
    public void Restore_UnwantedRelease_IsActionable()
    {
        InsertReleases(Rel("r1", "Game A", status: "unwanted"));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.RestoreWanted);
        var row  = Assert.Single(plan.Rows);
        Assert.False(row.IsNoOp);
        Assert.Equal(1, plan.ActionableCount);
    }

    [Fact] // Test 24
    public void Restore_NonUnwantedRelease_IsNoOp()
    {
        InsertReleases(Rel("r1", "Game A", status: "missing"));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.RestoreWanted);
        var row  = Assert.Single(plan.Rows);
        Assert.True(row.IsNoOp);
        Assert.Equal(0, plan.ActionableCount);
    }

    [Fact] // Test 25
    public void Restore_MixedStatuses_CorrectCounts()
    {
        InsertReleases(
            Rel("r1", "Game A", status: "unwanted"),
            Rel("r2", "Game B", status: "missing"),
            Rel("r3", "Game C", status: "present"));
        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.RestoreWanted);
        Assert.Equal(1, plan.ActionableCount);
        Assert.Equal(2, plan.NoOpCount);
    }

    [Fact] // Test 26
    public void Restore_ActionableRow_NoteContainsExpectedText()
    {
        InsertReleases(Rel("r1", "Game A", status: "unwanted"));
        var plan = Planner().Plan(DatId, "L", "Game A", LibraryBulkOperationType.RestoreWanted);
        var row  = Assert.Single(plan.Rows);
        Assert.Contains("restored", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ─── AGGREGATE STATS TESTS (27–32) ─────────────────────────────────────

    [Fact] // Test 27
    public void Stats_StatusBreakdown_AllStatusesCounted()
    {
        InsertReleases(
            Rel("r1", "G1", status: "missing"),
            Rel("r2", "G2", status: "present"),
            Rel("r3", "G3", status: "pending"),
            Rel("r4", "G4", status: "outdated"),
            Rel("r5", "G5", status: "lost"),
            Rel("r6", "G6", status: "unwanted"));
        var plan = Planner().Plan(DatId, "L", "G", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(6, plan.TotalMatches);
        Assert.Equal(1, plan.MissingCount);
        Assert.Equal(1, plan.PresentCount);
        Assert.Equal(1, plan.PendingCount);
        Assert.Equal(1, plan.OutdatedCount);
        Assert.Equal(1, plan.LostCount);
        Assert.Equal(1, plan.UnwantedCount);
    }

    [Fact] // Test 28
    public void Stats_TotalArchiveFiles_SummedAcrossAllMatchedReleases()
    {
        var store = Open();
        store.SaveReleases(new List<ReleaseRecord>
        {
            Rel("r1", "Game A", status: "present"),
            Rel("r2", "Game B", status: "present"),
        });
        InsertDerived(store, "r1", "A.chd");
        InsertDerived(store, "r1", "A2.chd");
        InsertDerived(store, "r2", "B.chd");

        var plan = new LibraryBulkOperationPlanner(store)
            .Plan(DatId, "L", "Game", LibraryBulkOperationType.PurgeAndMarkUnwanted);
        Assert.Equal(3, plan.TotalArchiveFiles);
    }

    [Fact] // Test 29
    public void Stats_TotalArchiveBytes_SummedCorrectly()
    {
        var store = Open();
        store.SaveReleases(new List<ReleaseRecord>
        {
            Rel("r1", "Game A", status: "present"),
        });
        InsertDerived(store, "r1", "A.chd",  bytes: 1000);
        InsertDerived(store, "r1", "A2.chd", bytes: 3000);

        var plan = new LibraryBulkOperationPlanner(store)
            .Plan(DatId, "L", "Game A", LibraryBulkOperationType.PurgeAndMarkUnwanted);
        Assert.Equal(4000, plan.TotalArchiveBytes);
    }

    [Fact] // Test 30
    public void Stats_ActionableCount_ExcludesNoOpRows()
    {
        InsertReleases(
            Rel("r1", "Game A", show: true),   // actionable for Hide
            Rel("r2", "Game B", show: false),  // no-op for Hide (already hidden)
            Rel("r3", "Game C", show: true));  // actionable for Hide
        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(2, plan.ActionableCount);
        Assert.Equal(1, plan.NoOpCount);
    }

    [Fact] // Test 31
    public void Stats_NoOpCount_CorrectForHide()
    {
        InsertReleases(
            Rel("r1", "Game A", show: false),
            Rel("r2", "Game B", show: false));
        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(0, plan.ActionableCount);
        Assert.Equal(2, plan.NoOpCount);
    }

    [Fact] // Test 32
    public void Stats_TotalMatches_EqualsRowCount()
    {
        InsertReleases(Rel("r1", "A"), Rel("r2", "A2"), Rel("r3", "A3"));
        var plan = Planner().Plan(DatId, "L", "A", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(plan.TotalMatches, plan.Rows.Count);
    }

    // ─── LARGE BATCH SAFETY TESTS (33–36) ──────────────────────────────────

    [Fact] // Test 33
    public void LargeBatch_50ActionableReleases_IsLargeBatchFalse()
    {
        var releases = Enumerable.Range(1, 50)
            .Select(i => Rel($"r{i}", $"Game {i:D3}", show: true))
            .ToList();
        Open().SaveReleases(releases);

        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(50, plan.ActionableCount);
        Assert.False(plan.IsLargeBatch);
        Assert.False(plan.IsVeryLargeBatch);
    }

    [Fact] // Test 34
    public void LargeBatch_51ActionableReleases_IsLargeBatchTrue()
    {
        var releases = Enumerable.Range(1, 51)
            .Select(i => Rel($"r{i}", $"Game {i:D3}", show: true))
            .ToList();
        Open().SaveReleases(releases);

        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(51, plan.ActionableCount);
        Assert.True(plan.IsLargeBatch);
        Assert.False(plan.IsVeryLargeBatch);
    }

    [Fact] // Test 35
    public void LargeBatch_200ActionableReleases_IsLargeBatchTrueNotVeryLarge()
    {
        var releases = Enumerable.Range(1, 200)
            .Select(i => Rel($"r{i}", $"Game {i:D3}", show: true))
            .ToList();
        Open().SaveReleases(releases);

        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(200, plan.ActionableCount);
        Assert.True(plan.IsLargeBatch);
        Assert.False(plan.IsVeryLargeBatch);
    }

    [Fact] // Test 36
    public void LargeBatch_201ActionableReleases_IsVeryLargeBatchTrue()
    {
        var releases = Enumerable.Range(1, 201)
            .Select(i => Rel($"r{i}", $"Game {i:D3}", show: true))
            .ToList();
        Open().SaveReleases(releases);

        var plan = Planner().Plan(DatId, "L", "Game", LibraryBulkOperationType.HideFromCatalog);
        Assert.Equal(201, plan.ActionableCount);
        Assert.True(plan.IsLargeBatch);
        Assert.True(plan.IsVeryLargeBatch);
        Assert.Equal($"HIDE {201}", plan.ConfirmationPhrase);
    }
}

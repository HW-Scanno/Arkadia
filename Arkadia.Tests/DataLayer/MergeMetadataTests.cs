using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Tests for DatLineStore.ApplyMergeSelections — the deliberate user-driven apply
/// (distinct from ApplyProviderProposals which is the safe auto-apply flow).
/// </summary>
public sealed class MergeMetadataTests : IDisposable
{
    private readonly string _dbPath;

    public MergeMetadataTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private DatLineStore Open() => new(_dbPath);

    private static ReleaseMetadataRecord EmptyRecord(string id = "rel-001") =>
        new() { ReleaseId = id, ScrapedAtUtc = "" };

    private static IReadOnlyList<(string, string)> Sel(params (string, string)[] pairs) => pairs;

    // ── Apply behavior ────────────────────────────────────────────────────────

    [Fact]
    public void ApplySelections_UpdatesCanonicalField()
    {
        var store  = Open();
        var merged = store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("title", "Great Game")), EmptyRecord());

        Assert.Equal("Great Game", merged.Title);
        Assert.Equal("Great Game", store.LoadReleaseMetadata()["rel-001"].Title);
    }

    [Fact]
    public void ApplySelections_ReturnsUpdatedRecord()
    {
        var store   = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001", Title = "Old", Developer = "Dev Co", ScrapedAtUtc = "",
        };
        var merged = store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("title", "New")), current);

        Assert.Equal("New",    merged.Title);
        Assert.Equal("Dev Co", merged.Developer); // preserved
    }

    [Fact]
    public void ApplySelections_PreservesUnselectedFields()
    {
        var store   = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            Title       = "My Title",
            Developer   = "Dev Co",
            ScrapedAtUtc = "",
        };
        store.SaveReleaseMetadata(current);

        // Apply only genre — developer and title must be untouched
        store.ApplyMergeSelections("rel-001", "ss", Sel(("genre", "Action")), current);

        var loaded = store.LoadReleaseMetadata()["rel-001"];
        Assert.Equal("My Title", loaded.Title);
        Assert.Equal("Dev Co",   loaded.Developer);
        Assert.Equal("Action",   loaded.Genre);
    }

    [Fact]
    public void ApplySelections_WritesFieldState_SourceIsProvider()
    {
        var store = Open();
        store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("title", "Great Game")), EmptyRecord());

        var states = store.LoadMetadataFieldStates("rel-001");
        var s = states.Find(x => x.Field == "title");
        Assert.NotNull(s);
        Assert.Equal("ss", s!.Source);
        Assert.Equal("ss", s.Provider);
        Assert.False(s.Locked);
    }

    [Fact]
    public void ApplySelections_MarksProposalAccepted()
    {
        var store = Open();
        // Seed a pending proposal
        store.SaveMetadataProposal("rel-001", "ss", "title", "Great Game");
        Assert.False(store.LoadMetadataProposals("rel-001", "ss").Find(x => x.Field == "title")!.Accepted);

        store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("title", "Great Game")), EmptyRecord());

        Assert.True(store.LoadMetadataProposals("rel-001", "ss").Find(x => x.Field == "title")!.Accepted);
    }

    [Fact]
    public void UnselectedProposal_CanonicalNotChanged()
    {
        var store   = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            Title       = "Existing Title",
            Genre       = "Puzzle",
            ScrapedAtUtc = "",
        };
        store.SaveReleaseMetadata(current);

        // Apply only genre — title proposal exists but is not in selections
        store.SaveMetadataProposal("rel-001", "ss", "title", "New Title");
        store.ApplyMergeSelections("rel-001", "ss", Sel(("genre", "Action")), current);

        var loaded = store.LoadReleaseMetadata()["rel-001"];
        Assert.Equal("Existing Title", loaded.Title);   // unchanged
        Assert.Equal("Action",         loaded.Genre);   // applied
    }

    [Fact]
    public void EmptySelections_ReturnsCurrent_NoSideEffects()
    {
        var store   = Open();
        var current = EmptyRecord();
        var merged  = store.ApplyMergeSelections("rel-001", "ss", [], current);

        Assert.Same(current, merged);
        Assert.Empty(store.LoadMetadataFieldStates("rel-001"));
    }

    [Fact]
    public void ApplyMultipleFields_AllUpdated()
    {
        var store  = Open();
        var merged = store.ApplyMergeSelections(
            "rel-001", "ss",
            Sel(("title", "T"), ("developer", "D"), ("genre", "G"), ("year", "2005")),
            EmptyRecord());

        Assert.Equal("T",    merged.Title);
        Assert.Equal("D",    merged.Developer);
        Assert.Equal("G",    merged.Genre);
        Assert.Equal("2005", merged.Year);

        var loaded = store.LoadReleaseMetadata()["rel-001"];
        Assert.Equal("T",    loaded.Title);
        Assert.Equal("D",    loaded.Developer);
        Assert.Equal("G",    loaded.Genre);
        Assert.Equal("2005", loaded.Year);
    }

    [Fact]
    public void ApplySelections_DoesNotRemoveProviderPayload()
    {
        var store = Open();
        store.SaveProviderPayload("rel-001", "ss", "{\"test\":1}");
        store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("title", "T")), EmptyRecord());

        // Payload should still be present
        var payload = store.LoadProviderPayload("rel-001", "ss");
        Assert.Equal("{\"test\":1}", payload);
    }

    // ── Locked / manual protection (data-layer perspective) ───────────────────

    [Fact]
    public void LockedField_WhenExcludedFromSelections_CanonicalUnchanged()
    {
        var store   = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            Title       = "ManualTitle",
            ScrapedAtUtc = "",
        };
        store.SaveReleaseMetadata(current);
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);

        // Dialog would exclude locked field from selections — simulate by not passing it
        store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("genre", "Action")), current);

        Assert.Equal("ManualTitle", store.LoadReleaseMetadata()["rel-001"].Title);
        Assert.True(store.IsMetadataFieldLocked("rel-001", "title")); // lock preserved
    }

    [Fact]
    public void SameValueApply_OverwritesWithSameValue_NoError()
    {
        var store   = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            Title       = "Same Title",
            ScrapedAtUtc = "",
        };
        store.SaveReleaseMetadata(current);

        // Dialog normally filters same-value rows, but ApplyMergeSelections handles it gracefully
        var merged = store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("title", "Same Title")), current);

        Assert.Equal("Same Title", merged.Title);
        Assert.Equal("Same Title", store.LoadReleaseMetadata()["rel-001"].Title);
    }

    [Fact]
    public void ApplySelections_PreservesScrapedAtUtc()
    {
        var store   = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            ScrapedAtUtc = "2026-01-15T10:00:00Z",
        };
        var merged = store.ApplyMergeSelections(
            "rel-001", "ss", Sel(("title", "New")), current);

        Assert.Equal("2026-01-15T10:00:00Z", merged.ScrapedAtUtc);
        Assert.Equal("2026-01-15T10:00:00Z", store.LoadReleaseMetadata()["rel-001"].ScrapedAtUtc);
    }

    // ── Load proposals ────────────────────────────────────────────────────────

    [Fact]
    public void LoadProposals_ReturnsPendingAndAccepted()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "ss", "title",  "T1");
        store.SaveMetadataProposal("rel-001", "ss", "genre",  "G1");
        store.MarkMetadataProposalAccepted("rel-001", "ss", "genre");

        var proposals = store.LoadMetadataProposals("rel-001", "ss");
        Assert.Equal(2, proposals.Count);
        Assert.False(proposals.Find(p => p.Field == "title")!.Accepted);
        Assert.True( proposals.Find(p => p.Field == "genre")!.Accepted);
    }

    [Fact]
    public void LoadProposals_EmptyForNewRelease()
    {
        var store = Open();
        Assert.Empty(store.LoadMetadataProposals("rel-999", "ss"));
    }
}

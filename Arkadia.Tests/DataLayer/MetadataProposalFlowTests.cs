using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Tests for DatLineStore.ApplyProviderProposals — the safe auto-apply rules:
/// 1. Empty proposed value → ignored (no proposal row).
/// 2. Field locked        → proposal saved (accepted=0), canonical unchanged.
/// 3. Canonical non-empty → proposal saved (accepted=0), canonical unchanged.
/// 4. Canonical empty + not locked → auto-apply, field_state saved, proposal accepted=1.
/// </summary>
public sealed class MetadataProposalFlowTests : IDisposable
{
    private readonly string _dbPath;

    public MetadataProposalFlowTests()
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

    private static ReleaseMetadataRecord EmptyRecord(string id = "rel-001") => new()
    {
        ReleaseId    = id,
        ScrapedAtUtc = "",
    };

    private static IReadOnlyDictionary<string, string> P(params (string, string)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    // ── Rule 1: Empty proposed value is ignored ───────────────────────────────

    [Fact]
    public void EmptyProposedValue_NoProposalSaved()
    {
        var store = Open();
        store.ApplyProviderProposals("rel-001", "ss", P(("title", "")), EmptyRecord());

        Assert.Empty(store.LoadMetadataProposals("rel-001", "ss"));
        Assert.Empty(store.LoadMetadataFieldStates("rel-001"));
    }

    [Fact]
    public void EmptyProposedValue_CanonicalUnchanged()
    {
        var store = Open();
        store.ApplyProviderProposals("rel-001", "ss", P(("title", "")), EmptyRecord());

        Assert.False(store.LoadReleaseMetadata().ContainsKey("rel-001"));
    }

    // ── Rule 4: Empty canonical + not locked → auto-apply ────────────────────

    [Fact]
    public void EmptyCanonical_NotLocked_AutoApplied()
    {
        var store = Open();
        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Great Game")), EmptyRecord());

        Assert.Contains("title", applied);
        Assert.Equal("Great Game", merged.Title);
    }

    [Fact]
    public void EmptyCanonical_AutoApplied_WrittenToDatabase()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Great Game")), EmptyRecord());

        Assert.Equal("Great Game", store.LoadReleaseMetadata()["rel-001"].Title);
    }

    [Fact]
    public void EmptyCanonical_AutoApplied_ProposalMarkedAccepted()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Great Game")), EmptyRecord());

        var proposals = store.LoadMetadataProposals("rel-001", "ss");
        var p = proposals.Find(x => x.Field == "title");
        Assert.NotNull(p);
        Assert.True(p!.Accepted);
    }

    [Fact]
    public void EmptyCanonical_AutoApplied_FieldStateSourceIsProvider()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Great Game")), EmptyRecord());

        var states = store.LoadMetadataFieldStates("rel-001");
        var s = states.Find(x => x.Field == "title");
        Assert.NotNull(s);
        Assert.Equal("ss", s!.Source);
        Assert.False(s.Locked);
    }

    // ── Rule 3: Non-empty canonical → proposal only ───────────────────────────

    [Fact]
    public void NonEmptyCanonical_ProposalOnly_CanonicalPreserved()
    {
        var store = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001", Title = "Original", ScrapedAtUtc = "",
        };
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Proposed")), current);

        // Canonical not written (ApplyProviderProposals only saves when auto-applied)
        // But we pre-saved current so we can verify it's unchanged
        store.SaveReleaseMetadata(current);
        Assert.Equal("Original", store.LoadReleaseMetadata()["rel-001"].Title);
    }

    [Fact]
    public void NonEmptyCanonical_ProposalSaved_NotAccepted()
    {
        var store = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001", Title = "Original", ScrapedAtUtc = "",
        };
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Proposed")), current);

        var proposals = store.LoadMetadataProposals("rel-001", "ss");
        var p = proposals.Find(x => x.Field == "title");
        Assert.NotNull(p);
        Assert.Equal("Proposed", p!.Value);
        Assert.False(p.Accepted);
    }

    [Fact]
    public void NonEmptyCanonical_ReturnedRecordIsCurrentUnchanged()
    {
        var store = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001", Title = "Original", ScrapedAtUtc = "",
        };
        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Proposed")), current);

        Assert.Empty(applied);
        Assert.Equal("Original", merged.Title);
    }

    // ── Rule 2: Locked field → proposal only ─────────────────────────────────

    [Fact]
    public void LockedField_ProposalOnly_NotAccepted()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);

        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Proposed")), EmptyRecord());

        var proposals = store.LoadMetadataProposals("rel-001", "ss");
        var p = proposals.Find(x => x.Field == "title");
        Assert.NotNull(p);
        Assert.False(p!.Accepted);
    }

    [Fact]
    public void LockedField_CanonicalNotOverwritten()
    {
        var store = Open();
        // Pre-save canonical with a value
        var canon = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001", Title = "ManualTitle", ScrapedAtUtc = "",
        };
        store.SaveReleaseMetadata(canon);
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);

        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "ProviderTitle")), canon);

        Assert.Equal("ManualTitle", store.LoadReleaseMetadata()["rel-001"].Title);
    }

    // ── Mixed scenario ────────────────────────────────────────────────────────

    [Fact]
    public void MixedFields_SomeAutoApplied_SomePending()
    {
        var store = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            Title       = "Existing",   // non-empty → proposal only
            Developer   = "",           // empty     → auto-apply
            ScrapedAtUtc = "",
        };
        store.SaveMetadataFieldState("rel-001", "publisher", "manual", "", locked: true);

        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss",
            P(("title", "NewTitle"), ("developer", "Dev Co"), ("publisher", "Pub Co")),
            current);

        // developer auto-applied
        Assert.Contains("developer", applied);
        Assert.Equal("Dev Co", merged.Developer);

        // title: non-empty canonical → pending proposal
        Assert.DoesNotContain("title", applied);

        // publisher: locked → pending proposal
        Assert.DoesNotContain("publisher", applied);

        var proposals = store.LoadMetadataProposals("rel-001", "ss");
        Assert.True(proposals.Find(x => x.Field == "title")?.Accepted == false);
        Assert.True(proposals.Find(x => x.Field == "developer")?.Accepted == true);
        Assert.True(proposals.Find(x => x.Field == "publisher")?.Accepted == false);
    }

    // ── Empty proposed dict → no-op ───────────────────────────────────────────

    [Fact]
    public void EmptyProposedDict_ReturnsCurrent_NoSideEffects()
    {
        var store   = Open();
        var current = EmptyRecord();
        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss", new Dictionary<string, string>(), current);

        Assert.Empty(applied);
        Assert.Same(current, merged);
        Assert.Empty(store.LoadMetadataProposals("rel-001", "ss"));
    }

    // ── All new fields handled ────────────────────────────────────────────────

    [Fact]
    public void AllNewFields_AutoAppliedWhenEmpty()
    {
        var store = Open();
        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss",
            P(("genre", "Action"), ("subgenre", "Beat 'em up"),
              ("players", "1-2"), ("rating", "T")),
            EmptyRecord());

        Assert.Contains("genre",    applied);
        Assert.Contains("subgenre", applied);
        Assert.Contains("players",  applied);
        Assert.Contains("rating",   applied);
        Assert.Equal("Action",      merged.Genre);
        Assert.Equal("Beat 'em up", merged.Subgenre);
        Assert.Equal("1-2",         merged.Players);
        Assert.Equal("T",           merged.Rating);
    }
}

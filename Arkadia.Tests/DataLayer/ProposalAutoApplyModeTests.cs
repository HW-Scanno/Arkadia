using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Tests for the autoApplyEmptyFields option on ApplyProviderProposals.
/// Verifies that true preserves existing behavior and false saves proposals
/// only without touching canonical metadata or field_state.
/// </summary>
public sealed class ProposalAutoApplyModeTests : IDisposable
{
    private readonly string _dbPath;

    public ProposalAutoApplyModeTests()
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

    private static ReleaseMetadataRecord Empty(string id = "rel-001") =>
        new() { ReleaseId = id, ScrapedAtUtc = "" };

    private static IReadOnlyDictionary<string, string> P(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    // ── autoApplyEmptyFields = true (default) ────────────────────────────────
    // These mirror the MetadataProposalFlowTests but are explicit about the flag.

    [Fact]
    public void AutoApplyTrue_EmptyCanonical_AutoApplied()
    {
        var store = Open();
        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: true);

        Assert.Contains("title", applied);
        Assert.Equal("T", merged.Title);
        Assert.Equal("T", store.LoadReleaseMetadata()["rel-001"].Title);
    }

    [Fact]
    public void AutoApplyTrue_EmptyCanonical_ProposalAccepted()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: true);

        Assert.True(store.LoadMetadataProposals("rel-001", "ss").Find(p => p.Field == "title")!.Accepted);
    }

    [Fact]
    public void AutoApplyTrue_EmptyCanonical_FieldStateWritten()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: true);

        var s = store.LoadMetadataFieldStates("rel-001").Find(x => x.Field == "title");
        Assert.NotNull(s);
        Assert.Equal("ss", s!.Source);
        Assert.False(s.Locked);
    }

    // ── autoApplyEmptyFields = false ──────────────────────────────────────────

    [Fact]
    public void AutoApplyFalse_EmptyCanonical_CanonicalNotChanged()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: false);

        Assert.False(store.LoadReleaseMetadata().ContainsKey("rel-001"));
    }

    [Fact]
    public void AutoApplyFalse_EmptyCanonical_ProposalSaved_NotAccepted()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: false);

        var p = store.LoadMetadataProposals("rel-001", "ss").Find(x => x.Field == "title");
        Assert.NotNull(p);
        Assert.Equal("T", p!.Value);
        Assert.False(p.Accepted);
    }

    [Fact]
    public void AutoApplyFalse_EmptyCanonical_NoFieldStateWritten()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: false);

        Assert.Empty(store.LoadMetadataFieldStates("rel-001"));
    }

    [Fact]
    public void AutoApplyFalse_ReturnedMergedIsCurrentUnchanged()
    {
        var store   = Open();
        var current = Empty();
        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), current, autoApplyEmptyFields: false);

        Assert.Empty(applied);
        Assert.Same(current, merged);
    }

    [Fact]
    public void AutoApplyFalse_MultipleFields_AllProposalsSaved_NoneAccepted()
    {
        var store = Open();
        store.ApplyProviderProposals(
            "rel-001", "ss",
            P(("title", "T"), ("developer", "D"), ("genre", "G")),
            Empty(),
            autoApplyEmptyFields: false);

        var proposals = store.LoadMetadataProposals("rel-001", "ss");
        Assert.Equal(3, proposals.Count);
        Assert.All(proposals, p => Assert.False(p.Accepted));
    }

    // ── Both modes: locked/manual fields always proposal-only ────────────────

    [Fact]
    public void AutoApplyTrue_LockedField_ProposalOnly()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);

        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: true);

        Assert.False(store.LoadReleaseMetadata().ContainsKey("rel-001"));
        Assert.False(store.LoadMetadataProposals("rel-001", "ss").Find(p => p.Field == "title")!.Accepted);
    }

    [Fact]
    public void AutoApplyFalse_LockedField_ProposalOnly()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);

        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty(), autoApplyEmptyFields: false);

        Assert.False(store.LoadReleaseMetadata().ContainsKey("rel-001"));
        Assert.False(store.LoadMetadataProposals("rel-001", "ss").Find(p => p.Field == "title")!.Accepted);
    }

    [Fact]
    public void AutoApplyFalse_NonEmptyCanonical_ProposalOnly()
    {
        var store   = Open();
        var current = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001", Title = "Existing", ScrapedAtUtc = "",
        };
        store.SaveReleaseMetadata(current);

        store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "Provider")), current, autoApplyEmptyFields: false);

        // Canonical unchanged
        Assert.Equal("Existing", store.LoadReleaseMetadata()["rel-001"].Title);
        // Proposal saved (non-empty canonical path)
        var p = store.LoadMetadataProposals("rel-001", "ss").Find(x => x.Field == "title");
        Assert.NotNull(p);
        Assert.False(p!.Accepted);
    }

    // ── Default parameter preserves old call sites ────────────────────────────

    [Fact]
    public void DefaultBehavior_IsAutoApplyTrue()
    {
        // Calling without the parameter must behave identically to autoApplyEmptyFields=true.
        var store = Open();

        var (merged, applied) = store.ApplyProviderProposals(
            "rel-001", "ss", P(("title", "T")), Empty());  // no explicit bool

        Assert.Contains("title", applied);
        Assert.Equal("T", merged.Title);
    }
}

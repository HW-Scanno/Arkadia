using System.Collections.Generic;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Group Append preview presentation (V-G6 micro-fix): collection terminology instead of "DAT line", and the
/// collection identity kept visible even for an empty (0-planned) plan. Pure — no UI harness. Single wording is
/// verified to remain "DAT line" (unchanged) via the plan model's own hint.
/// </summary>
public sealed class GroupAppendPresentationTests
{
    // Minimal plan hitting the "all already assigned" empty-plan branch of AppendVolumePlan.ComputeHint.
    private static AppendVolumePlan AlreadyAssignedEmptyPlan() => new()
    {
        VolumeId = "v", VolumeLabel = "c64-tosec-001", DatLineId = "", VolumeRootPath = "",
        TargetCapacityBytes = 10, TargetUsedBytes = 1, TargetFreeBytes = 9,
        TotalDerivedArtifactsForDatLine = 2, ReleaseUnwantedSkipped = 0, TotalCandidates = 2,
        AlreadyAssignedSkipped = 2, ArchiveMissingSkipped = 0, TargetCollisionSkipped = 0,
        TooLargeSkipped = 0, InvalidHashSkipped = 0, ExcludedIncomingSkipPath = 0, ExcludedZeroOrInvalidSize = 0,
        PlannedCount = 0, PlannedBytes = 0, SkippedCount = 2, RemainingTargetFreeBytes = 9, TargetBytesAfter = 1,
        LargestCandidateBytes = 0, SmallestCandidateBytes = 0, TotalCandidateBytes = 0,
        ActiveArchivePhysicalFileCount = 0, ActiveArchiveKnownWantedFileCount = 0,
        ActiveArchiveUnassignedWantedFileCount = 0,
        Entries = [], SkipReasonCounts = new Dictionary<string, int>(), CanExecute = false,
    };

    // ── §1/§5: Single wording is unchanged — the model still says "DAT line" ──

    [Fact]
    public void SingleEmptyPlan_Hint_SaysDatLine_NotCollection()
    {
        var hint = AlreadyAssignedEmptyPlan().DominantReasonHint;
        Assert.Contains("DAT line", hint);
        Assert.DoesNotContain("collection", hint);
        Assert.Contains("already assigned", hint);
    }

    // ── §2: Group rewrites the same wording to "collection" ──

    [Fact]
    public void Groupify_RewritesDatLine_ToCollection()
    {
        var single = AlreadyAssignedEmptyPlan().DominantReasonHint;
        var group  = GroupAppendPresentation.Groupify(single);

        Assert.Contains("collection", group);
        Assert.DoesNotContain("DAT line", group);
        Assert.Contains("already assigned", group);   // rest of the message intact
    }

    [Theory] // the other DAT-line hint variants are rewritten too
    [InlineData("No archive artifacts found for this DAT line.",
                "No archive artifacts found for this collection.")]
    [InlineData("No DB artifacts found for this DAT line, but 5 physical file(s) exist in the archive folder. Run Verify Archive to classify them.",
                "No DB artifacts found for this collection, but 5 physical file(s) exist in the archive folder. Run Verify Archive to classify them.")]
    public void Groupify_RewritesAllDatLineVariants(string single, string expected)
        => Assert.Equal(expected, GroupAppendPresentation.Groupify(single));

    [Fact] // a hint without "DAT line" (e.g. the executable "Ready …") is unchanged
    public void Groupify_LeavesNonMatchingHintUnchanged()
    {
        const string ready = "Ready — 5 file(s) will be copied from archive to volume (2.0 KB).";
        Assert.Equal(ready, GroupAppendPresentation.Groupify(ready));
    }

    // ── §3: Collection identity stays visible for a zero-plan case ──

    [Fact]
    public void ComposeHint_EmptyPlan_StillShowsCollectionIdentity()
    {
        var baseHint = AlreadyAssignedEmptyPlan().DominantReasonHint;   // CanExecute == false
        var line     = GroupAppendPresentation.ComposeHint("Commodore 64 TOSEC", 410, baseHint);

        Assert.Contains("Collection: Commodore 64 TOSEC", line);
        Assert.Contains("410 leaf DAT(s)", line);
        Assert.Contains("collection", line);          // collection-aware wording
        Assert.DoesNotContain("DAT line", line);
    }

    [Fact] // executable plan also shows the identity, prepended to the "Ready …" hint
    public void ComposeHint_ExecutablePlan_PrependsIdentity()
    {
        var line = GroupAppendPresentation.ComposeHint(
            "Commodore 64 TOSEC", 410, "Ready — 3 file(s) will be copied from archive to volume (1.0 KB).");

        Assert.StartsWith("Collection: Commodore 64 TOSEC", line);
        Assert.Contains("Ready — 3 file(s)", line);
    }

    [Fact] // defensive: no base hint → identity only
    public void ComposeHint_EmptyBaseHint_IdentityOnly()
        => Assert.Equal("Collection: X  ·  2 leaf DAT(s)", GroupAppendPresentation.ComposeHint("X", 2, ""));
}

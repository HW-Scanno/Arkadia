using System.Collections.Generic;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Tests for ArchiveCleanupPlanner — the per-archive cleanup policy.
/// No real ZIP extraction needed; all inputs are pure in-memory data.
/// </summary>
public sealed class ArchiveCleanupPlannerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExtractedArchiveInfo MakeArchive(string archivePath, params string[] files)
        => new(archivePath, archivePath.Replace(".zip", "/"), files);

    private static IReadOnlyDictionary<string, HashSet<string>> Touched(
        params (string archive, string[] releaseIds)[] entries)
    {
        var d = new Dictionary<string, HashSet<string>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (a, ids) in entries)
            d[a] = new HashSet<string>(ids, System.StringComparer.Ordinal);
        return d;
    }

    private static IReadOnlySet<string> Set(params string[] ids)
        => new HashSet<string>(ids, System.StringComparer.Ordinal);

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public void BothArchivesDeleted_RegardlessOfChildOutcome()
    {
        var goodZip = MakeArchive("good.zip", "good/game.iso");
        var badZip  = MakeArchive("bad.zip",  "bad/game.cue");   // missing .bin → release incomplete

        var decisions = ArchiveCleanupPlanner.Plan(
            [goodZip, badZip],
            Touched(("good.zip", ["rel-good"]), ("bad.zip", ["rel-bad"])),
            successfulReleaseIds:   Set("rel-good"),
            incompleteReleaseIds:   Set("rel-bad"),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var byPath = decisions.ToDictionary(d => d.Archive.ArchivePath);
        Assert.True(byPath["good.zip"].ShouldDelete, "good.zip must be deleted");
        Assert.True(byPath["bad.zip"].ShouldDelete,  "bad.zip must also be deleted — extraction succeeded");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithIncompleteRelease_IsDeleted()
    {
        var archive = MakeArchive("game.zip", "game/game.cue");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("game.zip", ["rel-1"])),
            successfulReleaseIds:   Set(),
            incompleteReleaseIds:   Set("rel-1"),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete);
        Assert.Contains("succeeded", d.Reason);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithTransformFailedRelease_IsDeleted()
    {
        var archive = MakeArchive("game.zip", "game/game.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("game.zip", ["rel-1"])),
            successfulReleaseIds:      Set(),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set("rel-1"),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete);
        Assert.Contains("succeeded", d.Reason);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithAllSuccessfulReleases_IsDeleted()
    {
        var archive = MakeArchive("game.zip", "game/game.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("game.zip", ["rel-1"])),
            successfulReleaseIds:      Set("rel-1"),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete);
        Assert.Contains("succeeded", d.Reason);
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithNoMatchedRelease_IsDeleted()
    {
        // Archive's files produced no entries in archiveTouchedReleaseIds — still deleted.
        var archive = MakeArchive("mystery.zip", "mystery/unknown.bin");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(),   // no entries
            successfulReleaseIds:      Set(),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete);
        Assert.Contains("succeeded", d.Reason);
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithUnmatchedExtractedFiles_IsDeleted()
    {
        // Unmatched extracted files do not prevent container deletion.
        var archive = MakeArchive("game.zip", "game/game.iso", "game/readme.txt");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("game.zip", ["rel-1"])),
            successfulReleaseIds:      Set("rel-1"),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set("game/readme.txt")); // readme didn't match

        var d = decisions.Single();
        Assert.True(d.ShouldDelete);
        Assert.Contains("succeeded", d.Reason);
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────

    [Fact]
    public void MultiReleaseArchive_DeletedOnlyWhenAllTouchedReleasesSucceeded()
    {
        // An archive that contains files for two releases; both succeed.
        var archive = MakeArchive("multi.zip", "multi/disc1.iso", "multi/disc2.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("multi.zip", ["rel-disc1", "rel-disc2"])),
            successfulReleaseIds:      Set("rel-disc1", "rel-disc2"),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete, "Both releases succeeded — archive must be deleted");
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────

    [Fact]
    public void MultiReleaseArchive_DeletedEvenIfOneReleaseIncomplete()
    {
        // An archive that contains files for two releases; one is incomplete.
        // Container is deleted regardless — extraction succeeded.
        var archive = MakeArchive("multi.zip", "multi/disc1.iso", "multi/disc2.cue");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("multi.zip", ["rel-disc1", "rel-disc2"])),
            successfulReleaseIds:      Set("rel-disc1"),  // disc1 OK, disc2 incomplete
            incompleteReleaseIds:      Set("rel-disc2"),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete, "Extraction succeeded — archive must be deleted regardless of child outcome");
        Assert.Contains("succeeded", d.Reason);
    }

    // ── Test 9 ────────────────────────────────────────────────────────────────

    [Fact]
    public void UnwantedSkippedChild_ArchiveIsDeleted()
    {
        // Release was matched but is unwanted-skipped (not in any outcome set).
        // Previously logged "release outcome unknown" and preserved; now deleted.
        var archive = MakeArchive("game.zip", "game/game.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("game.zip", ["rel-unwanted"])),
            successfulReleaseIds:      Set(),      // unwanted-skipped — not in any set
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete, "Unwanted-skipped child must not preserve the container");
        Assert.Contains("succeeded", d.Reason);
    }

    // ── Test 10 ───────────────────────────────────────────────────────────────

    [Fact]
    public void AlreadyPresentChild_ArchiveIsDeleted()
    {
        // Release was already present before this run (not added to successfulReleaseIds
        // when it is treated as already-present rather than newly succeeded).
        var archive = MakeArchive("game.zip", "game/game.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("game.zip", ["rel-present"])),
            successfulReleaseIds:      Set(),   // already-present skipped
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete, "Already-present child must not preserve the container");
    }

    // ── Test 11 ───────────────────────────────────────────────────────────────

    [Fact]
    public void MixedSuccessAndUnwanted_ArchiveIsDeleted()
    {
        // One release succeeded; the other is unwanted-skipped.
        var archive = MakeArchive("multi.zip", "multi/a.iso", "multi/b.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("multi.zip", ["rel-good", "rel-unwanted"])),
            successfulReleaseIds:      Set("rel-good"),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete, "Mixed child outcomes must not prevent container deletion");
    }

    // ── Test 12 ───────────────────────────────────────────────────────────────

    [Fact]
    public void AllChildrenUnwantedSkipped_ArchiveIsDeleted()
    {
        // All children are unwanted-skipped — nothing in any outcome set.
        var archive = MakeArchive("unwanted.zip", "u/disc1.iso", "u/disc2.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("unwanted.zip", ["rel-u1", "rel-u2"])),
            successfulReleaseIds:      Set(),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete, "All-unwanted archive must be deleted — extraction succeeded");
    }

    // ── Test 13 ───────────────────────────────────────────────────────────────

    [Fact]
    public void MultipleArchives_AllDeletedRegardlessOfChildOutcomes()
    {
        // Three archives with different child outcomes — all must be deleted.
        var zip1 = MakeArchive("a.zip", "a/game.iso");      // successful child
        var zip2 = MakeArchive("b.zip", "b/game.cue");      // incomplete child
        var zip3 = MakeArchive("c.zip", "c/game.iso");      // unwanted-skipped child

        var decisions = ArchiveCleanupPlanner.Plan(
            [zip1, zip2, zip3],
            Touched(("a.zip", ["rel-a"]), ("b.zip", ["rel-b"]), ("c.zip", ["rel-c"])),
            successfulReleaseIds:      Set("rel-a"),
            incompleteReleaseIds:      Set("rel-b"),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        Assert.Equal(3, decisions.Count);
        Assert.All(decisions, d => Assert.True(d.ShouldDelete));
        Assert.All(decisions, d => Assert.Contains("succeeded", d.Reason));
    }

    // ── Test 14 ───────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyArchiveList_ReturnsEmptyDecisions()
    {
        var decisions = ArchiveCleanupPlanner.Plan(
            [],
            Touched(),
            successfulReleaseIds:      Set(),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        Assert.Empty(decisions);
    }

    // ── Test 15 ───────────────────────────────────────────────────────────────

    [Fact]
    public void AllChildrenMovedToIncomingSkip_ArchiveIsDeleted()
    {
        // Children were unwanted and moved to incoming-skip — not in successfulReleaseIds.
        // The container must still be deleted.
        var archive = MakeArchive("skip.zip", "skip/game.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("skip.zip", ["rel-skip"])),
            successfulReleaseIds:      Set(),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete, "Container must be deleted even when children moved to incoming-skip");
    }

    // ── Test 16 ───────────────────────────────────────────────────────────────

    [Fact]
    public void ReasonAlwaysContainsExtractionSucceeded()
    {
        // Regardless of inputs, the reason must communicate extraction-based deletion.
        var archive = MakeArchive("any.zip", "any/file.iso");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("any.zip", ["rel-1"])),
            successfulReleaseIds:      Set("rel-1"),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.True(d.ShouldDelete);
        Assert.Equal("extraction succeeded", d.Reason);
    }
}

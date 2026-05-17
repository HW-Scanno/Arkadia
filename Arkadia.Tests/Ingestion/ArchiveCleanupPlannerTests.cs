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
    public void OneIncompleteArchive_DeletesOtherSuccessfulArchives()
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
        Assert.False(byPath["bad.zip"].ShouldDelete,  "bad.zip must be preserved");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithIncompleteRelease_IsPreserved()
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
        Assert.False(d.ShouldDelete);
        Assert.Contains("incomplete", d.Reason);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithTransformFailedRelease_IsPreserved()
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
        Assert.False(d.ShouldDelete);
        Assert.Contains("transform", d.Reason);
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
    public void ArchiveWithNoMatchedRelease_IsPreserved()
    {
        // Archive's files produced no entries in archiveTouchedReleaseIds.
        var archive = MakeArchive("mystery.zip", "mystery/unknown.bin");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(),   // no entries
            successfulReleaseIds:      Set(),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.False(d.ShouldDelete);
        Assert.Contains("no matched release", d.Reason);
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWithUnmatchedExtractedFiles_IsPreserved()
    {
        // Archive has a file that matched a DAT release AND a file that didn't.
        var archive = MakeArchive("game.zip", "game/game.iso", "game/readme.txt");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("game.zip", ["rel-1"])),
            successfulReleaseIds:      Set("rel-1"),
            incompleteReleaseIds:      Set(),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set("game/readme.txt")); // readme didn't match

        var d = decisions.Single();
        Assert.False(d.ShouldDelete);
        Assert.Contains("unmatched", d.Reason);
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
    public void MultiReleaseArchive_PreservedIfAnyTouchedReleaseIncomplete()
    {
        // An archive that contains files for two releases; one is incomplete.
        var archive = MakeArchive("multi.zip", "multi/disc1.iso", "multi/disc2.cue");

        var decisions = ArchiveCleanupPlanner.Plan(
            [archive],
            Touched(("multi.zip", ["rel-disc1", "rel-disc2"])),
            successfulReleaseIds:      Set("rel-disc1"),  // disc1 OK, disc2 incomplete
            incompleteReleaseIds:      Set("rel-disc2"),
            transformFailedReleaseIds: Set(),
            unmatchedExtractedFiles:   Set());

        var d = decisions.Single();
        Assert.False(d.ShouldDelete, "One incomplete release must preserve the entire archive");
        Assert.Contains("incomplete", d.Reason);
    }
}

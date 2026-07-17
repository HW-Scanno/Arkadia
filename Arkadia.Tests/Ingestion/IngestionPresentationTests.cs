using System;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Presentation/counter contract tests for the ingestion log formatter, the
/// shared summary counter set, and the post-run refresh gate. These exercise the
/// real formatter/helper production code (not reimplementations), so they catch
/// label/counter regressions without needing the full ingestion pipeline.
/// </summary>
public sealed class IngestionPresentationTests
{
    private static string BuildLog(IngestionResult r) =>
        IngestionLogFormatter.Build("ps2-redump", r, new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc));

    // ── 1 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionLog_PrintsUnwantedSkippedCount()
    {
        var r   = new IngestionResult { UnwantedSkipped = 7 };
        var log = BuildLog(r);
        Assert.Contains("Unwanted skipped:", log);
        Assert.Matches(@"Unwanted skipped:\s*7", log);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionLog_FilesCopiedLabel_IsFilesStaged()
    {
        var log = BuildLog(new IngestionResult { FilesCopied = 3 });
        Assert.Contains("Files staged:", log);
        Assert.DoesNotContain("Files copied", log);
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionLog_DistinguishesUnwantedClassifiedFromUnwantedMoved()
    {
        var r = new IngestionResult { UnwantedSkipped = 1 };
        r.Operations.Add(new IngestionOperation("game.iso", "unwanted-classified", "Some Unwanted Release"));
        r.Operations.Add(new IngestionOperation("game.iso", "unwanted-moved", "incoming-skip/ps2/game.iso"));

        var log = BuildLog(r);

        Assert.Contains("unwanted-classified", log);
        Assert.Contains("unwanted-moved", log);
        // They are distinct actions, not a duplicated single label.
        Assert.NotEqual(
            r.Operations[0].Action,
            r.Operations[1].Action);
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionSummary_AllUnwantedRun_DoesNotOnlySayFilesSkippedZero()
    {
        // Everything was unwanted: FilesSkipped stays 0 but UnwantedSkipped is high.
        var r   = new IngestionResult { FilesScanned = 5, FilesMatched = 5, UnwantedSkipped = 5 };
        var log = BuildLog(r);

        Assert.Matches(@"Files skipped:\s*0", log);
        Assert.Matches(@"Unwanted skipped:\s*5", log);
        // The clarifying note must make the outcome unambiguous.
        Assert.Contains("no wanted releases acquired", log);
        Assert.Contains("moved to incoming-skip", log);
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionSummary_DialogAndLogShareCoreCounters()
    {
        var r = new IngestionResult
        {
            FilesScanned = 10, FilesMatched = 8, FilesCopied = 6,
            ReleaseInputsAssembled = 4, DerivedArtifactsCreated = 4, AlreadyPresent = 1,
            ReleasesPresent = 4, ReleasesIncomplete = 1, FilesSkipped = 1,
            UnwantedSkipped = 1, TransformsFailed = 0, FilesDeletedFromIncoming = 2,
        };

        var core = IngestionSummary.CoreCounters(r);
        var log  = BuildLog(r);

        // Every core counter the dialog renders must also appear in the log.
        foreach (var (label, _) in core)
            Assert.Contains(label + ":", log);

        // The shared set is exactly the agreed core counters, in order.
        var expected = new[]
        {
            "Files scanned", "Files matched", "Files staged", "Release inputs assembled",
            "Derived artifacts created", "Already present", "Releases present",
            "Releases incomplete", "Files skipped", "Unwanted skipped",
            "Transforms failed", "Archives deleted",
        };
        Assert.Equal(expected, core.Select(c => c.Label).ToArray());
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionResult_CanRepresentDerivedArtifactsCreated_IfImplemented()
    {
        var r = new IngestionResult { DerivedArtifactsCreated = 12 };
        Assert.Equal(12, r.DerivedArtifactsCreated);

        var pair = IngestionSummary.CoreCounters(r)
            .Single(c => c.Label == "Derived artifacts created");
        Assert.Equal("12", pair.Value);
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionResult_CanRepresentAlreadyPresent_IfImplemented()
    {
        var r = new IngestionResult { AlreadyPresent = 9 };
        Assert.Equal(9, r.AlreadyPresent);

        var pair = IngestionSummary.CoreCounters(r)
            .Single(c => c.Label == "Already present");
        Assert.Equal("9", pair.Value);
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionRefresh_AllUnwantedRun_RefreshesWhenUnwantedSkippedPositive()
    {
        // All-unwanted run: only UnwantedSkipped > 0 — must still trigger a refresh.
        var allUnwanted = new IngestionResult { UnwantedSkipped = 3 };
        Assert.True(IngestionSummary.ShouldRefreshAfterIngest(allUnwanted));

        // Truly empty run: nothing changed → no refresh.
        var empty = new IngestionResult();
        Assert.False(IngestionSummary.ShouldRefreshAfterIngest(empty));

        // Error run never refreshes even if counters are set.
        var errored = new IngestionResult { UnwantedSkipped = 3, Error = "disk full" };
        Assert.False(IngestionSummary.ShouldRefreshAfterIngest(errored));
    }

    // ── Bonus: release-input-assembled label reflects staging→source rename ────

    [Fact]
    public void IngestionSummary_ReleaseInputsAssembled_IsAcoreCounter()
    {
        var r = new IngestionResult { ReleaseInputsAssembled = 5 };
        Assert.Contains(IngestionSummary.CoreCounters(r), c => c.Label == "Release inputs assembled" && c.Value == "5");
    }

    // ── Stale-cleanup rows appear only when they occurred ─────────────────────

    [Fact]
    public void IngestionSummary_StaleCleanupRows_OnlyShownWhenNonZero()
    {
        // Zero cleanup → base 12 counters, no stale rows.
        var none = IngestionSummary.CoreCounters(new IngestionResult());
        Assert.Equal(12, none.Count);
        Assert.DoesNotContain(none, c => c.Label == "Stale staging moved");
        Assert.DoesNotContain(none, c => c.Label == "Stale source moved");

        // Non-zero cleanup → the relevant rows are appended.
        var withStale = IngestionSummary.CoreCounters(
            new IngestionResult { StaleStagingMoved = 2, StaleSourceMoved = 3 });
        Assert.Contains(withStale, c => c.Label == "Stale staging moved" && c.Value == "2");
        Assert.Contains(withStale, c => c.Label == "Stale source moved"  && c.Value == "3");
    }
}

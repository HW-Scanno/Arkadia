using System.Collections.Generic;
using Arkadia;
using Xunit;

namespace Arkadia.Tests.Amp;

public sealed class AmpReportHelpersTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AmpExportPlan EmptyPlan() => new(
        HardwareFamilyId:     "snes",
        DatLineId:            "snes-nointro",
        SystemName:           "Super Nintendo",
        ReleaseCount:         0,
        ReleasesWithMetadata: 0,
        ReleasesWithMedia:    0,
        TotalMediaFiles:      0,
        TotalBytes:           0L,
        ExclusionCount:       0,
        ExtraNotesCount:      0,
        Releases:             [],
        Issues:               []);

    private static AmpExportPlan PlanWith(
        int releaseCount,
        int withMeta,
        int withMedia,
        IReadOnlyList<AmpExportPlanRelease>? releases = null,
        IReadOnlyList<AmpExportPlanIssue>?   issues   = null) =>
        new(
            HardwareFamilyId:     "snes",
            DatLineId:            "snes-nointro",
            SystemName:           "Super Nintendo",
            ReleaseCount:         releaseCount,
            ReleasesWithMetadata: withMeta,
            ReleasesWithMedia:    withMedia,
            TotalMediaFiles:      0,
            TotalBytes:           0L,
            ExclusionCount:       0,
            ExtraNotesCount:      0,
            Releases:             releases ?? [],
            Issues:               issues   ?? []);

    private static AmpExportPlanRelease ReleaseWithIssues(
        IReadOnlyList<AmpExportPlanIssue> issues) =>
        new(
            ReleaseId:       "rel-001",
            DatName:         "Game (USA)",
            Title:           "Game",
            OriginalTitle:   "",
            SortTitle:       "",
            Developer:       "",
            Publisher:       "",
            Year:            "",
            Languages:       "",
            AlternateTitles: "",
            Description:     "",
            Genre:           "",
            Subgenre:        "",
            Players:         "",
            ReleaseType:     "",
            Rating:          "",
            HasMetadata:     true,
            MediaEntries:    [],
            ExclusionHashes: [],
            ExtraNotes:      null,
            Issues:          issues);

    // ── GetMetadataPercent ────────────────────────────────────────────────────

    [Fact]
    public void GetMetadataPercent_EmptyPlan_ReturnsZero()
    {
        Assert.Equal(0, AmpReportHelpers.GetMetadataPercent(EmptyPlan()));
    }

    [Fact]
    public void GetMetadataPercent_FullCoverage_Returns100()
    {
        var plan = PlanWith(releaseCount: 10, withMeta: 10, withMedia: 0);
        Assert.Equal(100, AmpReportHelpers.GetMetadataPercent(plan));
    }

    [Fact]
    public void GetMetadataPercent_PartialCoverage_ReturnsIntegerPercent()
    {
        var plan = PlanWith(releaseCount: 4, withMeta: 3, withMedia: 0);
        Assert.Equal(75, AmpReportHelpers.GetMetadataPercent(plan));
    }

    // ── GetMediaPercent ───────────────────────────────────────────────────────

    [Fact]
    public void GetMediaPercent_EmptyPlan_ReturnsZero()
    {
        Assert.Equal(0, AmpReportHelpers.GetMediaPercent(EmptyPlan()));
    }

    [Fact]
    public void GetMediaPercent_FullCoverage_Returns100()
    {
        var plan = PlanWith(releaseCount: 5, withMeta: 0, withMedia: 5);
        Assert.Equal(100, AmpReportHelpers.GetMediaPercent(plan));
    }

    // ── GetErrorCount ─────────────────────────────────────────────────────────

    [Fact]
    public void GetErrorCount_NoIssues_ReturnsZero()
    {
        Assert.Equal(0, AmpReportHelpers.GetErrorCount(EmptyPlan()));
    }

    [Fact]
    public void GetErrorCount_CountsOnlyErrors()
    {
        var issues = new AmpExportPlanIssue[]
        {
            new(AmpExportPlanSeverity.Error,   "media", "bad file"),
            new(AmpExportPlanSeverity.Warning, "media", "missing cover"),
            new(AmpExportPlanSeverity.Info,    "meta",  "note"),
            new(AmpExportPlanSeverity.Error,   "archive", "duplicate path"),
        };
        var plan = PlanWith(
            releaseCount: 1, withMeta: 1, withMedia: 1,
            releases: [ReleaseWithIssues(issues)]);

        Assert.Equal(2, AmpReportHelpers.GetErrorCount(plan));
    }

    // ── GetWarningCount ───────────────────────────────────────────────────────

    [Fact]
    public void GetWarningCount_CountsOnlyWarnings()
    {
        var issues = new AmpExportPlanIssue[]
        {
            new(AmpExportPlanSeverity.Error,   "media", "bad file"),
            new(AmpExportPlanSeverity.Warning, "media", "missing cover"),
            new(AmpExportPlanSeverity.Warning, "dedup", "hash match"),
        };
        var plan = PlanWith(
            releaseCount: 1, withMeta: 1, withMedia: 1,
            releases: [ReleaseWithIssues(issues)]);

        Assert.Equal(2, AmpReportHelpers.GetWarningCount(plan));
    }

    // ── GetInfoCount ──────────────────────────────────────────────────────────

    [Fact]
    public void GetInfoCount_CountsOnlyInfo()
    {
        var issues = new AmpExportPlanIssue[]
        {
            new(AmpExportPlanSeverity.Error, "media", "bad"),
            new(AmpExportPlanSeverity.Info,  "meta",  "note 1"),
            new(AmpExportPlanSeverity.Info,  "meta",  "note 2"),
        };
        var plan = PlanWith(
            releaseCount: 1, withMeta: 1, withMedia: 1,
            releases: [ReleaseWithIssues(issues)]);

        Assert.Equal(2, AmpReportHelpers.GetInfoCount(plan));
    }

    // ── GetInfoCount includes plan-level issues ───────────────────────────────

    [Fact]
    public void GetErrorCount_IncludesPlanLevelIssues()
    {
        var planIssue = new AmpExportPlanIssue(AmpExportPlanSeverity.Error, "plan", "plan error");
        var plan = PlanWith(
            releaseCount: 0, withMeta: 0, withMedia: 0,
            issues: [planIssue]);

        Assert.Equal(1, AmpReportHelpers.GetErrorCount(plan));
    }

    // ── SuggestedAmpFileName ──────────────────────────────────────────────────

    private static AmpExportPlan NamedPlan(string systemName, string datLineId) => new(
        HardwareFamilyId:     "test",
        DatLineId:            datLineId,
        SystemName:           systemName,
        ReleaseCount:         0,
        ReleasesWithMetadata: 0,
        ReleasesWithMedia:    0,
        TotalMediaFiles:      0,
        TotalBytes:           0L,
        ExclusionCount:       0,
        ExtraNotesCount:      0,
        Releases:             [],
        Issues:               []);

    [Fact]
    public void SuggestedAmpFileName_Standard_ReturnsExpected()
    {
        var plan = NamedPlan("Super Nintendo", "snes-nointro");
        Assert.Equal("Super-Nintendo-snes-nointro.amp", AmpReportHelpers.SuggestedAmpFileName(plan));
    }

    [Fact]
    public void SuggestedAmpFileName_WithSpaces_ReplacesWithDash()
    {
        var plan = NamedPlan("Game Boy Advance", "gba");
        Assert.Equal("Game-Boy-Advance-gba.amp", AmpReportHelpers.SuggestedAmpFileName(plan));
    }

    [Fact]
    public void SuggestedAmpFileName_WithInvalidChars_ReplacesWithDash()
    {
        var plan = NamedPlan("A:B<C>", "dat");
        Assert.Equal("A-B-C-dat.amp", AmpReportHelpers.SuggestedAmpFileName(plan));
    }

    [Fact]
    public void SuggestedAmpFileName_CollapsesDuplicateDashes()
    {
        var plan = NamedPlan("Test", "--double");
        Assert.Equal("Test-double.amp", AmpReportHelpers.SuggestedAmpFileName(plan));
    }

    [Fact]
    public void SuggestedAmpFileName_AllWhitespace_FallsBackToDefault()
    {
        var plan = NamedPlan("   ", "   ");
        Assert.Equal("Arkadia-Media-Pack.amp", AmpReportHelpers.SuggestedAmpFileName(plan));
    }

    [Fact]
    public void SuggestedAmpFileName_AlwaysEndsWithAmpExtension()
    {
        var plan = NamedPlan("Mega Drive", "md-nointro");
        Assert.EndsWith(".amp", AmpReportHelpers.SuggestedAmpFileName(plan));
    }

    // ── FormatBytes ───────────────────────────────────────────────────────────

    [Fact]
    public void FormatBytes_Zero_ReturnsZeroB()
    {
        Assert.Equal("0 B", AmpReportHelpers.FormatBytes(0));
    }

    [Fact]
    public void FormatBytes_Bytes_ReturnsB()
    {
        Assert.Equal("512 B", AmpReportHelpers.FormatBytes(512));
    }

    [Fact]
    public void FormatBytes_Kilobytes_ReturnsKB()
    {
        var result = AmpReportHelpers.FormatBytes(2048);
        Assert.Contains("KB", result);
        Assert.Contains("2.0", result);
    }

    [Fact]
    public void FormatBytes_Megabytes_ReturnsMB()
    {
        var result = AmpReportHelpers.FormatBytes(5 * 1024 * 1024);
        Assert.Contains("MB", result);
        Assert.Contains("5.0", result);
    }
}

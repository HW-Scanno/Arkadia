using System;
using System.IO;
using Arkadia.Data;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// Systems coverage semantics: coverage answers "of the releases I want to keep, how
/// complete is this system?", so the denominator is the WANTED subset (total minus the
/// unwanted curator veto). Unwanted is reported separately as an exclusion share.
///
/// Formula tests exercise the production calculator (<see cref="SystemPlatform"/>);
/// the hidden/lost tests exercise the production query (<see cref="DatLineStore.GetAllStatusCounts"/>)
/// that feeds the unwanted count into that calculator. No calculation is reimplemented locally.
/// </summary>
public sealed class SystemPlatformCoverageTests : IDisposable
{
    private readonly string _tmp;

    public SystemPlatformCoverageTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ArkSysCov_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    // ── production calculator under test ──────────────────────────────────────
    private static SystemPlatform Platform(int total, int present, int unwanted,
                                           int outdated = 0, int lost = 0) => new()
    {
        Id = "sys", Name = "Sys", Manufacturer = "M", HardwareType = "console",
        DatLines = 1, TotalTitles = total, Present = present, Outdated = outdated,
        Missing = Math.Max(0, Math.Max(0, total - unwanted) - present - outdated - lost),
        Lost = lost, Unwanted = unwanted,
    };

    // ── model formula tests ───────────────────────────────────────────────────

    [Fact]
    public void SystemsCoverage_ExcludesUnwantedFromWantedCoverageDenominator()
    {
        // 5 present of 10 total, 5 unwanted → wanted = 5, all present → 100% (NOT 50%).
        var p = Platform(total: 10, present: 5, unwanted: 5);
        Assert.Equal(5, p.WantedTitles);
        Assert.Equal(100, p.WantedCoveragePercent);   // gross would be 50%
    }

    [Fact]
    public void SystemsCoverage_WantedAllPresent_UnwantedIgnored_Coverage100()
    {
        var p = Platform(total: 8, present: 5, unwanted: 3);
        Assert.Equal(5, p.WantedTitles);
        Assert.Equal(100, p.WantedCoveragePercent);
        Assert.Equal("100%", p.WantedCoverage);
    }

    [Fact]
    public void SystemsCoverage_WantedMissing_ReducesWantedCoverage()
    {
        // No unwanted: 7 present of 10 wanted → 70%.
        var p = Platform(total: 10, present: 7, unwanted: 0);
        Assert.Equal(70, p.WantedCoveragePercent);
        Assert.Equal(3, p.Missing);
    }

    [Fact]
    public void SystemsCoverage_UnwantedShare_ComputedOverTotalReleases()
    {
        // Share is over ALL releases, not the wanted subset: 4 of 10 = 40%.
        var p = Platform(total: 10, present: 3, unwanted: 4);
        Assert.Equal(40, p.UnwantedSharePercent);
        Assert.Equal("40%", p.UnwantedShare);
    }

    [Fact]
    public void SystemsCoverage_AllUnwanted_WantedCoverageIsNotMisleading()
    {
        // Every release vetoed → no wanted denominator. Must read N/A, not a scary 0%.
        var p = Platform(total: 5, present: 0, unwanted: 5);
        Assert.Equal(0, p.WantedTitles);
        Assert.Null(p.WantedCoveragePercent);
        Assert.Equal("N/A", p.WantedCoverage);
        Assert.Equal(100, p.UnwantedSharePercent);
        Assert.Equal("100%", p.UnwantedShare);
    }

    [Fact]
    public void SystemsCoverage_NoUnwanted_MatchesPreviousCoverage()
    {
        // With no unwanted the wanted denominator equals the total, so the value is
        // identical to the previous present/total coverage (integer percent).
        var p = Platform(total: 4, present: 3, unwanted: 0);
        Assert.Equal(p.TotalTitles, p.WantedTitles);
        Assert.Equal(75, p.WantedCoveragePercent);    // previous formula: 3 * 100 / 4
        Assert.Equal("75%", p.WantedCoverage);
        Assert.Equal(0, p.UnwantedSharePercent);
    }

    [Fact]
    public void SystemsCoverage_ZeroReleases_IsNotMisleading()
    {
        var p = Platform(total: 0, present: 0, unwanted: 0);
        Assert.Equal("N/A", p.WantedCoverage);
        Assert.Equal("—", p.UnwantedShare);
    }

    // ── production-query tests (the count that feeds the calculator) ───────────

    [Fact]
    public void SystemsCoverage_HiddenWanted_NotExcludedUnlessUnwanted()
    {
        var store = new DatLineStore(Path.Combine(_tmp, "hidden.db"));
        // Hidden (show_in_catalog = false) but still wanted (status = missing).
        store.UpsertRelease(new ReleaseRecord { Id = "h", DatLineId = "dl", Name = "Hidden",
                                                Status = "missing", ShowInCatalog = false });
        store.UpsertRelease(new ReleaseRecord { Id = "p", DatLineId = "dl", Name = "Present",
                                                Status = "present", ShowInCatalog = true });
        store.UpsertRelease(new ReleaseRecord { Id = "u", DatLineId = "dl", Name = "Unwanted",
                                                Status = "unwanted", ShowInCatalog = false });

        var c = store.GetAllStatusCounts();
        Assert.Equal(1, c.Unwanted);          // only the vetoed row — hidden is NOT unwanted
        Assert.Equal(1, c.Missing);           // hidden-wanted still counted

        // Feed the production query into the production calculator: total = 3 releases.
        var p = Platform(total: 3, present: c.Present, unwanted: c.Unwanted);
        Assert.Equal(2, p.WantedTitles);      // hidden-wanted stays in the denominator
        Assert.Equal(50, p.WantedCoveragePercent);   // 100% only if hidden were wrongly excluded
    }

    [Fact]
    public void SystemsCoverage_MiaWanted_IsSurfacedSeparately_IfSupported()
    {
        // Arkadia has no distinct "MIA" status; 'lost' is the separate MIA-like
        // classification and must be surfaced on its own, not folded into unwanted.
        var store = new DatLineStore(Path.Combine(_tmp, "mia.db"));
        store.UpsertRelease(new ReleaseRecord { Id = "l", DatLineId = "dl", Name = "Lost",     Status = "lost" });
        store.UpsertRelease(new ReleaseRecord { Id = "p", DatLineId = "dl", Name = "Present",  Status = "present" });
        store.UpsertRelease(new ReleaseRecord { Id = "u", DatLineId = "dl", Name = "Unwanted", Status = "unwanted" });

        var c = store.GetAllStatusCounts();
        Assert.Equal(1, c.Lost);              // surfaced separately
        Assert.Equal(1, c.Unwanted);          // not conflated with lost

        // 'lost' is a wanted release: it counts toward the wanted denominator.
        var p = Platform(total: 3, present: c.Present, unwanted: c.Unwanted, lost: c.Lost);
        Assert.Equal(2, p.WantedTitles);
        Assert.Equal(1, p.Lost);
        Assert.Equal(50, p.WantedCoveragePercent);
    }
}

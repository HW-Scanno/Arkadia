using System;
using System.IO;
using Arkadia.Data;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Purge;

/// <summary>
/// Tests for analytics semantics with the Unwanted status.
/// Verifies wanted/DAT coverage math and denominator exclusions.
/// </summary>
public sealed class PurgeAnalyticsTests : IDisposable
{
    private readonly string _dbPath;

    public PurgeAnalyticsTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ArkPurgeAnalytics_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private DatLineStore Open() => new(_dbPath);

    private void SaveRelease(DatLineStore store, string id, string status) =>
        store.SaveReleases(new System.Collections.Generic.List<ReleaseRecord>
        {
            new() { Id = id, DatLineId = "dl1", Name = id, Status = status }
        });

    // ── Test 1: UnwantedExcludedFromWantedCoverage ────────────────────────────

    [Fact]
    public void UnwantedExcludedFromWantedCoverage()
    {
        var store = Open();

        // 10 total: 8 present + 1 missing + 1 unwanted
        var releases = new System.Collections.Generic.List<ReleaseRecord>();
        for (int i = 0; i < 8;  i++) releases.Add(new() { Id = $"p{i}", DatLineId = "dl1", Name = $"p{i}", Status = "present"  });
        releases.Add(new() { Id = "m1", DatLineId = "dl1", Name = "m1", Status = "missing"  });
        releases.Add(new() { Id = "u1", DatLineId = "dl1", Name = "u1", Status = "unwanted" });
        store.SaveReleases(releases);

        var (missing, pending, outdated, present, lost, unwanted) = store.GetAllStatusCounts();

        int total   = missing + pending + outdated + present + lost + unwanted;
        int wanted  = total - unwanted;
        double wantedCovPct = wanted > 0 ? present * 100.0 / wanted : 0.0;

        Assert.Equal(10, total);
        Assert.Equal(9,  wanted);          // 10 - 1 unwanted
        Assert.Equal(1,  unwanted);
        Assert.Equal(8,  present);
        Assert.InRange(wantedCovPct, 88.88, 88.9);  // 8/9
    }

    // ── Test 2: FullDatCoverageStillCountsUnwanted ────────────────────────────

    [Fact]
    public void FullDatCoverageStillCountsUnwanted()
    {
        var store = Open();

        var releases = new System.Collections.Generic.List<ReleaseRecord>
        {
            new() { Id = "p1", DatLineId = "dl1", Name = "p1", Status = "present"  },
            new() { Id = "p2", DatLineId = "dl1", Name = "p2", Status = "present"  },
            new() { Id = "u1", DatLineId = "dl1", Name = "u1", Status = "unwanted" },
        };
        store.SaveReleases(releases);

        var (_, _, _, present, _, unwanted) = store.GetAllStatusCounts();
        int total = 3;
        // Full DAT coverage: (present + unwanted) / total = 3/3 = 100%
        double fullCovPct = (present + unwanted) * 100.0 / total;

        Assert.Equal(100.0, fullCovPct, precision: 5);
    }

    // ── Test 3: PresentWantedCoverageUsesWantedDenominator ───────────────────

    [Fact]
    public void PresentWantedCoverageUsesWantedDenominator()
    {
        var store = Open();

        var releases = new System.Collections.Generic.List<ReleaseRecord>
        {
            new() { Id = "p1", DatLineId = "dl1", Name = "p1", Status = "present"  },
            new() { Id = "p2", DatLineId = "dl1", Name = "p2", Status = "present"  },
            new() { Id = "m1", DatLineId = "dl1", Name = "m1", Status = "missing"  },
            new() { Id = "u1", DatLineId = "dl1", Name = "u1", Status = "unwanted" },
            new() { Id = "u2", DatLineId = "dl1", Name = "u2", Status = "unwanted" },
        };
        store.SaveReleases(releases);

        var (missing, _, _, present, _, unwanted) = store.GetAllStatusCounts();
        int total  = 5;
        int wanted = total - unwanted;

        Assert.Equal(3, wanted);     // 5 - 2 unwanted
        Assert.Equal(2, present);
        // Coverage = 2/3, not 2/5
        double wantedCovPct = present * 100.0 / wanted;
        Assert.InRange(wantedCovPct, 66.6, 66.8);
    }

    // ── Test 4: RestoreWantedReincludesReleaseInCoverage ─────────────────────

    [Fact]
    public void RestoreWantedReincludesReleaseInCoverage()
    {
        var store = Open();

        store.SaveReleases(new System.Collections.Generic.List<ReleaseRecord>
        {
            new() { Id = "r1", DatLineId = "dl1", Name = "r1", Status = "unwanted" },
        });

        var (_, _, _, _, _, unwantedBefore) = store.GetAllStatusCounts();
        Assert.Equal(1, unwantedBefore);

        // Restore by setting status to missing
        store.UpdateReleaseStatus("r1", "missing");

        var (missing, _, _, _, _, unwantedAfter) = store.GetAllStatusCounts();
        Assert.Equal(0, unwantedAfter);
        Assert.Equal(1, missing);
    }
}

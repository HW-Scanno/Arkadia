using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Tests for the DB-authoritative pre-staging admission guard
/// (<see cref="RedundantIncomingPolicy"/>) used in ingestion Phase 4b.
///
/// Product decision: staging admission trusts the database and does NOT probe the
/// filesystem. A release is satisfied (non-stageable) when the DB says it is
/// <c>present</c> AND has at least one <c>derived_artifacts</c> row — regardless of where
/// that artifact is recorded (local archive, reachable volume, offline assigned volume,
/// legacy relative_path). Physical existence/integrity is out of scope here; that is
/// reconciled by Verify Archive / Verify Volume, not by normal ingestion.
/// </summary>
public sealed class RedundantIncomingPolicyTests
{
    // ── Satisfied: present + ≥1 derived artifact row ──────────────────────────

    [Fact]
    public void PresentWithDerivedArtifact_IsSatisfied()
    {
        // Present release whose DB carries a derived artifact → the incoming .cue is redundant,
        // so its target is satisfied and NO staging folder is created for it.
        Assert.Equal(ReleaseSatisfaction.Satisfied,
            RedundantIncomingPolicy.Locate("present", derivedArtifactRowCount: 1));
    }

    [Fact]
    public void PresentWithMultipleArtifactRows_IsSatisfied()
    {
        Assert.Equal(ReleaseSatisfaction.Satisfied,
            RedundantIncomingPolicy.Locate("present", derivedArtifactRowCount: 3));
    }

    [Fact]
    public void Satisfaction_IsDbOnly_LocationIrrelevant()
    {
        // The whole point of the DB-authoritative model: satisfaction is decided from DB
        // state alone. There is no location/probe input — a present release with an artifact
        // row is satisfied whether that artifact lives in local archive, on a reachable
        // volume, on an OFFLINE assigned volume, or under a legacy relative_path.
        Assert.Equal(ReleaseSatisfaction.Satisfied,
            RedundantIncomingPolicy.Locate("present", derivedArtifactRowCount: 1));
    }

    [Fact]
    public void Decision_IsIdempotent()
    {
        // Stable across repeated runs: a present+derived release stays satisfied, so
        // re-ingesting the .cue keeps duplicate-deleting it rather than re-staging.
        Assert.Equal(ReleaseSatisfaction.Satisfied, RedundantIncomingPolicy.Locate("present", 1));
        Assert.Equal(ReleaseSatisfaction.Satisfied, RedundantIncomingPolicy.Locate("present", 1));
    }

    // ── PresentWithoutArtifact: inconsistent DB state ─────────────────────────

    [Fact]
    public void PresentWithNoArtifactRow_IsPresentWithoutArtifact_NotSatisfied()
    {
        // status 'present' but zero derived_artifacts rows → inconsistent state. NOT satisfied
        // for admission (may stage so a real artifact can be produced); flagged for logging.
        Assert.Equal(ReleaseSatisfaction.PresentWithoutArtifact,
            RedundantIncomingPolicy.Locate("present", derivedArtifactRowCount: 0));
    }

    // ── NotSatisfied: any non-present status ──────────────────────────────────

    [Theory]
    [InlineData("missing")]
    [InlineData("pending")]
    [InlineData("")]
    public void NonPresentStatus_IsNotSatisfied_RegardlessOfArtifactRows(string status)
    {
        // A release still being acquired is never short-circuited — its constituent files
        // must stage so it can complete, even if a stale artifact row somehow exists.
        Assert.Equal(ReleaseSatisfaction.NotSatisfied, RedundantIncomingPolicy.Locate(status, 0));
        Assert.Equal(ReleaseSatisfaction.NotSatisfied, RedundantIncomingPolicy.Locate(status, 2));
    }

    [Fact]
    public void UnwantedStatus_IsNotSatisfiedByThisPolicy()
    {
        // Unwanted is handled by the UNWANTED-WINS filter, not by satisfaction. This policy
        // simply reports NotSatisfied for it (a non-'present' status).
        Assert.Equal(ReleaseSatisfaction.NotSatisfied,
            RedundantIncomingPolicy.Locate("unwanted", derivedArtifactRowCount: 1));
    }

    [Fact]
    public void MissingReleaseWithNoArtifact_Stages()
    {
        // Wanted missing release, no derived artifact → stageable if incoming material is useful.
        Assert.Equal(ReleaseSatisfaction.NotSatisfied,
            RedundantIncomingPolicy.Locate("missing", derivedArtifactRowCount: 0));
    }
}

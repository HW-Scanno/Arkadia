using System.Collections.Generic;
using Arkadia.Data.Identifiers;
using Xunit;

namespace Arkadia.Tests.Data.Identifiers;

/// <summary>
/// Tests for <see cref="DatGroupId"/> and <see cref="DatLineId"/>: strict new-id creation vs
/// verbatim persisted loading, equality/hashing, the case-insensitive collision comparer, and
/// the non-interchangeability of the two id types.
/// </summary>
public sealed class DatTechnicalIdValueObjectTests
{
    // ── New vs persisted ──────────────────────────────────────────────────────

    [Fact]
    public void TryCreateNew_CanonicalInput_Succeeds()
    {
        Assert.True(DatGroupId.TryCreateNew("tosec-c64", out var id, out var error, out var warn));
        Assert.Equal(DatTechnicalIdError.None, error);
        Assert.False(warn);
        Assert.Equal("tosec-c64", id.Value);
        Assert.True(id.ConformsToNewPolicy);
    }

    [Fact]
    public void TryCreateNew_Uppercase_IsRejectedNotRewritten()
    {
        Assert.False(DatGroupId.TryCreateNew("TOSEC-C64", out var id, out var error, out _));
        Assert.Equal(DatTechnicalIdError.NotCanonical, error);
        Assert.Equal(default, id);              // no silent lowercasing into a different id
        Assert.Null(id.Value);
    }

    [Fact]
    public void TryCreateNew_Line_Uppercase_IsRejected()
    {
        Assert.False(DatLineId.TryCreateNew("TOSEC-C64", out _, out var error, out _));
        Assert.Equal(DatTechnicalIdError.NotCanonical, error);
    }

    [Fact]
    public void FromPersisted_PreservesLegacyValueVerbatim()
    {
        var id = DatGroupId.FromPersisted("TOSEC-C64");
        Assert.Equal("TOSEC-C64", id.Value);    // exact — no rename, no lowercase
        Assert.False(id.ConformsToNewPolicy);   // diagnostic: does not meet the new policy
    }

    [Fact]
    public void FromPersisted_ConformingValue_ReportsConformance()
    {
        var id = DatLineId.FromPersisted("ps2-redump-other");
        Assert.True(id.ConformsToNewPolicy);
    }

    [Fact]
    public void TryCreateNew_ExceedingTarget_SucceedsWithWarning()
    {
        Assert.True(DatLineId.TryCreateNew(new string('a', 50), out _, out var error, out var warn));
        Assert.Equal(DatTechnicalIdError.None, error);
        Assert.True(warn);
    }

    // ── Equality and hashing ──────────────────────────────────────────────────

    [Fact]
    public void Equality_SameCanonicalValue_AreEqual()
    {
        DatGroupId.TryCreateNew("tosec-c64", out var a, out _, out _);
        DatGroupId.TryCreateNew("tosec-c64", out var b, out _, out _);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_IsOrdinalOnStoredValue()
    {
        // Legacy case-variants are NOT intrinsically equal (ordinal).
        var lower = DatGroupId.FromPersisted("tosec-c64");
        var upper = DatGroupId.FromPersisted("TOSEC-C64");
        Assert.False(lower.Equals(upper));
        Assert.True(lower != upper);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        DatLineId.TryCreateNew("tosec-c64-games", out var id, out _, out _);
        Assert.Equal("tosec-c64-games", id.ToString());
    }

    [Fact]
    public void Default_Instance_IsSafe()
    {
        var d = default(DatGroupId);
        Assert.Equal("", d.ToString());
        Assert.Equal(0, d.GetHashCode());   // no throw on null value
    }

    // ── Case-insensitive collision comparer ───────────────────────────────────

    [Fact]
    public void CaseInsensitiveComparer_MatchesCaseVariants()
    {
        var lower = DatGroupId.FromPersisted("tosec-c64");
        var upper = DatGroupId.FromPersisted("TOSEC-C64");
        Assert.True(DatGroupId.CaseInsensitiveComparer.Equals(lower, upper));
        Assert.Equal(
            DatGroupId.CaseInsensitiveComparer.GetHashCode(lower),
            DatGroupId.CaseInsensitiveComparer.GetHashCode(upper));
    }

    [Fact]
    public void HashSet_WithCaseInsensitiveComparer_DetectsCollision()
    {
        var set = new HashSet<DatLineId>(DatLineId.CaseInsensitiveComparer)
        {
            DatLineId.FromPersisted("tosec-c64"),
            DatLineId.FromPersisted("TOSEC-C64"),
        };
        Assert.Single(set);   // collapsed as a collision
    }

    [Fact]
    public void HashSet_WithDefaultEquality_KeepsCaseVariantsDistinct()
    {
        var set = new HashSet<DatLineId>
        {
            DatLineId.FromPersisted("tosec-c64"),
            DatLineId.FromPersisted("TOSEC-C64"),
        };
        Assert.Equal(2, set.Count);   // ordinal — distinct
    }

    // ── Non-interchangeability of the two id types ────────────────────────────

    [Fact]
    public void GroupId_And_LineId_AreNotInterchangeable()
    {
        var group = DatGroupId.FromPersisted("tosec-c64");
        var line  = DatLineId.FromPersisted("tosec-c64");

        // Same text, but different types → not equal when compared as objects.
        Assert.False(((object)group).Equals(line));
        Assert.IsType<DatGroupId>(group);
        Assert.IsType<DatLineId>(line);
    }
}

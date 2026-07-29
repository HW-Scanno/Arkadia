using System;
using System.Collections.Generic;

namespace Arkadia.Data.Identifiers;

/// <summary>
/// Immutable technical identifier of a Group DAT (<c>dat_groups.id</c>). A distinct type from
/// <see cref="DatLineId"/> — the two are intentionally NOT interchangeable — but both share
/// <see cref="DatTechnicalIdPolicy"/>.
///
/// <para>New ids are created only via <see cref="TryCreateNew"/> and are therefore always in
/// canonical lowercase form. Already-persisted values are wrapped verbatim via
/// <see cref="FromPersisted"/>, which never lowercases, renames, or normalizes — legacy values
/// that predate the policy remain usable, with <see cref="ConformsToNewPolicy"/> exposing the
/// diagnostic.</para>
///
/// <para>Intrinsic equality is ordinal on the stored value. Because created ids are always
/// lowercase, ordinal equality is sufficient for them; for collision checks that must span
/// case (including legacy case-variant values), use <see cref="CaseInsensitiveComparer"/>.</para>
/// </summary>
public readonly struct DatGroupId : IEquatable<DatGroupId>
{
    /// <summary>The stored identifier text. May be a legacy value when built via <see cref="FromPersisted"/>.</summary>
    public string Value { get; }

    private DatGroupId(string value) => Value = value;

    /// <summary>
    /// Attempts to create a NEW group id from user/suggested input. Accepts only the canonical
    /// form; invalid input is rejected (never silently rewritten). On success the id holds the
    /// exact (already-canonical) input.
    /// </summary>
    public static bool TryCreateNew(
        string?                 input,
        out DatGroupId          id,
        out DatTechnicalIdError error,
        out bool                exceedsRecommendedLength)
    {
        error = DatTechnicalIdPolicy.Validate(input, out exceedsRecommendedLength);
        if (error != DatTechnicalIdError.None)
        {
            id = default;
            return false;
        }

        id = new DatGroupId(input!);
        return true;
    }

    /// <summary>
    /// Wraps an already-persisted value verbatim. Does not lowercase, rename, or normalize.
    /// Throws only for the truly unrepresentable case (<c>null</c>).
    /// </summary>
    public static DatGroupId FromPersisted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DatGroupId(value);
    }

    /// <summary>Whether the stored value would be accepted under the new-id policy (diagnostic only).</summary>
    public bool ConformsToNewPolicy => DatTechnicalIdPolicy.ConformsToNewPolicy(Value ?? "");

    public bool Equals(DatGroupId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is DatGroupId other && Equals(other);
    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(DatGroupId left, DatGroupId right) => left.Equals(right);
    public static bool operator !=(DatGroupId left, DatGroupId right) => !left.Equals(right);

    public override string ToString() => Value ?? "";

    /// <summary>Collision-safe (case-insensitive) comparer for sets and cross-value checks.</summary>
    public static IEqualityComparer<DatGroupId> CaseInsensitiveComparer { get; } = new CiComparer();

    private sealed class CiComparer : IEqualityComparer<DatGroupId>
    {
        public bool Equals(DatGroupId x, DatGroupId y) =>
            string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(DatGroupId obj) =>
            obj.Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value);
    }
}

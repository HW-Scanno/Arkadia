using System;
using System.Collections.Generic;

namespace Arkadia.Data.Identifiers;

/// <summary>
/// Immutable technical identifier of a leaf DAT line (<c>dat_lines.id</c>). A distinct type
/// from <see cref="DatGroupId"/> — the two are intentionally NOT interchangeable — but both
/// share <see cref="DatTechnicalIdPolicy"/>.
///
/// <para>This value object is a <b>foundation for the future Group DAT workflow</b>. It does
/// NOT yet replace the existing <c>string datLineId</c> used across import/ingestion/archive —
/// existing workflows are untouched in this phase.</para>
///
/// <para>New ids are created only via <see cref="TryCreateNew"/> and are therefore always in
/// canonical lowercase form. Already-persisted values are wrapped verbatim via
/// <see cref="FromPersisted"/>, which never lowercases, renames, or normalizes — existing
/// leaf ids (e.g. <c>platform-authority-media</c>) load unchanged, with
/// <see cref="ConformsToNewPolicy"/> exposing the diagnostic.</para>
///
/// <para>Intrinsic equality is ordinal on the stored value. Because created ids are always
/// lowercase, ordinal equality is sufficient for them; for collision checks that must span
/// case (including legacy case-variant values), use <see cref="CaseInsensitiveComparer"/>.</para>
/// </summary>
public readonly struct DatLineId : IEquatable<DatLineId>
{
    /// <summary>The stored identifier text. May be a legacy value when built via <see cref="FromPersisted"/>.</summary>
    public string Value { get; }

    private DatLineId(string value) => Value = value;

    /// <summary>
    /// Attempts to create a NEW leaf id from user/suggested input. Accepts only the canonical
    /// form; invalid input is rejected (never silently rewritten). On success the id holds the
    /// exact (already-canonical) input.
    /// </summary>
    public static bool TryCreateNew(
        string?                 input,
        out DatLineId           id,
        out DatTechnicalIdError error,
        out bool                exceedsRecommendedLength)
    {
        error = DatTechnicalIdPolicy.Validate(input, out exceedsRecommendedLength);
        if (error != DatTechnicalIdError.None)
        {
            id = default;
            return false;
        }

        id = new DatLineId(input!);
        return true;
    }

    /// <summary>
    /// Wraps an already-persisted value verbatim. Does not lowercase, rename, or normalize.
    /// Throws only for the truly unrepresentable case (<c>null</c>).
    /// </summary>
    public static DatLineId FromPersisted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DatLineId(value);
    }

    /// <summary>Whether the stored value would be accepted under the new-id policy (diagnostic only).</summary>
    public bool ConformsToNewPolicy => DatTechnicalIdPolicy.ConformsToNewPolicy(Value ?? "");

    public bool Equals(DatLineId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is DatLineId other && Equals(other);
    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(DatLineId left, DatLineId right) => left.Equals(right);
    public static bool operator !=(DatLineId left, DatLineId right) => !left.Equals(right);

    public override string ToString() => Value ?? "";

    /// <summary>Collision-safe (case-insensitive) comparer for sets and cross-value checks.</summary>
    public static IEqualityComparer<DatLineId> CaseInsensitiveComparer { get; } = new CiComparer();

    private sealed class CiComparer : IEqualityComparer<DatLineId>
    {
        public bool Equals(DatLineId x, DatLineId y) =>
            string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(DatLineId obj) =>
            obj.Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value);
    }
}

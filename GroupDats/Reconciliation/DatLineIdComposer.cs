using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data.Identifiers;

namespace Arkadia.GroupDats;

/// <summary>Outcome of evaluating a proposed/edited leaf id.</summary>
public sealed record DatLineIdEvaluation(
    string              Id,
    bool                IsValid,
    DatTechnicalIdError PolicyError,
    bool                ExceedsRecommendedLength,
    bool                Collides)
{
    /// <summary>A human reason when not valid (for the UI summary).</summary>
    public string? Reason =>
        Collides                                          ? "id already used (case-insensitive)"
        : PolicyError == DatTechnicalIdError.Empty        ? "id is empty"
        : PolicyError == DatTechnicalIdError.NotCanonical ? "id must be lowercase a-z 0-9 - (no leading/trailing/double hyphen)"
        : PolicyError == DatTechnicalIdError.TooLong      ? "id exceeds 64 characters"
        : PolicyError == DatTechnicalIdError.ReservedName ? "id is a reserved name"
        : null;
}

/// <summary>
/// Pure composer/validator for new-leaf ids. Composition is
/// <c>group-id + non-empty folder tokens + DAT token</c> joined by <c>-</c>; it never appends media
/// type, authority, hardware family, a hash, or a random suffix, and never truncates or silently
/// normalizes. Validation reuses the Phase-1 <see cref="DatTechnicalIdPolicy"/> and a
/// case-insensitive collision predicate supplied by the caller.
/// </summary>
public static class DatLineIdComposer
{
    public static string Compose(string groupId, IEnumerable<string> folderTokens, string datToken)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(groupId)) parts.Add(groupId);
        foreach (var t in folderTokens)
            if (!string.IsNullOrEmpty(t)) parts.Add(t);
        if (!string.IsNullOrEmpty(datToken)) parts.Add(datToken);
        return string.Join("-", parts);
    }

    /// <param name="collides">Case-insensitive collision test against occupied + other session ids.</param>
    public static DatLineIdEvaluation Evaluate(string id, Func<string, bool> collides)
    {
        var policyError = DatTechnicalIdPolicy.Validate(id, out var exceeds);
        var policyOk    = policyError == DatTechnicalIdError.None;
        var coll        = policyOk && collides(id);
        return new DatLineIdEvaluation(id, policyOk && !coll, policyError, exceeds, coll);
    }
}

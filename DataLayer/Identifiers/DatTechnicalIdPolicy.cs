using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arkadia.Data.Identifiers;

/// <summary>
/// Structured outcome of validating a candidate technical id against the new-id policy.
/// <see cref="None"/> means the candidate is a valid, committable new id.
/// </summary>
public enum DatTechnicalIdError
{
    /// <summary>Candidate is a valid new id (no error).</summary>
    None = 0,
    /// <summary>Candidate is null or empty.</summary>
    Empty,
    /// <summary>Candidate does not match the canonical lowercase-hyphen form.</summary>
    NotCanonical,
    /// <summary>Candidate exceeds the blocking maximum length.</summary>
    TooLong,
    /// <summary>Candidate is exactly a reserved Windows device name.</summary>
    ReservedName,
}

/// <summary>
/// Pure, shared policy for Arkadia's <b>new</b> technical identifiers (Group DAT ids and
/// Group-created leaf ids). Foundational for the Group DAT milestone — see
/// <c>docs/SPECS/ARKADIA_GROUP_DAT_V1_SPEC.md</c>.
///
/// <para><b>Scope guard.</b> This policy governs ids that are being <i>created</i>. It does
/// NOT normalize, migrate, or reject historical / already-persisted ids — legacy values are
/// loaded verbatim via the value objects' <c>FromPersisted</c> entry points. There is no DB
/// or filesystem access here.</para>
///
/// <para><b>Canonical form:</b> lowercase ASCII, characters <c>a-z 0-9 -</c>, must start and
/// end with an alphanumeric, no consecutive hyphens. Length 1..<see cref="MaxLength"/>;
/// lengths above <see cref="TargetLength"/> are valid but flagged as exceeding the recommended
/// target. Collision comparisons are case-insensitive (<see cref="Comparer"/>) to defend the
/// case-sensitive-SQLite vs case-insensitive-NTFS split.</para>
/// </summary>
public static class DatTechnicalIdPolicy
{
    /// <summary>Recommended maximum length; exceeding it is a warning, not an error.</summary>
    public const int TargetLength = 48;

    /// <summary>Blocking maximum length; exceeding it is <see cref="DatTechnicalIdError.TooLong"/>.</summary>
    public const int MaxLength = 64;

    /// <summary>Case-insensitive comparer for collision-safe id comparison across DB/filesystem.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    // Canonical: 1+ alphanumeric groups joined by single hyphens. No leading/trailing/double
    // hyphen, no uppercase, no other characters. Length is checked separately.
    private static readonly Regex CanonicalForm =
        new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Windows reserved device names. A whole id equal (case-insensitively) to one of these is
    // rejected. Because dots are not permitted by the canonical form, dotted variants like
    // "con.txt" already fail the syntax check and need no special handling. Composite ids such
    // as "tosec-con" are allowed (the whole segment is not a reserved name).
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        };

    /// <summary>
    /// Validates a candidate <b>new</b> id. Never mutates the input. Returns
    /// <see cref="DatTechnicalIdError.None"/> for a valid, committable id and sets
    /// <paramref name="exceedsRecommendedLength"/> when a valid id is longer than
    /// <see cref="TargetLength"/> (a non-blocking warning).
    /// </summary>
    public static DatTechnicalIdError Validate(string? candidate, out bool exceedsRecommendedLength)
    {
        exceedsRecommendedLength = false;

        if (string.IsNullOrEmpty(candidate))
            return DatTechnicalIdError.Empty;
        if (!CanonicalForm.IsMatch(candidate))
            return DatTechnicalIdError.NotCanonical;
        if (candidate.Length > MaxLength)
            return DatTechnicalIdError.TooLong;
        if (ReservedNames.Contains(candidate))
            return DatTechnicalIdError.ReservedName;

        exceedsRecommendedLength = candidate.Length > TargetLength;
        return DatTechnicalIdError.None;
    }

    /// <summary>True when <paramref name="candidate"/> is a valid new id.</summary>
    public static bool IsValidNew(string? candidate) =>
        Validate(candidate, out _) == DatTechnicalIdError.None;

    /// <summary>
    /// Diagnostic for an already-persisted value: whether it would be accepted under the new-id
    /// policy. Used only to surface information (e.g. legacy ids); never blocks loading.
    /// </summary>
    public static bool ConformsToNewPolicy(string value) =>
        Validate(value, out _) == DatTechnicalIdError.None;

    /// <summary>
    /// Pure, deterministic, culture-invariant normalization of arbitrary text into a
    /// suggestion-shaped id fragment. This is a helper for a future automatic id suggester —
    /// it is intentionally partial:
    /// <list type="bullet">
    ///   <item>trims, lowercases (invariant), and Unicode-decomposes (FormD);</item>
    ///   <item>drops combining marks and maps every non <c>[a-z0-9]</c> character to a hyphen;</item>
    ///   <item>collapses consecutive hyphens and trims leading/trailing hyphens.</item>
    /// </list>
    /// It does NOT prepend a group id, apply TOSEC-specific reduction, strip generic words, or
    /// compute a disambiguation hash — those belong to a later phase. It may return an empty
    /// string (e.g. all-non-ASCII input); the caller must handle that. The result is NOT
    /// guaranteed to be a valid id (it may be empty); callers validate separately.
    /// </summary>
    public static string NormalizeSuggestion(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return "";

        var decomposed = source.Trim().Normalize(NormalizationForm.FormD);

        var mapped = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark
                         or UnicodeCategory.SpacingCombiningMark
                         or UnicodeCategory.EnclosingMark)
                continue;   // drop accents left over from decomposition

            var lower = char.ToLowerInvariant(ch);
            mapped.Append(lower is >= 'a' and <= 'z' or >= '0' and <= '9' ? lower : '-');
        }

        // Collapse consecutive hyphens, then trim leading/trailing hyphens.
        var collapsed = new StringBuilder(mapped.Length);
        var previousWasHyphen = false;
        foreach (var ch in mapped.ToString())
        {
            if (ch == '-')
            {
                if (!previousWasHyphen) collapsed.Append('-');
                previousWasHyphen = true;
            }
            else
            {
                collapsed.Append(ch);
                previousWasHyphen = false;
            }
        }

        return collapsed.ToString().Trim('-');
    }
}

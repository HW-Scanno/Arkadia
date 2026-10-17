using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Arkadia.Data.Identifiers;

/// <summary>Why a Single DAT id could not be built. <see cref="None"/> means a valid, collision-free id.</summary>
public enum SingleDatIdError
{
    None = 0,
    /// <summary>A structured component (system / authority / media type) is empty or not a canonical id segment.</summary>
    InvalidSystem,
    InvalidAuthority,
    InvalidMediaType,
    /// <summary>The variant is null/empty.</summary>
    VariantEmpty,
    /// <summary>The variant is not <c>^[a-z0-9]+(?:-[a-z0-9]+)*$</c> (case, spaces, underscores, dots, separators, traversal…).</summary>
    VariantInvalid,
    /// <summary>The full id exceeds <see cref="DatTechnicalIdPolicy.MaxLength"/>.</summary>
    IdTooLong,
    /// <summary>The full id is a reserved Windows device name (defensive; unreachable for 4-part ids).</summary>
    IdReserved,
    /// <summary>The full id equals (case-insensitively) an existing DAT id, including Group leaves.</summary>
    Collision,
}

/// <summary>Outcome of <see cref="SingleDatIdBuilder.Build"/>. <see cref="Id"/> is non-null only when <see cref="IsValid"/>.</summary>
public sealed record SingleDatIdResult(
    bool IsValid, string? Id, SingleDatIdError Error, string Message, bool ExceedsRecommendedLength = false)
{
    internal static SingleDatIdResult Fail(SingleDatIdError error, string message) => new(false, null, error, message);
}

/// <summary>
/// Pure builder/validator for the technical id of a NEW standalone (Single) DAT:
/// <c>{hardwareFamilyId}-{authorityId}-{mediaTypeId}-{variant}</c>, e.g. <c>nds-nointro-cartridge-main</c>.
///
/// <para><b>Scope.</b> New Single DAT imports only. Legacy three-component ids (<c>platform-authority-media</c>)
/// are never parsed, re-validated, or rewritten — the id is built from the ORIGINAL structured values, never by
/// splitting an existing id. Group DAT id/leaf policy is untouched (this class only reads
/// <see cref="DatTechnicalIdPolicy"/>). No DB, no filesystem: the caller supplies the existing ids.</para>
///
/// <para><b>Validation, not repair.</b> <see cref="Build"/> never trims, lowercases, or otherwise rewrites its
/// inputs — invalid input is rejected with a specific <see cref="SingleDatIdError"/>. Only
/// <see cref="SuggestVariant"/> normalizes, and it is a suggestion helper, never used on the persistence path.
/// The id is immutable once created; the Display Name is a separate field and is not an input here.</para>
/// </summary>
public static class SingleDatIdBuilder
{
    /// <summary>Default variant suggested for a new Single DAT.</summary>
    public const string DefaultVariant = "main";

    // Same canonical shape as DatTechnicalIdPolicy, applied to the variant and to each structured component:
    // lowercase ASCII alphanumerics, hyphens only BETWEEN non-empty groups. Anchored with \z, not $: in .NET
    // '$' also matches before a trailing newline, which would let a variant ending in a newline through.
    private static readonly Regex Segment =
        new(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="variant"/> is a valid variant slug (exact match, no normalization).</summary>
    public static bool IsValidVariant(string? variant)
        => !string.IsNullOrEmpty(variant) && Segment.IsMatch(variant);

    /// <summary>
    /// Suggests a variant from free text (e.g. "Download Play" → <c>download-play</c>) via
    /// <see cref="DatTechnicalIdPolicy.NormalizeSuggestion"/>. Falls back to <see cref="DefaultVariant"/> when the
    /// text is null/blank or normalizes to nothing. The result is always a valid variant slug.
    /// </summary>
    public static string SuggestVariant(string? source)
    {
        var normalized = DatTechnicalIdPolicy.NormalizeSuggestion(source);
        return IsValidVariant(normalized) ? normalized : DefaultVariant;
    }

    /// <summary>
    /// Builds and validates <c>{hardwareFamilyId}-{authorityId}-{mediaTypeId}-{variant}</c>. All inputs are used
    /// verbatim. <paramref name="existingIds"/> must contain EVERY existing DAT id (Single and Group leaves —
    /// i.e. all <c>dat_lines.id</c>); the comparison is case-insensitive.
    /// </summary>
    public static SingleDatIdResult Build(
        string?              hardwareFamilyId,
        string?              authorityId,
        string?              mediaTypeId,
        string?              variant,
        IEnumerable<string>  existingIds)
    {
        ArgumentNullException.ThrowIfNull(existingIds);

        if (!IsValidComponent(hardwareFamilyId))
            return SingleDatIdResult.Fail(SingleDatIdError.InvalidSystem,
                $"System id '{hardwareFamilyId}' cannot be used in a DAT id (lowercase letters, digits and single hyphens only).");
        if (!IsValidComponent(authorityId))
            return SingleDatIdResult.Fail(SingleDatIdError.InvalidAuthority,
                $"Authority id '{authorityId}' cannot be used in a DAT id (lowercase letters, digits and single hyphens only).");
        if (!IsValidComponent(mediaTypeId))
            return SingleDatIdResult.Fail(SingleDatIdError.InvalidMediaType,
                $"Media type id '{mediaTypeId}' cannot be used in a DAT id (lowercase letters, digits and single hyphens only).");

        if (string.IsNullOrEmpty(variant))
            return SingleDatIdResult.Fail(SingleDatIdError.VariantEmpty, "The variant is required.");
        if (!Segment.IsMatch(variant))
            return SingleDatIdResult.Fail(SingleDatIdError.VariantInvalid,
                $"Variant '{variant}' is not valid: use lowercase letters and digits, with single hyphens between non-empty parts (e.g. 'main', 'download-play').");

        var id = $"{hardwareFamilyId}-{authorityId}-{mediaTypeId}-{variant}";

        // Full-id check against the shared policy (canonical form, max length, reserved names).
        var policy = DatTechnicalIdPolicy.Validate(id, out var exceeds);
        switch (policy)
        {
            case DatTechnicalIdError.TooLong:
                return SingleDatIdResult.Fail(SingleDatIdError.IdTooLong,
                    $"The id '{id}' is {id.Length} characters; the maximum is {DatTechnicalIdPolicy.MaxLength}. Use a shorter variant.");
            case DatTechnicalIdError.ReservedName:
                return SingleDatIdResult.Fail(SingleDatIdError.IdReserved, $"The id '{id}' is a reserved name.");
            case DatTechnicalIdError.Empty:
            case DatTechnicalIdError.NotCanonical:
                // Unreachable once every part is canonical, but never let a policy disagreement through.
                return SingleDatIdResult.Fail(SingleDatIdError.VariantInvalid, $"The id '{id}' is not a canonical technical id.");
        }

        foreach (var existing in existingIds)
            if (existing is not null && DatTechnicalIdPolicy.Comparer.Equals(existing, id))
                return SingleDatIdResult.Fail(SingleDatIdError.Collision,
                    $"A DAT with id '{existing}' already exists (ids are compared case-insensitively). Choose a different variant.");

        return new SingleDatIdResult(true, id, SingleDatIdError.None, "", exceeds);
    }

    private static bool IsValidComponent(string? value)
        => !string.IsNullOrEmpty(value) && Segment.IsMatch(value);
}

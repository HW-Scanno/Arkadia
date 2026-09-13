using System;
using System.Globalization;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>How an authoritative DAT file requirement (one <c>release_files</c> row) can be satisfied.</summary>
public enum DatFileRequirementKind
{
    /// <summary>Carries a SHA1 and/or MD5 consistent with its declared size: satisfied by hash-matching incoming material.</summary>
    Physical,

    /// <summary>
    /// The DAT explicitly declares <c>size == 0</c> and carries NO CRC/MD5/SHA1. Empty content is fully determined by
    /// the declared size, so the file is satisfied by authoritative synthesis (generate → verify) during finalization.
    /// </summary>
    SynthesizableEmpty,

    /// <summary>
    /// No SHA1/MD5 (hashless, or CRC-only) and not an explicit zero-byte declaration: the matcher cannot identify it
    /// and it must never be synthesized. The owning release cannot be completed.
    /// </summary>
    Unmatchable,

    /// <summary>
    /// The DAT declares <c>size == 0</c> but one or more declared hashes are NOT the empty-file hashes. Contradictory:
    /// never synthesized, never ignored — the owning release cannot be completed.
    /// </summary>
    ContradictoryZeroByte,
}

/// <summary>
/// The single shared classification rule for DAT file requirements. Pure: reads only the DAT-declared size/hashes,
/// never the filesystem, never the DB, and never rewrites the DAT. Missing hashes alone NEVER make a file
/// synthesizable; a missing/invalid/non-zero size NEVER makes a file synthesizable.
/// </summary>
public static class DatFileRequirement
{
    /// <summary>Canonical hashes of a zero-byte file (used only to detect contradictory zero-byte declarations).</summary>
    public const string EmptySha1  = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
    public const string EmptyMd5   = "d41d8cd98f00b204e9800998ecf8427e";
    public const string EmptyCrc32 = "00000000";

    public static DatFileRequirementKind Classify(ReleaseFileRecord file)
        => Classify(file.Size, file.Crc, file.Md5, file.Sha1);

    public static DatFileRequirementKind Classify(string? size, string? crc, string? md5, string? sha1)
    {
        var c = Normalize(crc);
        var m = Normalize(md5);
        var s = Normalize(sha1);
        bool declaredZero = IsDeclaredZeroSize(size);

        if (c.Length == 0 && m.Length == 0 && s.Length == 0)
            return declaredZero ? DatFileRequirementKind.SynthesizableEmpty : DatFileRequirementKind.Unmatchable;

        if (declaredZero &&
            ((s.Length > 0 && s != EmptySha1) ||
             (m.Length > 0 && m != EmptyMd5)  ||
             (c.Length > 0 && c != EmptyCrc32)))
            return DatFileRequirementKind.ContradictoryZeroByte;

        // SHA1/MD5 are the only hashes the matcher can use; a CRC-only requirement stays unmatchable.
        return s.Length > 0 || m.Length > 0 ? DatFileRequirementKind.Physical : DatFileRequirementKind.Unmatchable;
    }

    public static bool IsSynthesizableEmpty(ReleaseFileRecord file)
        => Classify(file) == DatFileRequirementKind.SynthesizableEmpty;

    /// <summary>True for requirements no ingestion path can satisfy (unmatchable or contradictory).</summary>
    public static bool IsUnsatisfiable(DatFileRequirementKind kind)
        => kind is DatFileRequirementKind.Unmatchable or DatFileRequirementKind.ContradictoryZeroByte;

    /// <summary>The declared size parses (invariant, digits only) to exactly zero. Blank/invalid/non-zero ⇒ false.</summary>
    public static bool IsDeclaredZeroSize(string? size)
    {
        var t = (size ?? "").Trim();
        return t.Length > 0
            && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            && v == 0;
    }

    /// <summary>
    /// Release-scoped content identity for a synthesized hashless empty file. Deliberately NOT the canonical empty-file
    /// hash (every empty file shares it — that would collapse artifacts and reuse archive paths across releases): the
    /// key embeds the owning release id and the DAT-declared relative file name, so the artifact stays bound to exactly
    /// one release/file requirement.
    /// </summary>
    public static string SynthesizedEmptyIdentityKey(string releaseId, string romName)
        => $"empty-file:{releaseId}:{romName}";

    /// <summary>Human-readable reason for an unsatisfiable requirement (operation logs / plan notices).</summary>
    public static string DescribeUnsatisfiable(DatFileRequirementKind kind) => kind switch
    {
        DatFileRequirementKind.Unmatchable =>
            "DAT provides no SHA1 or MD5 and does not declare size 0 — the file cannot be identified",
        DatFileRequirementKind.ContradictoryZeroByte =>
            "DAT declares size 0 but its hashes are not the empty-file hashes (contradictory requirement)",
        _ => "",
    };

    private static string Normalize(string? h) => (h ?? "").Trim().ToLowerInvariant();
}

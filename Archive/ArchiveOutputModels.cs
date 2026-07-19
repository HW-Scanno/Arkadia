using System.Collections.Generic;
using Arkadia.Data;

namespace Arkadia.Archive;

/// <summary>
/// Uniform archive layout form for a whole DAT line. There is no per-release
/// fallback between forms. <see cref="Unknown"/> marks legacy / not-yet-validated
/// DAT lines — existing lines must never be blindly assumed <see cref="SingleFileFlat"/>.
/// </summary>
public enum ArchiveDatLineOutputForm
{
    Unknown,
    SingleFileFlat,
    MultiFileReleaseFolder,
}

/// <summary>Persistence-facing validation state for a DAT line's archive output plan.</summary>
public enum ArchiveOutputValidationState
{
    /// <summary>Never validated under the policy (legacy line, or no stored fingerprint).</summary>
    Unknown,
    /// <summary>
    /// The FULL DAT release set has no output collisions. Normal Exclude/Restore
    /// curation does NOT invalidate this — only a structural change does. This is
    /// the common, curation-stable state.
    /// </summary>
    ValidFullSet,
    /// <summary>
    /// The full DAT release set collides, but the current wanted subset does not
    /// because one or more colliding releases are unwanted. Valid only for the
    /// current wanted/unwanted set — restoring an excluded release can reintroduce
    /// the collision.
    /// </summary>
    ValidWithExclusions,
    /// <summary>The current wanted subset still has an unresolved collision (curator action needed).</summary>
    CollisionUnresolved,
    /// <summary>The structural fingerprint changed since validation (DAT update / strategy / ext change).</summary>
    Stale,
}

/// <summary>Per-extension rule for the <c>file_extension</c> strategy.</summary>
public sealed record ArchiveFileExtensionRule(bool IsDiscard, string OutputExtension);

/// <summary>
/// DAT-line transform configuration needed to resolve and plan archive output.
/// Built by the caller from the catalog (transform strategy + transform record);
/// the helpers stay pure and DB-free.
/// </summary>
public sealed record ArchiveOutputConfig
{
    public required string PlatformId { get; init; }
    public required string DatLineId  { get; init; }

    /// <summary>"release_shape" | "release_folder" | "file_extension" | "none".</summary>
    public required string StrategyType { get; init; }

    /// <summary>
    /// Output extension for single-file forms (release_shape ⇒ ".chd",
    /// release_folder single-file/ZIP ⇒ ".zip"). Ignored for file_extension.
    /// </summary>
    public string SingleFileOutputExtension { get; init; } = "";

    /// <summary>
    /// release_folder only: true ⇒ the transform emits a folder bundle
    /// (No Compression Folder → <see cref="ArchiveDatLineOutputForm.MultiFileReleaseFolder"/>);
    /// false ⇒ a single file (ZIP → <see cref="ArchiveDatLineOutputForm.SingleFileFlat"/>).
    /// </summary>
    public bool FolderOutputsFolder { get; init; }

    /// <summary>
    /// file_extension only: extension (lowercased, incl. dot, or "(no ext)") → rule.
    /// A file whose extension is absent here produces no derived output.
    /// </summary>
    public IReadOnlyDictionary<string, ArchiveFileExtensionRule> ExtensionRules { get; init; }
        = new Dictionary<string, ArchiveFileExtensionRule>();
}

/// <summary>One release with its DAT-declared files. Status may be "unwanted".</summary>
public sealed record ArchiveReleaseInput
{
    public required string ReleaseId   { get; init; }
    public required string ReleaseName { get; init; }
    public required string Status      { get; init; }
    public required IReadOnlyList<ReleaseFileRecord> Files { get; init; }
}

/// <summary>Source file detail carried into a candidate for the future A/B dialog.</summary>
public sealed record ArchiveSourceFile(
    string RomName,
    long?  SizeBytes,
    string Sha1,
    string Md5,
    string Crc);

/// <summary>
/// Planned archive output for one release — rich enough to drive a future
/// side-by-side collision-review dialog without recomputation.
/// </summary>
public sealed record ArchiveOutputCandidate
{
    public required string ReleaseId       { get; init; }
    public required string ReleaseName     { get; init; }
    public required string SafeReleaseName { get; init; }
    public required string Status          { get; init; }
    public required ArchiveDatLineOutputForm Form { get; init; }

    /// <summary>
    /// Top-level archive entry name — the flat filename (SingleFileFlat) or the
    /// release folder name (MultiFileReleaseFolder). This is the collision key.
    /// </summary>
    public required string ArchiveEntryName { get; init; }

    /// <summary>Planned relative path under the app root (mirrors derived_artifacts.relative_path shape).</summary>
    public required string PlannedRelativePath { get; init; }

    /// <summary>Planned flat filename (SingleFileFlat only; empty for folder form).</summary>
    public string PlannedFilename { get; init; } = "";

    /// <summary>Main/source input file (release_shape .cue/.iso; single file_extension file), else "".</summary>
    public string MainInputFile { get; init; } = "";

    public required IReadOnlyList<ArchiveSourceFile> SourceFiles { get; init; }

    /// <summary>Planned inner filenames for a folder bundle (MultiFileReleaseFolder), else empty.</summary>
    public IReadOnlyList<string> PlannedInnerFilenames { get; init; } = [];

    /// <summary>Best-effort content identity ("release:{id}" or "sha1:{...}"), if determinable.</summary>
    public string? ContentIdentityKey { get; init; }

    public long TotalSourceBytes  { get; init; }
    public int  PlannedOutputCount { get; init; }
}

/// <summary>A set of wanted releases whose plans collide on the same archive entry name.</summary>
public sealed record ArchiveOutputCollisionGroup(
    string ArchiveEntryName,
    IReadOnlyList<ArchiveOutputCandidate> Candidates);

/// <summary>
/// Full validation result for a DAT line's archive output plan. Distinguishes the
/// FULL-set view (structural — used to decide <see cref="ArchiveOutputValidationState.ValidFullSet"/>)
/// from the current WANTED-subset view (exclusion-sensitive).
/// </summary>
public sealed record ArchiveOutputValidationResult(
    ArchiveDatLineOutputForm Form,
    ArchiveOutputValidationState State,
    IReadOnlyList<ArchiveOutputCandidate> WantedCandidates,
    IReadOnlyList<ArchiveOutputCollisionGroup> FullSetCollisions,
    IReadOnlyList<ArchiveOutputCollisionGroup> WantedSubsetCollisions,
    /// <summary>Wanted-agnostic fingerprint — only structural changes invalidate a validation.</summary>
    string StructuralFingerprint,
    /// <summary>Fingerprint of the exclusion decision (unwanted release ids); only meaningful for ValidWithExclusions.</summary>
    string ExclusionFingerprint)
{
    public bool FullSetHasCollision      => FullSetCollisions.Count > 0;
    public bool WantedSubsetHasCollision => WantedSubsetCollisions.Count > 0;
}

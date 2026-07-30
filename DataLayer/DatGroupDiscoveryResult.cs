using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Arkadia.Data;

/// <summary>
/// Deeply-immutable snapshot of one parsed ROM entry (mirrors <see cref="DatParser.ParsedRom"/>).
/// All members are value strings — no reference to the parser's objects is retained.
/// </summary>
public sealed record DiscoveredDatRom(string Name, string Size, string Crc, string Md5, string Sha1);

/// <summary>
/// Deeply-immutable snapshot of one parsed game (mirrors <see cref="DatParser.ParsedGame"/>).
/// <see cref="Roms"/> is an <see cref="ImmutableArray{T}"/> — truly non-modifiable (no writable
/// indexer, and not castable to a mutable array/list); no reference to the parser's mutable
/// <c>List</c> is kept. Order, multiplicity, and values are preserved exactly — no title
/// normalization, ROM reordering, dedup, merge, or hash enrichment.
/// </summary>
public sealed record DiscoveredDatGame(
    string                          Name,
    string                          Region,
    string                          Languages,
    string                          ContentKey,
    string                          WorkingState,
    ImmutableArray<DiscoveredDatRom> Roms);

/// <summary>Severity of a <see cref="DatGroupDiscoveryDiagnostic"/>.</summary>
public enum DatGroupDiscoveryDiagnosticSeverity { Info, Warning, Error }

/// <summary>Per-leaf outcome of the discovery scan (parsing only — no DB, no matching).</summary>
public enum DiscoveredDatLeafStatus
{
    /// <summary>The DAT was read and parsed successfully.</summary>
    Parsed,
    /// <summary>The file was readable but the parser rejected it (malformed / not a Logiqx DAT).</summary>
    ParseFailed,
    /// <summary>The file could not be opened/read (locked, permission denied, …).</summary>
    ReadFailed,
}

/// <summary>Stable diagnostic codes emitted by Group DAT source discovery.</summary>
public static class DatGroupDiscoveryDiagnosticCodes
{
    public const string SourceRootMissing          = "source-root-missing";
    public const string SourceRootNotDirectory     = "source-root-not-directory";
    public const string SourceRootEnumerationFailed = "source-root-enumeration-failed";
    public const string ReparsePointSkipped        = "reparse-point-skipped";
    public const string RelativePathOutsideRoot    = "relative-path-outside-root";
    public const string RelativePathCollision      = "relative-path-collision";
    public const string DatReadFailed              = "dat-read-failed";
    public const string DatParseFailed             = "dat-parse-failed";
}

/// <summary>
/// One discovery diagnostic. Holds only a stable code, severity, a controlled human message,
/// and (when file-scoped) the normalized relative path. Never holds an Exception or stack trace,
/// and messages avoid absolute paths.
/// </summary>
public sealed record DatGroupDiscoveryDiagnostic(
    string                              Code,
    DatGroupDiscoveryDiagnosticSeverity Severity,
    string                              Message,
    string?                             RelativePath = null);

/// <summary>
/// One candidate DAT found under the source root, represented even when parsing failed.
///
/// <para>Identity is the <see cref="RelativePath"/> (normalized with '/'). <see cref="SourcePath"/>
/// (the absolute path) is kept only for in-memory processing of the current session — it is NOT
/// identity, is NOT used for ordering, and is not part of any future persisted form.</para>
///
/// <para><see cref="Games"/> is a read-only snapshot of the parser's top-level game list (empty
/// unless <see cref="Status"/> is <see cref="DiscoveredDatLeafStatus.Parsed"/>); the parser's own
/// per-game data is surfaced as-is, without reinterpretation.</para>
/// </summary>
public sealed class DiscoveredDatLeaf
{
    public required string                   RelativePath { get; init; }
    public required string                   FileName     { get; init; }
    public required string                   SourcePath   { get; init; }
    public required DiscoveredDatLeafStatus  Status       { get; init; }

    /// <summary>Parser metadata (empty strings when not parsed).</summary>
    public string DatName    { get; init; } = "";
    public string DatVersion { get; init; } = "";
    public string DatDate    { get; init; } = "";
    public string DatAuthor  { get; init; } = "";

    /// <summary>Deeply-immutable snapshot of parsed games (empty when not parsed). Exposed as an
    /// <see cref="ImmutableArray{T}"/> so it cannot be mutated via any public cast; the parser's
    /// mutable <see cref="DatParser.ParsedGame"/>/<see cref="DatParser.ParsedRom"/> are never exposed.</summary>
    public ImmutableArray<DiscoveredDatGame> Games { get; init; } = [];

    /// <summary>Number of parsed games (0 when not parsed).</summary>
    public int GameCount => Games.Length;

    public bool ParseSucceeded => Status == DiscoveredDatLeafStatus.Parsed;

    /// <summary>The file-scoped failure diagnostic when <see cref="Status"/> is not Parsed; else null.</summary>
    public DatGroupDiscoveryDiagnostic? Diagnostic { get; init; }
}

/// <summary>
/// In-memory result of a pure, DB-independent Group DAT source-directory scan. Represents the
/// candidate leaf DATs found, per-file and global diagnostics, and derived counts. It answers
/// only "which candidate DAT leaves exist and how did parsing go?" — it performs no matching,
/// ID assignment, fingerprinting, or planning.
/// </summary>
public sealed class DatGroupDiscoveryResult
{
    /// <summary>Absolute, normalized source root for this session (not identity).</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Candidate leaves, ordered deterministically by <see cref="DiscoveredDatLeaf.RelativePath"/> (Ordinal).</summary>
    public required IReadOnlyList<DiscoveredDatLeaf> Leaves { get; init; }

    /// <summary>All diagnostics (global and file-scoped), deterministically ordered.</summary>
    public required IReadOnlyList<DatGroupDiscoveryDiagnostic> Diagnostics { get; init; }

    public int CandidateCount => Leaves.Count;
    public int ParsedCount     => Leaves.Count(l => l.Status == DiscoveredDatLeafStatus.Parsed);
    public int FailedCount     => Leaves.Count(l => l.Status != DiscoveredDatLeafStatus.Parsed);

    /// <summary>True when any diagnostic is <see cref="DatGroupDiscoveryDiagnosticSeverity.Error"/>.</summary>
    public bool HasBlockingErrors =>
        Diagnostics.Any(d => d.Severity == DatGroupDiscoveryDiagnosticSeverity.Error);

    /// <summary>
    /// True only when the root is valid, there are no blocking errors (no collisions, no read/parse
    /// failures), and at least one candidate exists. This does NOT create or imply any plan; later
    /// phases own planning.
    /// </summary>
    public bool CanProceedToPlanning => !HasBlockingErrors && CandidateCount > 0;
}

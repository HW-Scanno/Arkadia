namespace Arkadia.LocalArchive;

/// <summary>
/// Classification of a physical file found in the active local archive.
/// All non-MissingFile values represent a real file on disk.
/// </summary>
public enum LocalArchiveClass
{
    /// <summary>Physical file exists, SHA-1 matches DB, all linked releases are non-unwanted.</summary>
    WantedArchiveOk,

    /// <summary>Physical file exists and is linked to at least one unwanted release (UNWANTED WINS).</summary>
    UnwantedArchiveArtifact,

    /// <summary>Physical file exists but its SHA-1 does not match any DB derived artifact for this DAT line.</summary>
    UnknownArchiveFile,

    /// <summary>Physical file path/filename appears to correspond to a DB artifact, but hash does not match expected.</summary>
    ArchiveHashMismatch,

    /// <summary>Multiple DB rows collide on the same archive filename.</summary>
    ArchiveDuplicateCollision,

    /// <summary>
    /// Optional diagnostic only — DB artifact has no physical file in the active archive.
    /// NOT emitted by the primary physical scan; available via AbsentFromArchiveCount.
    /// </summary>
    ArchiveMissingFile,
}

/// <summary>
/// One entry in a <see cref="LocalArchiveVerifyPlan"/>.
/// Every entry represents a real physical file found on disk during the scan.
/// </summary>
public sealed class LocalArchiveEntry
{
    public LocalArchiveClass Classification    { get; init; }
    public string            FileName          { get; init; } = "";
    /// <summary>Path relative to the archive directory (includes subdirs, if any).</summary>
    public string            RelativePath      { get; init; } = "";
    /// <summary>Null for unknown files (no DB row matched this file's hash or name).</summary>
    public string?           DerivedArtifactId  { get; init; }
    /// <summary>Null for unknown files.</summary>
    public string?           ContentIdentityKey { get; init; }
    public string            ExpectedSha1       { get; init; } = "";
    /// <summary>Actual SHA-1 computed from disk during the scan.</summary>
    public string            ActualSha1         { get; init; } = "";
    public bool              IsRepairable       { get; init; }
    public string            Note               { get; init; } = "";
}

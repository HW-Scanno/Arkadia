using System.Collections.Generic;

namespace Arkadia.GroupDats;

/// <summary>Overall outcome of <see cref="GroupDatExecutionService.ExecuteCreateAsync"/>.</summary>
public enum GroupDatExecutionStatus
{
    /// <summary>The Group DAT was fully persisted (catalog committed, all leaf DBs published).</summary>
    Committed,
    /// <summary>A failure occurred and the catalog is unchanged; all this execution's files were cleaned up.</summary>
    AbortedNoWrites,
    /// <summary>Cancelled by the caller before the catalog commit; this execution's files were cleaned up.</summary>
    Cancelled,
    /// <summary>The catalog is unchanged, but one or more files of THIS execution could not be removed.</summary>
    CleanupRequired,
}

/// <summary>Deterministic, caller-distinguishable failure categories (no stack traces / absolute source paths).</summary>
public enum GroupDatExecutionErrorCode
{
    None,
    InvalidPlan,
    StalePlan,
    SourceRootMissing,
    SourceMissing,
    SourcePathInvalid,
    ReparsePoint,
    ReparseFailed,
    GroupIdCollision,
    LeafIdCollision,
    MediaTypeMissing,
    HardwareFamilyMissing,
    DataStorePathCollision,
    DestinationOccupied,
    PrepareFailed,
    PublishFailed,
    CatalogFailed,
    Cancelled,
}

/// <summary>Phases reported through <see cref="GroupDatExecutionProgress"/>.</summary>
public enum GroupDatExecutionPhase { Revalidating, Preparing, Publishing, CommittingCatalog, CleaningUp, Completed }

/// <summary>A single progress tick (1-based <see cref="Index"/> of <see cref="Total"/> leaves).</summary>
public sealed record GroupDatExecutionProgress(
    GroupDatExecutionPhase Phase,
    int                    Index,
    int                    Total,
    string?                LeafId,
    string                 Text);

/// <summary>
/// Result of a Group Create execution. All failure information is here (the method does not throw for
/// control flow): the caller distinguishes validation/stale, cancellation, prepare/publish/catalog
/// failure, and incomplete cleanup via <see cref="OverallStatus"/> + <see cref="ErrorCode"/>.
/// <see cref="CleanupPaths"/> lists this execution's files that still exist and need manual removal.
/// </summary>
public sealed record GroupDatExecutionResult
{
    public required string                     GroupId       { get; init; }
    public required GroupDatExecutionStatus    OverallStatus { get; init; }
    public required int                        LeafTotal     { get; init; }
    public          int                        PreparedCount { get; init; }
    public          int                        PublishedCount{ get; init; }
    public          int?                        Revision      { get; init; }
    public          GroupDatExecutionErrorCode ErrorCode     { get; init; } = GroupDatExecutionErrorCode.None;
    public          string?                     ErrorMessage  { get; init; }
    public          string?                     LeafId        { get; init; }
    public          IReadOnlyList<string>       CleanupPaths  { get; init; } = System.Array.Empty<string>();

    public bool Succeeded => OverallStatus == GroupDatExecutionStatus.Committed;
}

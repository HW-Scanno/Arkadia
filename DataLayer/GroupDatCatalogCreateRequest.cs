using System;
using System.Collections.Generic;

namespace Arkadia.Data;

/// <summary>
/// One initial catalog working-state entry to (re)apply during Group Create, mirroring the Single-DAT
/// import call <c>SetWorkingStateIfNotManual(itemId, state)</c>. <see cref="ItemId"/> is the release name
/// (the global key of <c>catalog_working_state</c>); it is only written when the row is not manually
/// curated (<c>is_manual = 0</c>).
/// </summary>
public sealed record GroupDatInitialWorkingState(string ItemId, string State, string? Note = null);

/// <summary>
/// One leaf to register atomically as part of a Group DAT: its full <c>dat_lines</c> row plus the Group
/// metadata columns and any initial working states, kept as a single aligned record so a leaf and its
/// metadata can never drift apart. The dat_line's <see cref="DatLineRecord.HardwareFamilyId"/> and
/// <see cref="DatLineRecord.Authority"/> must match the group's; the group id is taken from the request
/// (not from this record) so alignment is guaranteed.
/// </summary>
public sealed record GroupDatCatalogLeafCreate
{
    /// <summary>The complete <c>dat_lines</c> row (id, name, media type, version, DataStorePath, release count, …).</summary>
    public required DatLineRecord DatLine { get; init; }

    public required string  RelativeDatPath            { get; init; }
    public required string  SourceDatName              { get; init; }
    public required string  SourceDatSha256            { get; init; }
    public          string? SemanticFingerprint        { get; init; }        // v1: null
    public          int?    SemanticFingerprintVersion { get; init; }        // v1: null
    public          int     LastSeenGroupRevision      { get; init; } = 0;   // v1: 0

    /// <summary>Working states declared by this leaf's DAT (empty for most DATs, e.g. TOSEC).</summary>
    public IReadOnlyList<GroupDatInitialWorkingState> InitialWorkingStates { get; init; } = [];
}

/// <summary>
/// A complete request to atomically register a new Group DAT and all of its leaves in the catalog.
/// Consumed only by <see cref="CatalogService.CreateDatGroupWithLeaves"/>. Carries no filesystem paths
/// to open — the caller is responsible for having built/verified the leaf databases beforehand.
/// </summary>
public sealed record GroupDatCatalogCreateRequest
{
    public required string GroupId          { get; init; }
    public required string DisplayName      { get; init; }
    public required string HardwareFamilyId { get; init; }   // System id
    public required string Authority        { get; init; }
    public required IReadOnlyList<GroupDatCatalogLeafCreate> Leaves { get; init; }
}

/// <summary>One leaf returned by <see cref="CatalogService.GetLeavesForGroup"/>: the dat_line row and its Group metadata.</summary>
public sealed record GroupLeafRecord(DatLineRecord DatLine, DatLineGroupMetadataRecord GroupMetadata);

/// <summary>Distinguishable validation failures for <see cref="CatalogService.CreateDatGroupWithLeaves"/>.</summary>
public enum GroupDatCatalogCreateError
{
    InvalidGroupId,
    GroupIdCollision,
    EmptyDisplayName,
    HardwareFamilyMissing,
    InvalidAuthority,
    NoLeaves,
    InvalidLeafId,
    DuplicateLeafIdInPayload,
    LeafIdCollision,
    LeafSystemMismatch,
    LeafAuthorityMismatch,
    MediaTypeMissing,
    EmptyDataStorePath,
    DuplicateDataStorePathInPayload,
    DataStorePathCollision,
    InvalidRelativeDatPath,
    EmptySourceDatName,
    InvalidSourceSha256,
    InvalidLastSeenRevision,
    InvalidSemanticFingerprint,
}

/// <summary>
/// A rejected <see cref="CatalogService.CreateDatGroupWithLeaves"/> request — a deterministic validation
/// failure, distinct from an unexpected <c>SqliteException</c>. Carries the typed <see cref="Error"/> and,
/// where applicable, the offending <see cref="LeafId"/>. Thrown before/within the transaction; the
/// transaction is always rolled back, so no partial catalog state remains.
/// </summary>
public sealed class GroupDatCatalogValidationException : Exception
{
    public GroupDatCatalogCreateError Error  { get; }
    public string?                    LeafId { get; }

    public GroupDatCatalogValidationException(GroupDatCatalogCreateError error, string message, string? leafId = null)
        : base(message)
    {
        Error  = error;
        LeafId = leafId;
    }
}

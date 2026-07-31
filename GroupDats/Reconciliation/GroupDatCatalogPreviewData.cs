using System;
using System.Collections.Immutable;

namespace Arkadia.GroupDats;

/// <summary>One selectable option (hardware family / media type / authority) for the preview.</summary>
public sealed record GroupDatOption(string Id, string Name);

/// <summary>
/// Read-only snapshot of one existing Group-DAT leaf, built entirely from catalog.db (no leaf DB
/// open). The previous DAT header <c>date</c>/<c>author</c> are not persisted and are intentionally
/// absent here — the UI shows them as "not available".
/// </summary>
public sealed record GroupDatExistingLeaf(
    string  DatLineId,
    string  GroupId,
    string? RelativeDatPath,
    string? SourceDatName,
    string  Version,
    int     ReleaseCount,
    string  MediaTypeId,
    string  HardwareFamilyId,
    string  Authority,
    int?    LastSeenGroupRevision);

/// <summary>Read-only snapshot of one existing Group DAT and its leaves.</summary>
public sealed record GroupDatExistingGroup(
    string                                Id,
    string                                DisplayName,
    string                                HardwareFamilyId,
    string                                Authority,
    int                                   CurrentRevision,
    ImmutableArray<GroupDatExistingLeaf>  Leaves);

/// <summary>
/// Immutable catalog snapshot passed by MainWindow (built from its live <c>_catalog</c>) into the
/// reconciliation preview. The preview receives ONLY this data — never a CatalogService,
/// DatLineStore, connection string, data directory, or any write callback — which is what keeps the
/// window structurally non-mutating.
/// </summary>
public sealed class GroupDatCatalogPreviewData
{
    public required ImmutableArray<GroupDatExistingGroup> ExistingGroups   { get; init; }
    public required ImmutableArray<GroupDatOption>        HardwareFamilies { get; init; }
    public required ImmutableArray<GroupDatOption>        MediaTypes       { get; init; }
    public required ImmutableArray<GroupDatOption>        Authorities      { get; init; }

    /// <summary>Every already-occupied leaf id (all <c>dat_lines.id</c>), for case-insensitive collision checks.</summary>
    public required ImmutableHashSet<string> OccupiedLeafIds { get; init; }

    public static GroupDatCatalogPreviewData Empty { get; } = new()
    {
        ExistingGroups   = ImmutableArray<GroupDatExistingGroup>.Empty,
        HardwareFamilies = ImmutableArray<GroupDatOption>.Empty,
        MediaTypes       = ImmutableArray<GroupDatOption>.Empty,
        Authorities      = ImmutableArray<GroupDatOption>.Empty,
        OccupiedLeafIds  = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase),
    };
}

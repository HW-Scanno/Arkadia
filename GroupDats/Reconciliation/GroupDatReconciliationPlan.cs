using System.Collections.Immutable;
using Arkadia.Data;

namespace Arkadia.GroupDats;

/// <summary>Planned update of one existing leaf from a discovered DAT (no execution here).</summary>
public sealed record GroupDatUpdateActionPlan(
    string ExistingLeafId,
    string SourceRelativePath,
    string SourcePath,
    string HeaderName,
    int    ReleaseCount);

/// <summary>Planned creation of one new leaf from a discovered DAT.</summary>
public sealed record GroupDatNewLeafPlan(
    string LeafId,
    string MediaTypeId,
    string SourceRelativePath,
    string SourcePath,
    string HeaderName,
    string Version,
    int    ReleaseCount);

/// <summary>An existing leaf declared absent from the new revision (retained, never deleted).</summary>
public sealed record GroupDatAbsentLeafPlan(string ExistingLeafId);

/// <summary>
/// Deeply-immutable, execution-free plan produced only when the reconciliation is complete. Holds
/// the frozen decisions plus the immutable discovery snapshot (for a future reparse-and-compare
/// stability check). It contains no CatalogService/DatLineStore/connection string, no mutable parser
/// models (<see cref="DiscoveredDatLeaf"/> exposes only immutable snapshot types), no execution
/// state, no journal, no fingerprints, and no scores. The plan performs no operations.
/// </summary>
public sealed record GroupDatReconciliationPlan(
    GroupDatReconciliationMode                  Mode,
    string                                      SourceRoot,
    string                                      SystemId,
    string                                      SystemName,
    string                                      Authority,
    string                                      GroupId,          // stable technical key + leaf-id prefix
    string                                      GroupName,        // human-readable display_name (distinct from GroupId)
    string                                      HardwareFamilyId,
    ImmutableArray<GroupDatUpdateActionPlan>    Updates,
    ImmutableArray<GroupDatNewLeafPlan>         NewLeaves,
    ImmutableArray<GroupDatAbsentLeafPlan>      AbsentLeaves,
    ImmutableArray<DiscoveredDatLeaf>           DiscoverySnapshot);

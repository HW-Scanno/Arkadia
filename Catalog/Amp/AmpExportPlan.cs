using System.Collections.Generic;

namespace Arkadia;

public sealed record AmpExportPlan(
    string                               HardwareFamilyId,
    string                               DatLineId,
    string                               SystemName,
    int                                  ReleaseCount,
    int                                  ReleasesWithMetadata,
    int                                  ReleasesWithMedia,
    int                                  TotalMediaFiles,
    long                                 TotalBytes,
    int                                  ExclusionCount,
    int                                  ExtraNotesCount,
    IReadOnlyList<AmpExportPlanRelease>  Releases,
    IReadOnlyList<AmpExportPlanIssue>    Issues);

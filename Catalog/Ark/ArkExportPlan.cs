using System.Collections.Generic;

namespace Arkadia;

public sealed record ArkExportPlan(
    string                            DataDir,
    IReadOnlyList<ArkExportPlanStore> Stores,
    long                              EstimatedUncompressedBytes,
    int                               DatLineCount,
    bool                              CredentialsExcluded,
    bool                              CachePackagesExcluded,
    bool                              MediaIncluded,
    long                              MediaEstimatedBytes,
    bool                              AmpRegistryIncluded,
    int                               AmpPackageCount,
    IReadOnlyList<string>             Warnings,
    IReadOnlyList<string>             Issues);

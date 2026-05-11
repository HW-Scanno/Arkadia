using System.Collections.Generic;

namespace Arkadia;

public sealed record ArkRestorePlan(
    string                             ArkFilePath,
    string                             TargetDataDir,
    string                             FormatName,
    string                             FormatVersion,
    bool                               PackageValid,
    bool                               TargetExists,
    bool                               TargetIsEmpty,
    bool                               RequiresOverwrite,
    int                                StoreCount,
    int                                DatLineDbCount,
    long                               TotalRestoreBytes,
    IReadOnlyList<ArkRestorePlanEntry> Entries,
    IReadOnlyList<string>              Warnings,
    IReadOnlyList<string>              Issues);

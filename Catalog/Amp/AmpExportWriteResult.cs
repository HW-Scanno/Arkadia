using System.Collections.Generic;

namespace Arkadia;

public sealed record AmpExportWriteResult(
    bool                              Success,
    string                            OutputPath,
    long                              PackageBytes,
    string                            Sha256,
    int                               ReleaseCount,
    int                               MediaFileCount,
    long                              TotalMediaBytes,
    IReadOnlyList<AmpExportPlanIssue> Issues);

using System.Collections.Generic;

namespace Arkadia;

public sealed record ArkWriteResult(
    bool                  Success,
    string                OutputPath,
    string                SidecarPath,
    long                  PackageBytes,
    string                Sha256,
    int                   DatLineCount,
    int                   StoreCount,
    bool                  MediaIncluded,
    bool                  AmpRegistryIncluded,
    IReadOnlyList<string> Issues);

namespace Arkadia;

public sealed record AmpManifestInfo(
    string  FormatName,
    string  FormatVersion,
    string  HardwareFamilyId,
    string  DatLineId,
    string  SystemName,
    int     ReleaseCount,
    int     MediaFileCount,
    long    TotalMediaBytes,
    int     ExclusionCount,
    int     ExtraNotesCount,
    string? AttributionNotice         = null,
    string? AttributionGeneralCredits = null);

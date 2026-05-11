using System;

namespace Arkadia;

public sealed record AmpLocalPackageInfo(
    string                        FilePath,
    string                        FileName,
    long                          PackageBytes,
    string                        PackageSha256,
    string                        Status,
    bool                          HasErrors,
    bool                          HasWarnings,
    string                        FormatName,
    string                        FormatVersion,
    string                        HardwareFamilyId,
    string                        DatLineId,
    string                        SystemName,
    int                           ReleaseCount,
    int                           MediaFileCount,
    long                          TotalMediaBytes,
    int                           ExclusionCount,
    int                           ExtraNotesCount,
    DateTimeOffset                LastWriteTimeUtc,
    AmpPackageVerificationResult? VerificationResult);

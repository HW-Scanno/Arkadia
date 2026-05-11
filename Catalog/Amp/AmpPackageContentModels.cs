using System.Collections.Generic;

namespace Arkadia;

public sealed record AmpReleaseInfo(
    string                           ReleaseId,
    string                           DatName,
    string                           Title,
    string                           OriginalTitle,
    string                           SortTitle,
    string                           Developer,
    string                           Publisher,
    string                           Year,
    string                           Languages,
    string                           AlternateTitles,
    string                           Description,
    string                           Genre,
    string                           Subgenre,
    string                           Players,
    string                           ReleaseType,
    string                           Rating,
    IReadOnlyList<AmpMediaEntryInfo> Media);

public sealed record AmpMediaEntryInfo(
    string  MediaType,
    string  ArchivePath,
    string  FileName,
    string  Sha256,
    long    SizeBytes,
    bool    Preferred,
    string? Credits);

public sealed record AmpExclusionInfo(
    string ReleaseId,
    string DatName,
    string MediaType,
    string Sha256);

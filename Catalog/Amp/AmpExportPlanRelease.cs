using System.Collections.Generic;

namespace Arkadia;

public sealed record AmpExportPlanRelease(
    string                                 ReleaseId,
    string                                 DatName,
    string                                 Title,
    string                                 OriginalTitle,
    string                                 SortTitle,
    string                                 Developer,
    string                                 Publisher,
    string                                 Year,
    string                                 Languages,
    string                                 AlternateTitles,
    string                                 Description,
    string                                 Genre,
    string                                 Subgenre,
    string                                 Players,
    string                                 ReleaseType,
    string                                 Rating,
    bool                                   HasMetadata,
    IReadOnlyList<AmpExportPlanMediaEntry> MediaEntries,
    IReadOnlyList<string>                  ExclusionHashes,
    string?                                ExtraNotes,
    IReadOnlyList<AmpExportPlanIssue>      Issues);

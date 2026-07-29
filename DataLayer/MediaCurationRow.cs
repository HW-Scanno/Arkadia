namespace Arkadia.Data;

/// <summary>
/// A raw curation row as stored in release_media_curation.
/// Owned by DatLineStore; business logic lives in ReleaseMediaCurationService.
/// </summary>
public sealed record MediaCurationRow(
    string  ReleaseId,
    string  MediaType,
    string  FilePath,
    string? FileSha256,
    bool    IsPreferred,
    bool    IsExcluded,
    string? ExcludedReason,
    string? Credits,
    string? Notes);

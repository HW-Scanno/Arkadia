namespace Arkadia.Systems;

/// <summary>One DAT line shown in the Systems detail panel.</summary>
/// <param name="LibraryPlatform">Platform key used in Library datasets, or null if no matching dataset.</param>
/// <param name="LibraryDatLine">DAT line key used in Library datasets, or null if no matching dataset.</param>
/// <param name="CatalogId">DatLineRecord.Id — non-null when the row comes from persisted catalog data (enables delete).</param>
/// <param name="CatalogPlatformId">Platform folder id — required alongside CatalogId for release path lookups.</param>
public sealed record DatLineInfo(
    string  Name,
    int     Releases,
    string  LastImport,
    string  StorageStrategy       = "",
    string  Authority             = "",
    string  DatCategory           = "",
    string  DataStorePath         = "",
    string? LibraryPlatform       = null,
    string? LibraryDatLine        = null,
    string? CatalogId             = null,
    string? CatalogPlatformId     = null,
    int     Outdated              = 0,
    string  TransformStrategyType = "none",
    string  FolderTransformId     = "",
    string  FileHandling          = "archives_pre_extraction");

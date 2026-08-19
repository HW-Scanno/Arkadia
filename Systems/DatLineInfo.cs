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
    string  MediaTypeId           = "",
    string  DataStorePath         = "",
    string? LibraryPlatform       = null,
    string? LibraryDatLine        = null,
    string? CatalogId             = null,
    string? CatalogPlatformId     = null,
    int     Outdated              = 0,
    string  TransformStrategyType = "none",
    string  FolderTransformId     = "",
    string  FileHandling          = "archives_pre_extraction",
    // ── Completion + archive-lifecycle facts (from the same leaf-DB open that reads Outdated) ──
    int     Present               = 0,
    int     Unwanted              = 0,
    int     OwnedPresentDerived   = 0,
    int     Unassigned            = 0,
    bool    AnyLocalVolume        = false,
    bool    HasStaging            = false)
{
    /// <summary>Wanted-coverage percent for this DAT (present ÷ max(0, releases − unwanted)); null ⇒ "N/A".</summary>
    public int? CompletionPercent => CompletionMath.Percent(Present, Releases, Unwanted);

    /// <summary>Completion as a display string; "N/A" when there are no wanted releases.</summary>
    public string CompletionText => CompletionMath.Text(CompletionPercent);

    /// <summary>Archive lifecycle for this Single DAT (all facts are known — no pending state).</summary>
    public ArchiveLifecycle Lifecycle => StorageLifecycle.Derive(OwnedPresentDerived, Unassigned, AnyLocalVolume);
}

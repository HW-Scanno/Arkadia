namespace Arkadia.Library;

public sealed class BulkOperationReleaseRow
{
    public required string ReleaseId       { get; init; }
    public required string ReleaseName     { get; init; }
    public required string Status          { get; init; }
    public required bool   ShowInCatalog   { get; init; }
    public required bool   IsNoOp          { get; init; }
    public required string Note            { get; init; }
    public          int    ArchiveFileCount { get; init; }
    public          long   ArchiveBytes     { get; init; }
    public          bool   HasWarning       { get; init; }

    /// <summary>Title-cased status for display, matching Library list convention.</summary>
    public string StatusDisplay => Status.Length == 0 ? ""
        : char.ToUpperInvariant(Status[0]) + Status[1..];
}

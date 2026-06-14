using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Library;

public sealed class LibraryBulkOperationPlan
{
    public required string                         DatLineId    { get; init; }
    public required string                         DatLineLabel { get; init; }
    public required string                         MatchText    { get; init; }
    public required LibraryBulkOperationType       Operation    { get; init; }
    public required IReadOnlyList<BulkOperationReleaseRow> Rows { get; init; }

    public int TotalMatches    => Rows.Count;
    public int ActionableCount => Rows.Count(r => !r.IsNoOp);
    public int NoOpCount       => Rows.Count(r => r.IsNoOp);

    public bool IsLargeBatch     => ActionableCount > 50;
    public bool IsVeryLargeBatch => ActionableCount > 200;

    public int MissingCount  => Rows.Count(r => r.Status == "missing");
    public int PresentCount  => Rows.Count(r => r.Status == "present");
    public int PendingCount  => Rows.Count(r => r.Status == "pending");
    public int OutdatedCount => Rows.Count(r => r.Status == "outdated");
    public int LostCount     => Rows.Count(r => r.Status == "lost");
    public int UnwantedCount => Rows.Count(r => r.Status == "unwanted");

    public int  TotalArchiveFiles => Rows.Sum(r => r.ArchiveFileCount);
    public long TotalArchiveBytes => Rows.Sum(r => r.ArchiveBytes);

    /// <summary>Typed confirmation phrase required for large batch execution (>200 actionable).</summary>
    public string ConfirmationPhrase => Operation switch
    {
        LibraryBulkOperationType.HideFromCatalog     => IsVeryLargeBatch ? $"HIDE {ActionableCount}"    : "HIDE FROM CATALOG",
        LibraryBulkOperationType.ShowInCatalog       => IsVeryLargeBatch ? $"SHOW {ActionableCount}"    : "SHOW IN CATALOG",
        LibraryBulkOperationType.PurgeAndMarkUnwanted => IsVeryLargeBatch ? $"PURGE {ActionableCount}"  : "PURGE UNWANTED",
        LibraryBulkOperationType.RestoreWanted        => IsVeryLargeBatch ? $"RESTORE {ActionableCount}": "RESTORE WANTED",
        _                                             => "",
    };
}

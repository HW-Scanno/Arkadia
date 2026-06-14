using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Library;

/// <summary>
/// Builds a <see cref="LibraryBulkOperationPlan"/> without performing any side effects.
/// All database access is read-only.
/// </summary>
public sealed class LibraryBulkOperationPlanner
{
    private readonly DatLineStore _store;

    public LibraryBulkOperationPlanner(DatLineStore store)
        => _store = store;

    public LibraryBulkOperationPlan Plan(
        string                   datLineId,
        string                   datLineLabel,
        string                   matchText,
        LibraryBulkOperationType operation)
    {
        if (matchText.Trim().Length == 0)
            return EmptyPlan(datLineId, datLineLabel, matchText, operation);

        var releases = _store.LoadReleasesByDatLine(datLineId);
        var matched  = releases
            .Where(r => r.Name.Contains(matchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rows = new List<BulkOperationReleaseRow>(matched.Count);
        foreach (var r in matched)
            rows.Add(BuildRow(r, operation));

        return new LibraryBulkOperationPlan
        {
            DatLineId    = datLineId,
            DatLineLabel = datLineLabel,
            MatchText    = matchText,
            Operation    = operation,
            Rows         = rows,
        };
    }

    private BulkOperationReleaseRow BuildRow(ReleaseRecord r, LibraryBulkOperationType operation)
        => operation switch
        {
            LibraryBulkOperationType.HideFromCatalog      => BuildHideRow(r),
            LibraryBulkOperationType.ShowInCatalog        => BuildShowRow(r),
            LibraryBulkOperationType.PurgeAndMarkUnwanted => BuildPurgeRow(r),
            LibraryBulkOperationType.RestoreWanted        => BuildRestoreRow(r),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static BulkOperationReleaseRow BuildHideRow(ReleaseRecord r)
    {
        bool isNoOp = !r.ShowInCatalog;
        return new BulkOperationReleaseRow
        {
            ReleaseId     = r.Id,
            ReleaseName   = r.Name,
            Status        = r.Status,
            ShowInCatalog = r.ShowInCatalog,
            IsNoOp        = isNoOp,
            Note          = isNoOp ? "Already hidden from catalog" : "Will be hidden from catalog",
        };
    }

    private static BulkOperationReleaseRow BuildShowRow(ReleaseRecord r)
    {
        bool isNoOp = r.ShowInCatalog;
        return new BulkOperationReleaseRow
        {
            ReleaseId     = r.Id,
            ReleaseName   = r.Name,
            Status        = r.Status,
            ShowInCatalog = r.ShowInCatalog,
            IsNoOp        = isNoOp,
            Note          = isNoOp ? "Already visible in catalog" : "Will be shown in catalog",
        };
    }

    private BulkOperationReleaseRow BuildPurgeRow(ReleaseRecord r)
    {
        if (r.Status == "unwanted")
            return new BulkOperationReleaseRow
            {
                ReleaseId     = r.Id,
                ReleaseName   = r.Name,
                Status        = r.Status,
                ShowInCatalog = r.ShowInCatalog,
                IsNoOp        = true,
                Note          = "Already unwanted — no action needed",
            };

        var artifacts = _store.GetDerivedArtifactsByReleaseId(r.Id);
        int  count    = artifacts.Count;
        long bytes    = artifacts.Sum(a => a.DerivedSizeBytes);
        return new BulkOperationReleaseRow
        {
            ReleaseId        = r.Id,
            ReleaseName      = r.Name,
            Status           = r.Status,
            ShowInCatalog    = r.ShowInCatalog,
            IsNoOp           = false,
            ArchiveFileCount = count,
            ArchiveBytes     = bytes,
            Note             = count == 0
                ? "Will be marked unwanted (no archive files)"
                : $"Will be purged — {count} archive file{(count == 1 ? "" : "s")}",
        };
    }

    private static BulkOperationReleaseRow BuildRestoreRow(ReleaseRecord r)
    {
        bool isNoOp = r.Status != "unwanted";
        return new BulkOperationReleaseRow
        {
            ReleaseId     = r.Id,
            ReleaseName   = r.Name,
            Status        = r.Status,
            ShowInCatalog = r.ShowInCatalog,
            IsNoOp        = isNoOp,
            Note          = isNoOp
                ? $"Not unwanted (status: {r.Status})"
                : "Will be restored to wanted",
        };
    }

    private static LibraryBulkOperationPlan EmptyPlan(
        string datLineId, string datLineLabel, string matchText, LibraryBulkOperationType operation)
        => new()
        {
            DatLineId    = datLineId,
            DatLineLabel = datLineLabel,
            MatchText    = matchText,
            Operation    = operation,
            Rows         = Array.Empty<BulkOperationReleaseRow>(),
        };
}

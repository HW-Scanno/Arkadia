using System;
using System.Collections.Generic;
using Arkadia.Data;
using Arkadia.Purge;

namespace Arkadia.Library;

public sealed record LibraryBulkOperationProgress(
    int     Done,
    int     Total,
    string  ReleaseName,
    bool    Success,
    string? Error = null);

public sealed class LibraryBulkOperationResult
{
    public required int                   Succeeded { get; init; }
    public required int                   Failed    { get; init; }
    public required int                   Skipped   { get; init; }
    public required IReadOnlyList<string> Errors    { get; init; }
}

/// <summary>
/// Executes a <see cref="LibraryBulkOperationPlan"/> produced by <see cref="LibraryBulkOperationPlanner"/>.
/// Reuses <see cref="PurgeReleaseService"/> for purge rows and <see cref="DatLineStore"/> for
/// hide/show/restore rows.
/// </summary>
public sealed class LibraryBulkOperationService
{
    private readonly string         _appRoot;
    private readonly CatalogService _catalog;

    public LibraryBulkOperationService(string appRoot, CatalogService catalog)
    {
        _appRoot = appRoot;
        _catalog = catalog;
    }

    public LibraryBulkOperationResult Execute(
        LibraryBulkOperationPlan                  plan,
        string                                    dbPath,
        IProgress<LibraryBulkOperationProgress>?  progress = null)
    {
        var store       = new DatLineStore(dbPath);
        var planner     = new PurgeReleasePlanner(_appRoot, _catalog);
        var purgeService = new PurgeReleaseService(_appRoot, _catalog);
        var errors      = new List<string>();
        int succeeded   = 0;
        int failed      = 0;
        int skipped     = 0;
        int total       = plan.ActionableCount;
        int done        = 0;

        foreach (var row in plan.Rows)
        {
            if (row.IsNoOp) { skipped++; continue; }

            bool success = true;
            string? error = null;
            try
            {
                ExecuteRow(row, plan, store, planner, purgeService, dbPath);
                succeeded++;
            }
            catch (Exception ex)
            {
                success = false;
                error   = ex.Message;
                failed++;
                errors.Add($"{row.ReleaseName}: {ex.Message}");
            }

            done++;
            progress?.Report(new LibraryBulkOperationProgress(done, total, row.ReleaseName, success, error));
        }

        return new LibraryBulkOperationResult
        {
            Succeeded = succeeded,
            Failed    = failed,
            Skipped   = skipped,
            Errors    = errors,
        };
    }

    private static void ExecuteRow(
        BulkOperationReleaseRow  row,
        LibraryBulkOperationPlan plan,
        DatLineStore             store,
        PurgeReleasePlanner      planner,
        PurgeReleaseService      purgeService,
        string                   dbPath)
    {
        switch (plan.Operation)
        {
            case LibraryBulkOperationType.HideFromCatalog:
                store.SetShowInCatalog(row.ReleaseId, false);
                break;

            case LibraryBulkOperationType.ShowInCatalog:
                store.SetShowInCatalog(row.ReleaseId, true);
                break;

            case LibraryBulkOperationType.PurgeAndMarkUnwanted:
                var purgePlan = planner.Plan(
                    row.ReleaseId, row.ReleaseName, row.Status, plan.DatLineId, dbPath);
                if (!purgePlan.CanExecute)
                    throw new InvalidOperationException(string.Join("; ", purgePlan.Issues));
                var purgeResult = purgeService.Execute(purgePlan);
                if (!purgeResult.Success)
                    throw new InvalidOperationException(purgeResult.ErrorMessage ?? "Purge failed");
                break;

            case LibraryBulkOperationType.RestoreWanted:
                store.RestoreWantedRelease(row.ReleaseId);
                break;
        }
    }
}

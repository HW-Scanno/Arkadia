using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Activities;
using Arkadia.Data.Identifiers;

namespace Arkadia.Data;

/// <summary>Input to <see cref="SingleDatImportService.Execute"/>. The id is NOT supplied: it is rebuilt from the structured fields.</summary>
public sealed class SingleDatImportRequest
{
    public required string                              HardwareFamilyId { get; init; }
    public required string                              Authority        { get; init; }
    public required string                              MediaTypeId      { get; init; }
    public required string                              Variant          { get; init; }
    public required string                              DisplayName      { get; init; }
    public          string                              Version          { get; init; } = "";
    public required IReadOnlyList<DatParser.ParsedGame> Games            { get; init; }
}

public enum SingleDatImportError
{
    None = 0,
    InvalidId,
    InvalidDisplayName,
    UnknownSystem,
    UnknownMediaType,
    NoReleases,
    IdCollision,
    LeafDatabaseExists,
    LeafBuildFailed,
    CatalogWriteFailed,
    Cancelled,
}

/// <summary>Outcome of a Single DAT import. <see cref="DatLineId"/> is set only on success.</summary>
public sealed record SingleDatImportResult(
    bool Success, string? DatLineId, SingleDatImportError Error, string Message, int ReleaseCount)
{
    internal static SingleDatImportResult Fail(SingleDatImportError e, string message) => new(false, null, e, message, 0);
}

/// <summary>
/// UI-free creation of a NEW standalone (Single) DAT. It is the single place that turns validated import input
/// into a real DAT, so the id used for the <c>dat_lines</c> row, the leaf database file, and the
/// <c>data_store_path</c> is one and the same string, built by <see cref="SingleDatIdBuilder"/> from the
/// structured fields (never accepted pre-built from the caller).
///
/// <para>Order (all-or-nothing from the catalog's point of view): revalidate id + display name against the LIVE
/// catalog → map releases in memory → build + verify the leaf DB in a per-execution temp file → revalidate again
/// (the build can be long) → publish by rename (never over an existing file) → one atomic INSERT
/// (<see cref="CatalogService.CreateSingleDatLine"/>, never an upsert). The catalog row therefore appears only
/// after a verified leaf DB exists; any failure leaves the catalog untouched and removes only the files THIS
/// execution created. Group DAT code is not involved.</para>
/// </summary>
public sealed class SingleDatImportService
{
    private readonly CatalogService   _catalog;
    private readonly string           _dataDir;
    private readonly ActivityMonitor? _activities;

    // Test-only seams (never set in production): fire after the temp DB is built / after the rename publish.
    internal Action? OnLeafBuiltForTests;
    internal Action? OnLeafPublishedForTests;

    /// <param name="activities">
    /// The application's shared <see cref="ActivityMonitor"/> (null = not observed, e.g. tests of the import itself).
    /// The monitor only OBSERVES: it never influences validation, the build, the publish, the catalog write or cleanup.
    /// </param>
    public SingleDatImportService(CatalogService catalog, string dataDir, ActivityMonitor? activities = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? throw new ArgumentException("dataDir is required.", nameof(dataDir)) : dataDir;
        _activities = activities;
    }

    public SingleDatImportResult Execute(
        SingleDatImportRequest               request,
        IProgress<LeafDatBuildProgress>?     progress,
        CancellationToken                    ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // IMPORTING is registered when processing actually begins (this method runs only after the user confirmed the
        // import — never while the configuration dialog is open) and is released by the `using` on EVERY exit: success,
        // validation failure, build failure, cancellation (returned as a result) or an unexpected exception. Because the
        // method's own `finally` (temp-file cleanup) runs before the scope is disposed, IMPORTING outlives the cleanup.
        using var activity = _activities?.Begin(
            ActivityKind.Importing,
            $"Import DAT {request.HardwareFamilyId}-{request.Authority}-{request.MediaTypeId}-{request.Variant}");

        var nameError = DatLineDisplayName.Validate(request.DisplayName);
        if (nameError is not null)
            return SingleDatImportResult.Fail(SingleDatImportError.InvalidDisplayName, nameError);
        if (request.Games.Count == 0)
            return SingleDatImportResult.Fail(SingleDatImportError.NoReleases, "The DAT contains no releases.");

        var pre = Revalidate(request, out var datLineId, out var relPath, out var finalAbs);
        if (pre is not null) return pre;

        var tempAbs = finalAbs + ".tmp-" + Guid.NewGuid().ToString("N");
        var published = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            LeafDatPreparedBuild prepared;
            try
            {
                prepared = LeafDatDatabaseBuilder.Prepare(datLineId, request.Games, ct);
                LeafDatDatabaseBuilder.Build(tempAbs, prepared, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return SingleDatImportResult.Fail(SingleDatImportError.LeafBuildFailed,
                    $"The release database could not be built: {ex.Message}");
            }
            OnLeafBuiltForTests?.Invoke();

            ct.ThrowIfCancellationRequested();

            // Revalidate IMMEDIATELY before touching the final location / the catalog: another import (or a manual
            // file drop) may have claimed the id or the path while the temp DB was being built.
            var again = Revalidate(request, out _, out _, out _);
            if (again is not null) return again;

            try { File.Move(tempAbs, finalAbs, overwrite: false); }
            catch (Exception ex)
            {
                return SingleDatImportResult.Fail(SingleDatImportError.LeafDatabaseExists,
                    $"The release database could not be published: {ex.Message}");
            }
            published = true;
            OnLeafPublishedForTests?.Invoke();

            var record = new DatLineRecord
            {
                Id                = datLineId,
                HardwareFamilyId  = request.HardwareFamilyId,
                Name              = request.MediaTypeId,        // `name` keeps its historical meaning (= media type id)
                Authority         = request.Authority,
                MediaTypeId       = request.MediaTypeId,
                Version           = request.Version,
                StorageStrategyId = "",
                DataStorePath     = relPath,
                ReleaseCount      = prepared.ReleaseCount,
                ImportedAtUtc     = DateTime.UtcNow,
                DisplayName       = DatLineDisplayName.ToPersisted(request.DisplayName),
            };

            try { _catalog.CreateSingleDatLine(record); }
            catch (SingleDatCreateException ex)
            {
                RemoveIfCreated(finalAbs, ref published);
                return SingleDatImportResult.Fail(
                    ex.Failure == SingleDatCreateFailure.IdCollision ? SingleDatImportError.IdCollision : SingleDatImportError.CatalogWriteFailed,
                    ex.Message);
            }
            catch (Exception ex)
            {
                RemoveIfCreated(finalAbs, ref published);
                return SingleDatImportResult.Fail(SingleDatImportError.CatalogWriteFailed,
                    $"The DAT could not be registered in the catalog: {ex.Message}");
            }

            published = false;   // committed: the file is now owned by the catalog row

            string? warning = null;
            try
            {
                foreach (var game in request.Games)
                    if (game.WorkingState.Length > 0)
                        _catalog.SetWorkingStateIfNotManual(game.Name, game.WorkingState);
            }
            catch (Exception ex) { warning = $" (working-state hints could not all be applied: {ex.Message})"; }

            return new SingleDatImportResult(true, datLineId, SingleDatImportError.None, "Imported." + warning, prepared.ReleaseCount);
        }
        catch (OperationCanceledException)
        {
            if (published) RemoveIfCreated(finalAbs, ref published);
            return SingleDatImportResult.Fail(SingleDatImportError.Cancelled, "The import was cancelled. No DAT was created.");
        }
        finally
        {
            DeleteQuietly(tempAbs);
        }
    }

    /// <summary>
    /// Rebuilds the id from the structured fields against the CURRENT catalog and checks the target paths.
    /// Returns a failure result, or null when the id is valid and nothing exists at the target.
    /// </summary>
    private SingleDatImportResult? Revalidate(
        SingleDatImportRequest request, out string datLineId, out string relPath, out string finalAbs)
    {
        datLineId = ""; relPath = ""; finalAbs = "";

        var lines = _catalog.LoadDatLines();   // ALL dat_lines: Single DATs and Group leaves
        var built = SingleDatIdBuilder.Build(
            request.HardwareFamilyId, request.Authority, request.MediaTypeId, request.Variant,
            lines.Select(l => l.Id));
        if (!built.IsValid)
            return SingleDatImportResult.Fail(
                built.Error == SingleDatIdError.Collision ? SingleDatImportError.IdCollision : SingleDatImportError.InvalidId,
                built.Message);

        if (_catalog.GetHardwareFamily(request.HardwareFamilyId) is null)
            return SingleDatImportResult.Fail(SingleDatImportError.UnknownSystem, $"The system '{request.HardwareFamilyId}' does not exist.");
        if (_catalog.GetMediaTypes().All(m => !string.Equals(m.Id, request.MediaTypeId, StringComparison.Ordinal)))
            return SingleDatImportResult.Fail(SingleDatImportError.UnknownMediaType, $"The media type '{request.MediaTypeId}' does not exist.");

        var id  = built.Id!;
        var rel = $"systems/{request.HardwareFamilyId}/{id}.db";
        datLineId = id;
        relPath   = rel;
        finalAbs  = Path.Combine(_dataDir, "systems", request.HardwareFamilyId, id + ".db");

        if (lines.Any(l => string.Equals(l.DataStorePath, rel, StringComparison.OrdinalIgnoreCase)))
            return SingleDatImportResult.Fail(SingleDatImportError.IdCollision, $"Another DAT already uses '{rel}'.");
        if (File.Exists(finalAbs))
            return SingleDatImportResult.Fail(SingleDatImportError.LeafDatabaseExists,
                $"A release database already exists at '{rel}'. It is not overwritten; resolve it manually.");

        return null;
    }

    private static void RemoveIfCreated(string finalAbs, ref bool published)
    {
        if (!published) return;
        DeleteQuietly(finalAbs);
        published = false;
    }

    private static void DeleteQuietly(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort: only files this execution created */ }
    }
}

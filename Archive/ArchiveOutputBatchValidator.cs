using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Archive;

/// <summary>One problematic DAT line in a batch validation report.</summary>
public sealed record ArchiveOutputBatchIssue(
    string DatLineId,
    string DatLineName,
    string State,
    string Detail);

/// <summary>Aggregate result of validating every DAT line's archive output plan.</summary>
public sealed record ArchiveOutputBatchReport(
    int TotalScanned,
    int ValidFullSet,
    int ValidWithExclusions,
    int CollisionUnresolved,
    int UnknownOrError,
    IReadOnlyList<ArchiveOutputBatchIssue> Problematic);

/// <summary>
/// Operator maintenance action: validates every DAT line's archive output plan using
/// the M1 helpers and persists the computed form/state/fingerprints. Read-only over
/// releases — it never marks releases unwanted, never opens dialogs, never touches
/// files, and never mutates release statuses. Unknown/unresolvable lines are reported,
/// not defaulted.
/// </summary>
public sealed class ArchiveOutputBatchValidator
{
    private readonly CatalogService _catalog;
    private readonly string _dataDir;
    private readonly DatLineArchiveOutputValidationService _persist;

    /// <param name="dataDir">Base directory that <c>DatLineRecord.DataStorePath</c> is relative to.</param>
    public ArchiveOutputBatchValidator(CatalogService catalog, string dataDir)
    {
        _catalog = catalog;
        _dataDir = dataDir;
        _persist = new DatLineArchiveOutputValidationService(catalog);
    }

    public ArchiveOutputBatchReport ValidateAll()
    {
        var datLines      = _catalog.LoadDatLines();
        var allTransforms = _catalog.LoadTransforms();

        int vfs = 0, vwe = 0, cu = 0, unk = 0;
        var problems = new List<ArchiveOutputBatchIssue>();

        foreach (var dl in datLines)
        {
            var (state, detail) = ValidateOne(dl, allTransforms);
            switch (state)
            {
                case "valid_full_set":        vfs++; break;
                case "valid_with_exclusions": vwe++; break;
                case "collision_unresolved":
                    cu++;
                    problems.Add(new ArchiveOutputBatchIssue(dl.Id, dl.Name, state, detail));
                    break;
                default:   // unknown / stale / error
                    unk++;
                    problems.Add(new ArchiveOutputBatchIssue(dl.Id, dl.Name, state, detail));
                    break;
            }
        }

        return new ArchiveOutputBatchReport(datLines.Count, vfs, vwe, cu, unk, problems);
    }

    private (string State, string Detail) ValidateOne(DatLineRecord dl, List<TransformRecord> allTransforms)
    {
        if (dl.DataStorePath.Length == 0)
            return ("unknown", "no DAT store configured");

        var absPath = Path.Combine(_dataDir, dl.DataStorePath);
        if (!File.Exists(absPath))
            return ("unknown", "DAT store file not found");

        try
        {
            var store    = new DatLineStore(absPath);
            var config   = BuildConfig(dl, allTransforms);
            var releases = ArchiveOutputConfigFactory.BuildReleaseInputs(
                store.LoadReleases(), store.LoadAllReleaseFiles());

            var result = ArchiveOutputValidator.Validate(config, releases);

            // Persist the computed state (form/state/fingerprints). Never mutates releases.
            _persist.PersistResult(dl.Id, result);

            var stateDb = ArchiveOutputPersistenceMapping.StateToDb(result.State);
            var detail  = result.State == ArchiveOutputValidationState.CollisionUnresolved
                ? $"{result.WantedSubsetCollisions.Count} colliding name(s): " +
                  string.Join(", ", result.WantedSubsetCollisions.Take(3).Select(g => $"\"{g.ArchiveEntryName}\""))
                : $"form={ArchiveOutputPersistenceMapping.FormToDb(result.Form)}";
            return (stateDb, detail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ("error", ex.Message);
        }
    }

    private ArchiveOutputConfig BuildConfig(DatLineRecord dl, List<TransformRecord> allTransforms)
    {
        var strategyType = dl.TransformStrategyType.Length > 0 ? dl.TransformStrategyType : "none";

        TransformRecord? folderXf = null;
        if (strategyType == "release_folder" && dl.FolderTransformId is { Length: > 0 } fid)
            folderXf = allTransforms.FirstOrDefault(t => t.Id == fid);

        var extMappings = new Dictionary<string, ExtensionTransformMapping>(StringComparer.OrdinalIgnoreCase);
        if (strategyType == "file_extension")
            foreach (var m in _catalog.LoadExtensionMappings(dl.Id))
                extMappings[m.FileExtension] = m;

        return ArchiveOutputConfigFactory.BuildConfig(
            dl.HardwareFamilyId, dl.Id, strategyType, folderXf, extMappings, allTransforms);
    }
}

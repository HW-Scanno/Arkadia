using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// The resolved per-leaf transform configuration used at finalization: strategy, active/folder transforms
/// and tools, extension mappings, and the uniform archive output form. Pure data — no catalog access.
/// </summary>
public sealed record LeafTransformConfig
{
    public required string                                                StrategyType { get; init; }
    public required TransformRecord                                       ActiveXform  { get; init; }
    public required ToolRecord?                                           ActiveTool   { get; init; }
    public required TransformRecord?                                      FolderXform  { get; init; }
    public required ToolRecord?                                           FolderTool   { get; init; }
    public required Dictionary<string, ExtensionTransformMapping>          ExtMappings { get; init; }
    public required ArchiveDatLineOutputForm                              ArchiveForm  { get; init; }
}

/// <summary>
/// Pure resolution of a leaf's transform configuration, extracted verbatim from the finalization adapter so
/// both Single finalization (execution) and Group planning (read-only readiness) resolve it identically —
/// no ~70-line duplication. The caller loads the catalog data (transforms, tools, dat-line record,
/// extension mappings, persisted archive form) and passes it in; this method performs no catalog/DB/file I/O.
/// </summary>
public static class LeafTransformConfigResolver
{
    public static LeafTransformConfig Resolve(
        string                                               storageStrategyId,
        DatLineRecord?                                       dlRecord,
        IReadOnlyList<TransformRecord>                       allTransforms,
        IReadOnlyList<ToolRecord>                            allTools,
        IReadOnlyList<ExtensionTransformMapping>             extensionMappings,
        ArchiveDatLineOutputForm                             persistedArchiveForm,
        IReadOnlyDictionary<string, ReleaseRecord>           releases,
        IReadOnlyDictionary<string, List<ReleaseFileRecord>> allReleaseFiles)
    {
        var activeXform = allTransforms.FirstOrDefault(t => t.Id == storageStrategyId)
                       ?? new TransformRecord { Id = "no_compression", Name = "No Compression", CommandTemplate = "" };
        var activeTool  = allTools.FirstOrDefault(t => t.Id == activeXform.ToolId);

        var strategyType = "none";
        var extMap       = new Dictionary<string, ExtensionTransformMapping>(System.StringComparer.OrdinalIgnoreCase);
        TransformRecord? folderXform = null;
        ToolRecord?      folderTool  = null;

        if (dlRecord?.TransformStrategyType == "file_extension")
        {
            strategyType = "file_extension";
            foreach (var m in extensionMappings)
                extMap[m.FileExtension] = m;
        }
        else if (dlRecord?.TransformStrategyType == "release_folder" &&
                 dlRecord.FolderTransformId.Length > 0)
        {
            strategyType = "release_folder";
            folderXform = allTransforms.FirstOrDefault(t => t.Id == dlRecord.FolderTransformId);
            folderTool  = folderXform?.ToolId.Length > 0
                ? allTools.FirstOrDefault(t => t.Id == folderXform.ToolId)
                : null;
        }
        else if (dlRecord?.TransformStrategyType == "release_shape")
        {
            strategyType = "release_shape";
        }

        var archiveForm = ArchiveDatLineOutputForm.SingleFileFlat;
        if (strategyType == "file_extension")
        {
            if (persistedArchiveForm != ArchiveDatLineOutputForm.Unknown)
            {
                archiveForm = persistedArchiveForm;
            }
            else
            {
                static string ExtKey(string rom)
                {
                    var e = Path.GetExtension(rom).ToLowerInvariant();
                    return e.Length == 0 ? "(no ext)" : e;
                }
                bool anyMulti = releases.Values
                    .Where(r => r.Status != "unwanted")
                    .Any(r => allReleaseFiles.TryGetValue(r.Id, out var rf) &&
                              rf.Count(x => extMap.TryGetValue(ExtKey(x.RomName), out var m) && !m.IsDiscard) >= 2);
                archiveForm = anyMulti
                    ? ArchiveDatLineOutputForm.MultiFileReleaseFolder
                    : ArchiveDatLineOutputForm.SingleFileFlat;
            }
        }
        else if (strategyType == "release_folder")
        {
            archiveForm = folderXform is { IsFolderOriented: true, OutputIsFolder: true }
                ? ArchiveDatLineOutputForm.MultiFileReleaseFolder
                : ArchiveDatLineOutputForm.SingleFileFlat;
        }

        return new LeafTransformConfig
        {
            StrategyType = strategyType,
            ActiveXform  = activeXform,
            ActiveTool   = activeTool,
            FolderXform  = folderXform,
            FolderTool   = folderTool,
            ExtMappings  = extMap,
            ArchiveForm  = archiveForm,
        };
    }
}

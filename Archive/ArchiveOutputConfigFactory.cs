using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Archive;

/// <summary>
/// Builds the pure <see cref="ArchiveOutputConfig"/> and <see cref="ArchiveReleaseInput"/>
/// inputs from catalog/store primitives, so the config UI and the validator share one
/// construction path.
/// </summary>
public static class ArchiveOutputConfigFactory
{
    public static ArchiveOutputConfig BuildConfig(
        string platformId,
        string datLineId,
        string strategyType,
        TransformRecord? folderTransform,
        IReadOnlyDictionary<string, ExtensionTransformMapping> extMappings,
        IReadOnlyList<TransformRecord> allTransforms)
    {
        var singleExt           = "";
        var folderOutputsFolder = false;
        var rules               = new Dictionary<string, ArchiveFileExtensionRule>(StringComparer.OrdinalIgnoreCase);

        switch (strategyType)
        {
            case "release_shape":
                singleExt = ".chd";
                break;

            case "release_folder":
                folderOutputsFolder = folderTransform?.OutputIsFolder ?? false;
                singleExt           = folderTransform?.OutputExtension is { Length: > 0 } ext ? ext : ".zip";
                break;

            case "file_extension":
                foreach (var (extKey, m) in extMappings)
                {
                    var outExt = "";
                    if (!m.IsDiscard && m.TransformId.Length > 0)
                        outExt = allTransforms.FirstOrDefault(t => t.Id == m.TransformId)?.OutputExtension ?? "";
                    rules[extKey] = new ArchiveFileExtensionRule(m.IsDiscard, outExt);
                }
                break;
        }

        return new ArchiveOutputConfig
        {
            PlatformId                = platformId,
            DatLineId                 = datLineId,
            StrategyType              = strategyType,
            SingleFileOutputExtension = singleExt,
            FolderOutputsFolder       = folderOutputsFolder,
            ExtensionRules            = rules,
        };
    }

    public static List<ArchiveReleaseInput> BuildReleaseInputs(
        IEnumerable<ReleaseRecord> releases,
        IReadOnlyDictionary<string, List<ReleaseFileRecord>> allReleaseFiles)
    {
        var list = new List<ArchiveReleaseInput>();
        foreach (var r in releases)
        {
            allReleaseFiles.TryGetValue(r.Id, out var files);
            list.Add(new ArchiveReleaseInput
            {
                ReleaseId   = r.Id,
                ReleaseName = r.Name,
                Status      = r.Status,
                Files       = files ?? new List<ReleaseFileRecord>(),
            });
        }
        return list;
    }
}

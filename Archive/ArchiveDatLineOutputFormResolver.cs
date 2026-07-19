using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Archive;

/// <summary>
/// Resolves the uniform archive output form for a DAT line from its transform
/// configuration and (for file_extension) its wanted release set. Pure/DB-free.
/// </summary>
public static class ArchiveDatLineOutputFormResolver
{
    /// <summary>
    /// Rules:
    ///   release_shape (CHD)                         → SingleFileFlat
    ///   release_folder + single-file output (ZIP)   → SingleFileFlat
    ///   release_folder + folder output (No Comp.)   → MultiFileReleaseFolder
    ///   file_extension, every wanted release yields exactly one output → SingleFileFlat
    ///   file_extension, any wanted release yields 2+ outputs           → MultiFileReleaseFolder
    ///   anything else / "none"                      → Unknown
    /// Only wanted releases influence the file_extension decision.
    /// </summary>
    public static ArchiveDatLineOutputForm Resolve(
        ArchiveOutputConfig config,
        IReadOnlyList<ArchiveReleaseInput> releases)
    {
        switch (config.StrategyType)
        {
            case "release_shape":
                return ArchiveDatLineOutputForm.SingleFileFlat;

            case "release_folder":
                return config.FolderOutputsFolder
                    ? ArchiveDatLineOutputForm.MultiFileReleaseFolder
                    : ArchiveDatLineOutputForm.SingleFileFlat;

            case "file_extension":
                var anyMulti = releases
                    .Where(r => !IsUnwanted(r.Status))
                    .Any(r => CountFileExtensionOutputs(config, r) >= 2);
                return anyMulti
                    ? ArchiveDatLineOutputForm.MultiFileReleaseFolder
                    : ArchiveDatLineOutputForm.SingleFileFlat;

            default:
                return ArchiveDatLineOutputForm.Unknown;
        }
    }

    /// <summary>
    /// Resolves the form over the FULL release set, treating every release as wanted
    /// (status-agnostic). The archive output form is a stable per-DAT-line property —
    /// curation (Exclude/Restore) must never flip it — so the structural fingerprint
    /// and the actual write layout are both based on this.
    /// </summary>
    public static ArchiveDatLineOutputForm ResolveStructural(
        ArchiveOutputConfig config,
        IReadOnlyList<ArchiveReleaseInput> releases)
    {
        return config.StrategyType switch
        {
            "release_shape"  => ArchiveDatLineOutputForm.SingleFileFlat,
            "release_folder" => config.FolderOutputsFolder
                                    ? ArchiveDatLineOutputForm.MultiFileReleaseFolder
                                    : ArchiveDatLineOutputForm.SingleFileFlat,
            "file_extension" => releases.Any(r => CountFileExtensionOutputs(config, r) >= 2)
                                    ? ArchiveDatLineOutputForm.MultiFileReleaseFolder
                                    : ArchiveDatLineOutputForm.SingleFileFlat,
            _                => ArchiveDatLineOutputForm.Unknown,
        };
    }

    /// <summary>Count of derived outputs a file_extension release produces (non-discarded, mapped files).</summary>
    internal static int CountFileExtensionOutputs(ArchiveOutputConfig config, ArchiveReleaseInput r)
    {
        int count = 0;
        foreach (var f in r.Files)
        {
            if (config.ExtensionRules.TryGetValue(Ext(f.RomName), out var rule) && !rule.IsDiscard)
                count++;
        }
        return count;
    }

    internal static bool IsUnwanted(string status)
        => string.Equals(status, "unwanted", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lowercased extension including the dot, or "(no ext)".</summary>
    internal static string Ext(string romName)
    {
        var e = Path.GetExtension(romName).ToLowerInvariant();
        return e.Length == 0 ? "(no ext)" : e;
    }
}

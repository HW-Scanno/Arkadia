using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Ingestion;

namespace Arkadia.Archive;

/// <summary>
/// Builds the planned archive output candidate for each release under a resolved
/// DAT-line output form. Release-name-based naming for single-file forms; original
/// filenames preserved inside the release folder for bundle form. Pure/DB-free.
/// Uses <see cref="IngestionPaths.SafeFolderName"/> — the production name authority.
/// </summary>
public static class ArchiveOutputPlanBuilder
{
    public static IReadOnlyList<ArchiveOutputCandidate> Build(
        ArchiveOutputConfig config,
        ArchiveDatLineOutputForm form,
        IReadOnlyList<ArchiveReleaseInput> releases)
    {
        var list = new List<ArchiveOutputCandidate>(releases.Count);
        foreach (var r in releases)
            list.Add(BuildCandidate(config, form, r));
        return list;
    }

    private static ArchiveOutputCandidate BuildCandidate(
        ArchiveOutputConfig config, ArchiveDatLineOutputForm form, ArchiveReleaseInput r)
    {
        var safe        = IngestionPaths.SafeFolderName(r.ReleaseName);
        var sourceFiles = r.Files.Select(ToSourceFile).ToList();
        long totalBytes = sourceFiles.Sum(sf => sf.SizeBytes ?? 0);
        var (mainInput, cik) = ResolveMainAndCik(config, r);

        if (form == ArchiveDatLineOutputForm.SingleFileFlat)
        {
            var ext      = SingleFileExtension(config, r);
            var filename = safe + ext;
            return new ArchiveOutputCandidate
            {
                ReleaseId = r.ReleaseId, ReleaseName = r.ReleaseName, SafeReleaseName = safe,
                Status = r.Status, Form = form,
                ArchiveEntryName    = filename,
                PlannedFilename     = filename,
                PlannedRelativePath = $"archive/{config.PlatformId}/{config.DatLineId}/{filename}",
                MainInputFile       = mainInput,
                SourceFiles         = sourceFiles,
                ContentIdentityKey  = cik,
                TotalSourceBytes    = totalBytes,
                PlannedOutputCount  = 1,
            };
        }

        if (form == ArchiveDatLineOutputForm.MultiFileReleaseFolder)
        {
            var inner = PlannedInnerFilenames(config, r);
            return new ArchiveOutputCandidate
            {
                ReleaseId = r.ReleaseId, ReleaseName = r.ReleaseName, SafeReleaseName = safe,
                Status = r.Status, Form = form,
                ArchiveEntryName      = safe,   // folder name is the collision key
                PlannedFilename       = "",
                PlannedRelativePath   = $"archive/{config.PlatformId}/{config.DatLineId}/{safe}",
                MainInputFile         = mainInput,
                SourceFiles           = sourceFiles,
                PlannedInnerFilenames = inner,
                ContentIdentityKey    = cik,
                TotalSourceBytes      = totalBytes,
                PlannedOutputCount    = inner.Count,
            };
        }

        // Unknown form — no plan can be computed.
        return new ArchiveOutputCandidate
        {
            ReleaseId = r.ReleaseId, ReleaseName = r.ReleaseName, SafeReleaseName = safe,
            Status = r.Status, Form = ArchiveDatLineOutputForm.Unknown,
            ArchiveEntryName    = "",
            PlannedRelativePath = "",
            MainInputFile       = mainInput,
            SourceFiles         = sourceFiles,
            ContentIdentityKey  = cik,
            TotalSourceBytes    = totalBytes,
            PlannedOutputCount  = 0,
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ArchiveSourceFile ToSourceFile(ReleaseFileRecord f)
        => new(f.RomName,
               long.TryParse(f.Size, out var sz) ? sz : (long?)null,
               f.Sha1, f.Md5, f.Crc);

    /// <summary>Output extension for a single-file release under the current strategy.</summary>
    private static string SingleFileExtension(ArchiveOutputConfig config, ArchiveReleaseInput r)
    {
        if (config.StrategyType == "file_extension")
        {
            // The single non-discarded file's mapped output extension (fallback: its own ext).
            foreach (var f in r.Files)
            {
                if (config.ExtensionRules.TryGetValue(ArchiveDatLineOutputFormResolver.Ext(f.RomName), out var rule)
                    && !rule.IsDiscard)
                    return rule.OutputExtension.Length > 0 ? rule.OutputExtension : Path.GetExtension(f.RomName);
            }
            return "";
        }
        return config.SingleFileOutputExtension;
    }

    /// <summary>Planned inner filenames for a bundle release folder.</summary>
    private static IReadOnlyList<string> PlannedInnerFilenames(ArchiveOutputConfig config, ArchiveReleaseInput r)
    {
        if (config.StrategyType == "file_extension")
        {
            // One derived file per non-discarded source file; name = source base + mapped ext.
            var names = new List<string>();
            foreach (var f in r.Files)
            {
                if (config.ExtensionRules.TryGetValue(ArchiveDatLineOutputFormResolver.Ext(f.RomName), out var rule)
                    && !rule.IsDiscard)
                {
                    names.Add(rule.OutputExtension.Length > 0
                        ? Path.GetFileNameWithoutExtension(f.RomName) + rule.OutputExtension
                        : f.RomName);
                }
            }
            return names;
        }
        // No Compression Folder (and similar): the bundle preserves original source names.
        return r.Files.Select(f => f.RomName).ToList();
    }

    /// <summary>Best-effort main input file + content identity key for the candidate.</summary>
    private static (string MainInput, string? Cik) ResolveMainAndCik(ArchiveOutputConfig config, ArchiveReleaseInput r)
    {
        if (config.StrategyType == "release_shape")
        {
            // Main input = the .cue (multi-track) or the single .iso; identity is release-level.
            var cue = r.Files.FirstOrDefault(f =>
                Path.GetExtension(f.RomName).Equals(".cue", StringComparison.OrdinalIgnoreCase));
            var iso = r.Files.FirstOrDefault(f =>
                Path.GetExtension(f.RomName).Equals(".iso", StringComparison.OrdinalIgnoreCase));
            var main = cue?.RomName ?? iso?.RomName ?? (r.Files.Count > 0 ? r.Files[0].RomName : "");
            return (main, $"release:{r.ReleaseId}");
        }

        if (config.StrategyType == "release_folder")
            return ("", $"release:{r.ReleaseId}");

        // file_extension: the single non-discarded file drives identity (best-effort).
        foreach (var f in r.Files)
        {
            if (config.ExtensionRules.TryGetValue(ArchiveDatLineOutputFormResolver.Ext(f.RomName), out var rule)
                && !rule.IsDiscard)
            {
                var cik = f.Sha1.Length > 0 ? $"sha1:{f.Sha1}"
                        : f.Md5.Length  > 0 ? $"md5:{f.Md5}"
                        : null;
                return (f.RomName, cik);
            }
        }
        return ("", null);
    }
}

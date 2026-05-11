using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Arkadia;

public sealed class ArkRestorePlanService
{
    public ArkRestorePlan PlanRestore(string arkFilePath, string targetDataDir)
    {
        if (string.IsNullOrWhiteSpace(arkFilePath))
            throw new ArgumentException("ARK file path must not be empty.", nameof(arkFilePath));
        if (string.IsNullOrWhiteSpace(targetDataDir))
            throw new ArgumentException("Target data directory must not be empty.", nameof(targetDataDir));

        var warnings = new List<string>();
        var issues   = new List<string>();
        var entries  = new List<ArkRestorePlanEntry>();

        // ── Always-present warnings ───────────────────────────────────────────

        warnings.Add(
            "After restore, run Verify ALL / Verify Volume before trusting restored archive state.");
        warnings.Add(
            "ARK v0.5 restore is full replacement only. Merge restore is not supported.");

        // ── Target directory analysis ─────────────────────────────────────────

        bool targetExists     = Directory.Exists(targetDataDir);
        bool targetIsEmpty    = !targetExists
                                || !Directory.EnumerateFileSystemEntries(targetDataDir).Any();
        bool requiresOverwrite = targetExists && !targetIsEmpty;

        if (requiresOverwrite)
            warnings.Add(
                "Target data directory is not empty. ARK v0.5 restore is full replacement only " +
                "and would require explicit overwrite confirmation.");

        // ── Verification ──────────────────────────────────────────────────────

        var  verification = new ArkPackageVerifierService().Verify(arkFilePath);
        bool packageValid = !verification.HasErrors;

        if (!packageValid)
        {
            issues.Add("ARK package verification failed. Restore is blocked.");
            return Build("", "", false, arkFilePath, targetDataDir,
                         targetExists, targetIsEmpty, requiresOverwrite,
                         entries, warnings, issues);
        }

        // ── Open ZIP and read manifest ────────────────────────────────────────

        using var zip = ZipFile.OpenRead(arkFilePath);

        string formatName    = "";
        string formatVersion = "";

        var manifestEntry = zip.GetEntry("manifest.json");
        if (manifestEntry is not null)
        {
            try
            {
                using var s   = manifestEntry.Open();
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                if (root.TryGetProperty("FormatName",    out var fn)) formatName    = fn.GetString() ?? "";
                if (root.TryGetProperty("FormatVersion", out var fv)) formatVersion = fv.GetString() ?? "";
            }
            catch { /* verifier already validated JSON; ignore unexpected failures */ }
        }

        // ── Version policy ────────────────────────────────────────────────────

        if (!string.Equals(formatName, "Arkadia Backup", StringComparison.Ordinal))
        {
            issues.Add(
                $"Package FormatName is '{formatName}'; expected 'Arkadia Backup'. Restore is blocked.");
            return Build(formatName, formatVersion, true, arkFilePath, targetDataDir,
                         targetExists, targetIsEmpty, requiresOverwrite,
                         entries, warnings, issues);
        }

        if (!string.Equals(formatVersion, "0.5", StringComparison.Ordinal))
        {
            issues.Add(
                $"Package FormatVersion is '{formatVersion}'; expected '0.5'. " +
                "No migrator exists for this version. Restore is blocked.");
            return Build(formatName, formatVersion, true, arkFilePath, targetDataDir,
                         targetExists, targetIsEmpty, requiresOverwrite,
                         entries, warnings, issues);
        }

        // ── Absolute path warning ─────────────────────────────────────────────

        warnings.Add(
            "Restored databases may contain absolute paths in volume_locations and " +
            "release_media_curation. Run Verify ALL / Verify Volume after restore and " +
            "review path relocation needs.");

        // ── Entry planning ────────────────────────────────────────────────────

        foreach (var entry in zip.Entries)
        {
            var p = entry.FullName;

            if (p == "manifest.json" || p == "hashes/files.sha256.json") continue;

            if (!IsPathSafe(p, out var pathIssue))
            {
                issues.Add(pathIssue);
                entries.Add(new ArkRestorePlanEntry(p, "", entry.Length, "other", false));
                continue;
            }

            var (targetPath, category, willRestore) = MapEntry(p, targetDataDir);

            if (category == "other")
                warnings.Add($"Unknown archive entry will not be restored: '{p}'.");

            entries.Add(new ArkRestorePlanEntry(
                ArchivePath: p,
                TargetPath:  targetPath,
                SizeBytes:   entry.Length,
                Category:    category,
                WillRestore: willRestore));
        }

        return Build(formatName, formatVersion, true, arkFilePath, targetDataDir,
                     targetExists, targetIsEmpty, requiresOverwrite,
                     entries, warnings, issues);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArkRestorePlan Build(
        string formatName, string formatVersion, bool packageValid,
        string arkFilePath, string targetDataDir,
        bool targetExists, bool targetIsEmpty, bool requiresOverwrite,
        List<ArkRestorePlanEntry> entries,
        List<string> warnings, List<string> issues)
        => new(
            ArkFilePath:       arkFilePath,
            TargetDataDir:     targetDataDir,
            FormatName:        formatName,
            FormatVersion:     formatVersion,
            PackageValid:      packageValid,
            TargetExists:      targetExists,
            TargetIsEmpty:     targetIsEmpty,
            RequiresOverwrite: requiresOverwrite,
            StoreCount:        entries.Count(e => e.Category is "catalog" or "datline"),
            DatLineDbCount:    entries.Count(e => e.Category == "datline"),
            TotalRestoreBytes: entries.Where(e => e.WillRestore).Sum(e => e.SizeBytes),
            Entries:           entries,
            Warnings:          warnings,
            Issues:            issues);

    private static bool IsPathSafe(string p, out string issue)
    {
        if (p.Contains('\\'))
        {
            issue = $"Unsafe archive entry path (backslash): '{p}'"; return false;
        }
        if (p.StartsWith('/'))
        {
            issue = $"Unsafe archive entry path (absolute): '{p}'"; return false;
        }
        var segments = p.Split('/');
        if (Array.Exists(segments, s => s == ".."))
        {
            issue = $"Unsafe archive entry path (traversal): '{p}'"; return false;
        }
        if (Array.Exists(segments, s => s.Length == 0))
        {
            issue = $"Unsafe archive entry path (empty segment): '{p}'"; return false;
        }
        issue = "";
        return true;
    }

    private static (string targetPath, string category, bool willRestore) MapEntry(
        string archivePath, string targetDataDir)
    {
        if (archivePath == "db/catalog.db")
            return (Path.Combine(targetDataDir, "catalog.db"), "catalog", true);

        if (archivePath.StartsWith("db/systems/", StringComparison.Ordinal) &&
            archivePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            var rel = archivePath["db/".Length..];
            return (Path.Combine(targetDataDir, rel.Replace('/', Path.DirectorySeparatorChar)),
                    "datline", true);
        }

        if (archivePath == "registry/amp-packages.json")
            return (Path.Combine(targetDataDir, "ark-restore", "amp-packages.json"),
                    "registry", true);

        if (archivePath.StartsWith("media/", StringComparison.Ordinal))
            return (Path.Combine(targetDataDir,
                                 archivePath.Replace('/', Path.DirectorySeparatorChar)),
                    "media", true);

        return (Path.Combine(targetDataDir, "ark-restore",
                             archivePath.Replace('/', Path.DirectorySeparatorChar)),
                "other", false);
    }
}

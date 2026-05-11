using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Arkadia.Data;

namespace Arkadia;

public sealed class ArkPackageVerifierService
{
    public ArkPackageVerificationResult Verify(string arkFilePath)
    {
        if (string.IsNullOrWhiteSpace(arkFilePath))
            throw new ArgumentException("File path must not be empty.", nameof(arkFilePath));

        var issues = new List<ArkPackageVerificationIssue>();

        void Add(ArkPackageVerificationSeverity s, string area, string msg)
            => issues.Add(new ArkPackageVerificationIssue(s, area, msg));

        var fileName = Path.GetFileName(arkFilePath);

        // ── 1. File exists ────────────────────────────────────────────────────

        if (!File.Exists(arkFilePath))
        {
            Add(ArkPackageVerificationSeverity.Error, "File",
                $"Package file not found: {arkFilePath}");
            return Empty(arkFilePath, fileName, false, false, issues);
        }

        // ── 2. ZIP readable ───────────────────────────────────────────────────

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(arkFilePath); }
        catch (Exception ex)
        {
            Add(ArkPackageVerificationSeverity.Error, "File",
                $"Cannot open ZIP: {ex.Message}");
            return Empty(arkFilePath, fileName, true, false, issues);
        }

        using (zip)
        {
            var entrySet   = new HashSet<string>(StringComparer.Ordinal);
            var entrySizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                entrySet.Add(e.FullName);
                entrySizes[e.FullName] = e.Length;
            }

            // ── 3. Path safety ────────────────────────────────────────────────

            var reportedPathIssues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                var p = e.FullName;
                if (reportedPathIssues.Contains(p)) continue;

                if (p.Contains('\\'))
                {
                    Add(ArkPackageVerificationSeverity.Error, "Paths",
                        $"Entry path contains backslash: '{p}'");
                    reportedPathIssues.Add(p);
                    continue;
                }
                if (p.StartsWith('/'))
                {
                    Add(ArkPackageVerificationSeverity.Error, "Paths",
                        $"Entry path is absolute: '{p}'");
                    reportedPathIssues.Add(p);
                    continue;
                }
                var segments = p.Split('/');
                if (segments.Any(s => s == ".."))
                {
                    Add(ArkPackageVerificationSeverity.Error, "Paths",
                        $"Entry path contains traversal segment: '{p}'");
                    reportedPathIssues.Add(p);
                    continue;
                }
                if (segments.Any(s => s.Length == 0))
                {
                    Add(ArkPackageVerificationSeverity.Error, "Paths",
                        $"Entry path contains empty segment: '{p}'");
                    reportedPathIssues.Add(p);
                }
            }

            // ── 4. Duplicate entries ──────────────────────────────────────────

            foreach (var dup in zip.Entries
                .GroupBy(e => e.FullName, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key))
            {
                Add(ArkPackageVerificationSeverity.Error, "Paths",
                    $"Duplicate ZIP entry: '{dup}'");
            }

            // ── 5. Required files ─────────────────────────────────────────────

            bool manifestPresent  = entrySet.Contains("manifest.json");
            bool hashFilePresent  = entrySet.Contains("hashes/files.sha256.json");
            bool catalogDbPresent = entrySet.Contains("db/catalog.db");

            if (!manifestPresent)
                Add(ArkPackageVerificationSeverity.Error, "Manifest",
                    "manifest.json is missing from the package.");
            if (!hashFilePresent)
                Add(ArkPackageVerificationSeverity.Error, "Hashes",
                    "hashes/files.sha256.json is missing from the package.");
            if (!catalogDbPresent)
                Add(ArkPackageVerificationSeverity.Error, "Catalog",
                    "db/catalog.db is missing from the package.");

            // ── 6. Manifest validation ────────────────────────────────────────

            bool manifestValid = false;
            if (manifestPresent)
            {
                try
                {
                    using var s   = zip.GetEntry("manifest.json")!.Open();
                    using var doc = JsonDocument.Parse(s);
                    var root = doc.RootElement;
                    manifestValid = true;

                    if (root.TryGetProperty("FormatName", out var formatName))
                    {
                        if (!string.Equals(formatName.GetString(), "Arkadia Backup", StringComparison.Ordinal))
                            Add(ArkPackageVerificationSeverity.Error, "Manifest",
                                $"FormatName is '{formatName.GetString()}'; expected 'Arkadia Backup'.");
                    }
                    else
                    {
                        Add(ArkPackageVerificationSeverity.Error, "Manifest",
                            "manifest.json is missing required field 'FormatName'.");
                    }

                    if (root.TryGetProperty("FormatVersion", out var formatVersion))
                    {
                        if (!string.Equals(formatVersion.GetString(), "0.5", StringComparison.Ordinal))
                            Add(ArkPackageVerificationSeverity.Warning, "Manifest",
                                $"FormatVersion is '{formatVersion.GetString()}'; expected '0.5'.");
                    }
                    else
                    {
                        Add(ArkPackageVerificationSeverity.Warning, "Manifest",
                            "manifest.json is missing field 'FormatVersion'.");
                    }

                    if (root.TryGetProperty("HashAlgorithm", out var hashAlgorithm))
                    {
                        if (!string.Equals(hashAlgorithm.GetString(), "SHA-256", StringComparison.Ordinal))
                            Add(ArkPackageVerificationSeverity.Error, "Manifest",
                                $"HashAlgorithm is '{hashAlgorithm.GetString()}'; expected 'SHA-256'.");
                    }
                    else
                    {
                        Add(ArkPackageVerificationSeverity.Error, "Manifest",
                            "manifest.json is missing required field 'HashAlgorithm'.");
                    }

                    if (root.TryGetProperty("CredentialsExcluded", out var credEx) &&
                        credEx.ValueKind == JsonValueKind.False)
                        Add(ArkPackageVerificationSeverity.Warning, "Manifest",
                            "CredentialsExcluded is false; package may contain credentials.");

                    if (root.TryGetProperty("CachePackagesExcluded", out var cacheEx) &&
                        cacheEx.ValueKind == JsonValueKind.False)
                        Add(ArkPackageVerificationSeverity.Warning, "Manifest",
                            "CachePackagesExcluded is false; package may contain cached data.");
                }
                catch
                {
                    manifestValid = false;
                    Add(ArkPackageVerificationSeverity.Error, "Manifest",
                        "manifest.json is not valid JSON.");
                }
            }

            // ── 7. Hash manifest validation + SHA verification ────────────────

            bool hashFileValid    = false;
            int  hashFileCount    = 0;
            int  sha256Mismatches = 0;
            var  hashEntryPaths   = new HashSet<string>(StringComparer.Ordinal);

            if (hashFilePresent)
            {
                try
                {
                    List<ArkFileHashEntry> hashEntries;
                    using (var s = zip.GetEntry("hashes/files.sha256.json")!.Open())
                        hashEntries = JsonSerializer.Deserialize<List<ArkFileHashEntry>>(
                            s, new JsonSerializerOptions())!;

                    hashFileValid = true;

                    foreach (var entry in hashEntries)
                    {
                        hashFileCount++;
                        hashEntryPaths.Add(entry.Path);

                        if (!entrySet.Contains(entry.Path))
                        {
                            Add(ArkPackageVerificationSeverity.Warning, "Hashes",
                                $"Hash entry references missing ZIP entry: '{entry.Path}'.");
                            continue;
                        }

                        try
                        {
                            string computed;
                            using (var s = zip.GetEntry(entry.Path)!.Open())
                                computed = HashStream(s);

                            if (!string.Equals(computed, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                sha256Mismatches++;
                                Add(ArkPackageVerificationSeverity.Error, "Hashes",
                                    $"SHA-256 mismatch for '{entry.Path}': " +
                                    $"recorded={entry.Sha256[..Math.Min(8, entry.Sha256.Length)]}…, " +
                                    $"computed={computed[..Math.Min(8, computed.Length)]}…");
                            }
                        }
                        catch (Exception ex)
                        {
                            Add(ArkPackageVerificationSeverity.Error, "Hashes",
                                $"Could not hash ZIP entry '{entry.Path}': {ex.Message}");
                        }
                    }
                }
                catch
                {
                    Add(ArkPackageVerificationSeverity.Error, "Hashes",
                        "hashes/files.sha256.json is not valid JSON.");
                }
            }

            // ── 8. Untracked ZIP entries ──────────────────────────────────────

            int untrackedEntries = 0;
            if (hashFileValid)
            {
                foreach (var e in zip.Entries)
                {
                    if (e.FullName == "hashes/files.sha256.json") continue;
                    if (!hashEntryPaths.Contains(e.FullName))
                    {
                        untrackedEntries++;
                        Add(ArkPackageVerificationSeverity.Warning, "Hashes",
                            $"ZIP entry is not listed in hash manifest: '{e.FullName}'.");
                    }
                }
            }

            // ── 9. DB count ───────────────────────────────────────────────────

            int datLineDbCount = zip.Entries
                .Count(e => e.FullName.StartsWith("db/", StringComparison.Ordinal)
                         && e.FullName.EndsWith(".db",   StringComparison.OrdinalIgnoreCase));

            // ── 10. Sidecar ───────────────────────────────────────────────────

            bool sidecarPresent = false;
            bool sidecarValid   = false;
            var  sidecarPath    = arkFilePath + ".sha256";

            if (!File.Exists(sidecarPath))
            {
                Add(ArkPackageVerificationSeverity.Warning, "Sidecar",
                    "Sidecar file (.ark.sha256) is missing.");
            }
            else
            {
                sidecarPresent = true;
                try
                {
                    var content = File.ReadAllText(sidecarPath).Trim();
                    var parts   = content.Split("  ", 2);
                    if (parts.Length != 2 || parts[0].Length != 64)
                    {
                        Add(ArkPackageVerificationSeverity.Error, "Sidecar",
                            "Sidecar file format is invalid; expected '{sha256}  {filename}'.");
                    }
                    else
                    {
                        var recordedHash = parts[0];
                        var computedHash = ReleaseMediaCurationService.ComputeSha256(arkFilePath);
                        if (computedHash is null)
                        {
                            Add(ArkPackageVerificationSeverity.Error, "Sidecar",
                                "Could not compute SHA-256 of the package file.");
                        }
                        else if (!string.Equals(recordedHash, computedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            Add(ArkPackageVerificationSeverity.Error, "Sidecar",
                                "Sidecar SHA-256 does not match the package file.");
                        }
                        else
                        {
                            sidecarValid = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Add(ArkPackageVerificationSeverity.Error, "Sidecar",
                        $"Could not read sidecar file: {ex.Message}");
                }
            }

            return new ArkPackageVerificationResult(
                ArkFilePath:      arkFilePath,
                FileName:         fileName,
                FileExists:       true,
                ZipReadable:      true,
                ManifestPresent:  manifestPresent,
                ManifestValid:    manifestValid,
                HashFilePresent:  hashFilePresent,
                HashFileValid:    hashFileValid,
                CatalogDbPresent: catalogDbPresent,
                DatLineDbCount:   datLineDbCount,
                HashFileCount:    hashFileCount,
                Sha256Mismatches: sha256Mismatches,
                UntrackedEntries: untrackedEntries,
                SidecarPresent:   sidecarPresent,
                SidecarValid:     sidecarValid,
                Issues:           issues);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static ArkPackageVerificationResult Empty(
        string arkFilePath, string fileName,
        bool fileExists, bool zipReadable,
        List<ArkPackageVerificationIssue> issues)
        => new(arkFilePath, fileName, fileExists, zipReadable,
               false, false, false, false, false,
               0, 0, 0, 0, false, false, issues);

    private static string HashStream(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}

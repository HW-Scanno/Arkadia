using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arkadia;

public sealed class AmpPackageVerifierService
{
    private static readonly string[] KnownRootDirs    = ["media", "curation", "hashes"];
    private static readonly string[] KnownRootFiles   = ["manifest.json", "releases.json"];
    private static readonly string[] RequiredHashFiles =
        ["manifest.json", "releases.json", "curation/exclusions.json", "curation/notes.json"];

    // Tokens whose presence in any JSON entry is a hard Error
    private static readonly string[] ForbiddenErrorTokens =
        ["\"ssuser\"", "devid=", "devpassword=", "ssid=", "sspassword=",
         "\\u0026devid", "\\u0026ssid"];

    // Tokens whose presence is a Warning
    private static readonly string[] ForbiddenWarningTokens =
        ["screenscraper", "scrapedAtUtc", "release_provider_payloads",
         "release_metadata_proposals", "release_metadata_field_state"];

    public AmpPackageVerificationResult Verify(string ampFilePath)
    {
        if (string.IsNullOrWhiteSpace(ampFilePath))
            throw new ArgumentException("File path must not be empty.", nameof(ampFilePath));

        var issues = new List<AmpPackageVerificationIssue>();

        void Add(AmpPackageVerificationSeverity s, string area, string msg)
            => issues.Add(new AmpPackageVerificationIssue(s, area, msg));

        var fileName = Path.GetFileName(ampFilePath);

        // ── 1. File exists ────────────────────────────────────────────────────
        if (!File.Exists(ampFilePath))
        {
            Add(AmpPackageVerificationSeverity.Error, "File",
                $"Package file not found: {ampFilePath}");
            return Empty(ampFilePath, fileName, false, false, issues);
        }

        // ── 2. ZIP readable ───────────────────────────────────────────────────
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(ampFilePath); }
        catch (Exception ex)
        {
            Add(AmpPackageVerificationSeverity.Error, "File",
                $"Cannot open ZIP: {ex.Message}");
            return Empty(ampFilePath, fileName, true, false, issues);
        }

        using (zip)
        {
            // Build entry lookup
            var entrySet   = new HashSet<string>(StringComparer.Ordinal);
            var entrySizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                entrySet.Add(e.FullName);
                entrySizes[e.FullName] = e.Length;
            }

            // ── 3-7. Required entries ─────────────────────────────────────────
            bool manifestPresent  = entrySet.Contains("manifest.json");
            bool releasesPresent  = entrySet.Contains("releases.json");
            bool hashFilePresent  = entrySet.Contains("hashes/files.sha256.json");
            bool exclusionsPresent = entrySet.Contains("curation/exclusions.json");
            bool notesPresent     = entrySet.Contains("curation/notes.json");

            if (!manifestPresent)
                Add(AmpPackageVerificationSeverity.Error, "Manifest",
                    "manifest.json is missing from the ZIP.");
            if (!releasesPresent)
                Add(AmpPackageVerificationSeverity.Error, "Releases",
                    "releases.json is missing from the ZIP.");
            if (!hashFilePresent)
                Add(AmpPackageVerificationSeverity.Error, "Hashes",
                    "hashes/files.sha256.json is missing from the ZIP.");
            if (!exclusionsPresent)
                Add(AmpPackageVerificationSeverity.Warning, "Exclusions",
                    "curation/exclusions.json is missing from the ZIP.");
            if (!notesPresent)
                Add(AmpPackageVerificationSeverity.Warning, "Notes",
                    "curation/notes.json is missing from the ZIP.");

            // ── 8-13. Archive path safety ─────────────────────────────────────
            var reportedPathIssues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                var p = e.FullName;
                if (reportedPathIssues.Contains(p)) continue;

                if (p.Contains('\\'))
                {
                    Add(AmpPackageVerificationSeverity.Error, "Paths",
                        $"Entry path contains backslash: '{p}'");
                    reportedPathIssues.Add(p);
                    continue;
                }

                if (p.StartsWith('/'))
                {
                    Add(AmpPackageVerificationSeverity.Error, "Paths",
                        $"Entry path is absolute: '{p}'");
                    reportedPathIssues.Add(p);
                    continue;
                }

                var segments = p.Split('/');
                if (segments.Any(s => s == ".."))
                {
                    Add(AmpPackageVerificationSeverity.Error, "Paths",
                        $"Entry path contains traversal segment: '{p}'");
                    reportedPathIssues.Add(p);
                    continue;
                }

                if (segments.Any(s => s.Length == 0))
                {
                    Add(AmpPackageVerificationSeverity.Error, "Paths",
                        $"Entry path contains empty segment: '{p}'");
                    reportedPathIssues.Add(p);
                    continue;
                }

                // Unexpected root directory
                if (segments.Length > 1)
                {
                    var rootDir = segments[0];
                    if (!KnownRootDirs.Contains(rootDir, StringComparer.Ordinal))
                        Add(AmpPackageVerificationSeverity.Warning, "Paths",
                            $"Entry is in unexpected root directory '{rootDir}/': '{p}'");
                }
                else
                {
                    // Root-level file not in known list
                    if (!KnownRootFiles.Contains(p, StringComparer.Ordinal))
                        Add(AmpPackageVerificationSeverity.Warning, "Paths",
                            $"Unexpected root-level entry: '{p}'");
                }
            }

            // Duplicate ZIP entry names
            var dupPaths = zip.Entries
                .GroupBy(e => e.FullName, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            foreach (var dup in dupPaths)
                Add(AmpPackageVerificationSeverity.Error, "Paths",
                    $"Duplicate ZIP entry: '{dup}'");

            // ── 14-18. JSON parsing ───────────────────────────────────────────
            JsonDocument? manifestDoc   = null;
            JsonDocument? releasesDoc   = null;
            JsonDocument? hashFileDoc   = null;

            bool manifestValid  = false;
            bool releasesValid  = false;
            bool hashFileValid  = false;

            if (manifestPresent)
            {
                try
                {
                    using var s = zip.GetEntry("manifest.json")!.Open();
                    manifestDoc   = JsonDocument.Parse(s);
                    manifestValid = true;
                }
                catch
                {
                    Add(AmpPackageVerificationSeverity.Error, "Manifest",
                        "manifest.json is not valid JSON.");
                }
            }

            if (releasesPresent)
            {
                try
                {
                    using var s = zip.GetEntry("releases.json")!.Open();
                    releasesDoc   = JsonDocument.Parse(s);
                    releasesValid = true;
                }
                catch
                {
                    Add(AmpPackageVerificationSeverity.Error, "Releases",
                        "releases.json is not valid JSON.");
                }
            }

            if (hashFilePresent)
            {
                try
                {
                    using var s = zip.GetEntry("hashes/files.sha256.json")!.Open();
                    hashFileDoc   = JsonDocument.Parse(s);
                    hashFileValid = true;
                }
                catch
                {
                    Add(AmpPackageVerificationSeverity.Error, "Hashes",
                        "hashes/files.sha256.json is not valid JSON.");
                }
            }

            if (exclusionsPresent)
            {
                try
                {
                    using var s = zip.GetEntry("curation/exclusions.json")!.Open();
                    using var d = JsonDocument.Parse(s);
                }
                catch
                {
                    Add(AmpPackageVerificationSeverity.Warning, "Exclusions",
                        "curation/exclusions.json is not valid JSON.");
                }
            }

            if (notesPresent)
            {
                try
                {
                    using var s = zip.GetEntry("curation/notes.json")!.Open();
                    using var d = JsonDocument.Parse(s);
                }
                catch
                {
                    Add(AmpPackageVerificationSeverity.Warning, "Notes",
                        "curation/notes.json is not valid JSON.");
                }
            }

            // ── 19-21. Manifest field validation ──────────────────────────────
            int manifestReleaseCount   = 0;
            int manifestMediaFileCount = 0;

            if (manifestValid && manifestDoc is not null)
            {
                using (manifestDoc)
                {
                    var root = manifestDoc.RootElement;

                    foreach (var field in new[] { "FormatName", "FormatVersion", "HardwareFamilyId",
                                                  "DatLineId", "SystemName", "ReleaseCount", "MediaFileCount" })
                    {
                        if (!root.TryGetProperty(field, out _))
                            Add(AmpPackageVerificationSeverity.Error, "Manifest",
                                $"manifest.json is missing required field '{field}'.");
                    }

                    if (root.TryGetProperty("FormatName", out var formatName))
                    {
                        if (!string.Equals(formatName.GetString(), "Arkadia Media Pack", StringComparison.Ordinal))
                            Add(AmpPackageVerificationSeverity.Error, "Manifest",
                                $"manifest.json FormatName is '{formatName.GetString()}'; expected 'Arkadia Media Pack'.");
                    }

                    if (root.TryGetProperty("FormatVersion", out var formatVersion))
                    {
                        if (!string.Equals(formatVersion.GetString(), "1", StringComparison.Ordinal))
                            Add(AmpPackageVerificationSeverity.Warning, "Manifest",
                                $"manifest.json FormatVersion is '{formatVersion.GetString()}'; expected '1'.");
                    }

                    if (root.TryGetProperty("ReleaseCount", out var rc))
                        manifestReleaseCount = rc.ValueKind == JsonValueKind.Number
                            ? rc.GetInt32() : 0;
                    if (root.TryGetProperty("MediaFileCount", out var mfc))
                        manifestMediaFileCount = mfc.ValueKind == JsonValueKind.Number
                            ? mfc.GetInt32() : 0;

                    // Attribution block
                    if (root.TryGetProperty("Attribution", out var attr) &&
                        attr.ValueKind == JsonValueKind.Object)
                    {
                        if (!attr.TryGetProperty("Notice", out var notice) ||
                            notice.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(notice.GetString()))
                            Add(AmpPackageVerificationSeverity.Warning, "Manifest",
                                "manifest.json Attribution.Notice is missing or empty.");

                        if (!attr.TryGetProperty("GeneralCredits", out var credits) ||
                            credits.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(credits.GetString()))
                            Add(AmpPackageVerificationSeverity.Warning, "Manifest",
                                "manifest.json Attribution.GeneralCredits is missing or empty.");
                    }
                    else
                    {
                        Add(AmpPackageVerificationSeverity.Warning, "Manifest",
                            "manifest.json is missing the Attribution block.");
                    }
                }
                manifestDoc = null;
            }

            // ── 22-26. Releases validation ────────────────────────────────────
            int                      releasesReleaseCount   = 0;
            int                      releasesMediaFileCount = 0;
            int                      duplicateReleaseKeys   = 0;
            int                      duplicateArchivePaths  = 0;
            var                      releaseArchivePaths    = new List<string>();
            var                      releaseIds             = new List<string>();
            // Archive paths declared in releases.json (for media consistency checks)
            var                      declaredArchivePaths   = new HashSet<string>(StringComparer.Ordinal);

            if (releasesValid && releasesDoc is not null)
            {
                using (releasesDoc)
                {
                    var root = releasesDoc.RootElement;

                    if (root.ValueKind != JsonValueKind.Array)
                    {
                        Add(AmpPackageVerificationSeverity.Error, "Releases",
                            "releases.json root element must be a JSON array.");
                    }
                    else
                    {
                        foreach (var rel in root.EnumerateArray())
                        {
                            releasesReleaseCount++;

                            foreach (var field in new[] { "ReleaseId", "DatName" })
                            {
                                if (!rel.TryGetProperty(field, out _))
                                    Add(AmpPackageVerificationSeverity.Error, "Releases",
                                        $"Release entry #{releasesReleaseCount} is missing required field '{field}'.");
                            }

                            if (rel.TryGetProperty("ReleaseId", out var rid))
                                releaseIds.Add(rid.GetString() ?? "");

                            // Media entries
                            if (rel.TryGetProperty("Media", out var media) &&
                                media.ValueKind == JsonValueKind.Array)
                            {
                                int mediaIdx = 0;
                                foreach (var entry in media.EnumerateArray())
                                {
                                    mediaIdx++;
                                    releasesMediaFileCount++;

                                    foreach (var field in new[] { "MediaType", "ArchivePath",
                                                                  "FileName", "Sha256", "SizeBytes" })
                                    {
                                        if (!entry.TryGetProperty(field, out _))
                                            Add(AmpPackageVerificationSeverity.Error, "Releases",
                                                $"Media entry #{mediaIdx} in release #{releasesReleaseCount} " +
                                                $"is missing required field '{field}'.");
                                    }

                                    if (entry.TryGetProperty("ArchivePath", out var ap))
                                    {
                                        var apStr = ap.GetString() ?? "";
                                        releaseArchivePaths.Add(apStr);
                                        declaredArchivePaths.Add(apStr);
                                    }
                                }
                            }
                        }

                        // Duplicate ReleaseId
                        duplicateReleaseKeys = releaseIds
                            .GroupBy(x => x, StringComparer.Ordinal)
                            .Count(g => g.Count() > 1);
                        foreach (var g in releaseIds.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1))
                            Add(AmpPackageVerificationSeverity.Error, "Releases",
                                $"Duplicate ReleaseId: '{g.Key}'.");

                        // Duplicate ArchivePath
                        duplicateArchivePaths = releaseArchivePaths
                            .GroupBy(x => x, StringComparer.Ordinal)
                            .Count(g => g.Count() > 1);
                        foreach (var g in releaseArchivePaths.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1))
                            Add(AmpPackageVerificationSeverity.Error, "Releases",
                                $"Duplicate ArchivePath: '{g.Key}'.");
                    }
                }
                releasesDoc = null;
            }

            // ── 27-28. Count consistency (manifest vs releases) ───────────────
            if (manifestValid && releasesValid)
            {
                if (manifestReleaseCount != releasesReleaseCount)
                    Add(AmpPackageVerificationSeverity.Warning, "Consistency",
                        $"manifest.json ReleaseCount ({manifestReleaseCount}) does not match " +
                        $"actual release count ({releasesReleaseCount}) in releases.json.");

                if (manifestMediaFileCount != releasesMediaFileCount)
                    Add(AmpPackageVerificationSeverity.Warning, "Consistency",
                        $"manifest.json MediaFileCount ({manifestMediaFileCount}) does not match " +
                        $"actual media entry count ({releasesMediaFileCount}) in releases.json.");
            }

            // ── 29-34. Hash manifest validation ──────────────────────────────
            int  hashFileCount   = 0;
            var  hashEntryPaths  = new HashSet<string>(StringComparer.Ordinal);
            int  sha256Mismatches = 0;

            if (hashFileValid && hashFileDoc is not null)
            {
                using (hashFileDoc)
                {
                    var root = hashFileDoc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var entry in root.EnumerateArray())
                        {
                            idx++;
                            hashFileCount++;

                            bool hasPath      = entry.TryGetProperty("Path",      out var pathProp);
                            bool hasSha256    = entry.TryGetProperty("Sha256",    out var sha256Prop);
                            bool hasSizeBytes = entry.TryGetProperty("SizeBytes", out var sizeBytesProp);

                            if (!hasPath)
                                Add(AmpPackageVerificationSeverity.Error, "Hashes",
                                    $"Hash entry #{idx} is missing required field 'Path'.");
                            if (!hasSha256)
                                Add(AmpPackageVerificationSeverity.Error, "Hashes",
                                    $"Hash entry #{idx} is missing required field 'Sha256'.");
                            if (!hasSizeBytes)
                                Add(AmpPackageVerificationSeverity.Error, "Hashes",
                                    $"Hash entry #{idx} is missing required field 'SizeBytes'.");

                            if (!hasPath || !hasSha256) continue;

                            var entryPath  = pathProp.GetString()  ?? "";
                            var entrySha   = sha256Prop.GetString() ?? "";
                            hashEntryPaths.Add(entryPath);

                            // ZIP entry existence
                            if (!entrySet.Contains(entryPath))
                            {
                                Add(AmpPackageVerificationSeverity.Warning, "Hashes",
                                    $"Hash entry references missing ZIP entry: '{entryPath}'.");
                                continue;
                            }

                            // SHA-256 verification
                            try
                            {
                                string computed;
                                using (var s = zip.GetEntry(entryPath)!.Open())
                                    computed = HashStream(s);

                                if (!string.Equals(computed, entrySha, StringComparison.OrdinalIgnoreCase))
                                {
                                    sha256Mismatches++;
                                    Add(AmpPackageVerificationSeverity.Error, "Hashes",
                                        $"SHA-256 mismatch for '{entryPath}': " +
                                        $"recorded={entrySha[..Math.Min(8, entrySha.Length)]}…, " +
                                        $"computed={computed[..Math.Min(8, computed.Length)]}…");
                                }
                            }
                            catch (Exception ex)
                            {
                                Add(AmpPackageVerificationSeverity.Error, "Hashes",
                                    $"Could not hash ZIP entry '{entryPath}': {ex.Message}");
                            }

                            // SizeBytes check
                            if (hasSizeBytes && entrySizes.TryGetValue(entryPath, out var actualSize))
                            {
                                long recordedSize = sizeBytesProp.ValueKind == JsonValueKind.Number
                                    ? sizeBytesProp.GetInt64() : -1;
                                if (recordedSize != actualSize)
                                    Add(AmpPackageVerificationSeverity.Warning, "Hashes",
                                        $"SizeBytes mismatch for '{entryPath}': " +
                                        $"recorded={recordedSize}, actual={actualSize}.");
                            }
                        }
                    }
                }
                hashFileDoc = null;

                // Required JSON files must be in hash manifest
                foreach (var required in RequiredHashFiles)
                {
                    if (!hashEntryPaths.Contains(required))
                        Add(AmpPackageVerificationSeverity.Warning, "Hashes",
                            $"Required file '{required}' is not listed in hashes/files.sha256.json.");
                }
            }

            // ── 35-39. Media consistency ──────────────────────────────────────
            int mediaFilesFound   = 0;
            int mediaFilesMissing = 0;
            int zeroByteMedia     = 0;

            foreach (var ap in declaredArchivePaths)
            {
                if (!entrySet.Contains(ap))
                {
                    mediaFilesMissing++;
                    Add(AmpPackageVerificationSeverity.Warning, "Media",
                        $"Media file declared in releases.json is missing from ZIP: '{ap}'.");
                    continue;
                }

                var size = entrySizes[ap];
                if (size == 0)
                {
                    zeroByteMedia++;
                    Add(AmpPackageVerificationSeverity.Error, "Media",
                        $"Media file is zero bytes: '{ap}'.");
                    mediaFilesFound++;
                    continue;
                }

                mediaFilesFound++;

                // SHA-256 cross-check is done via hash manifest (checks 29-34).
                // Media in releases but not in hash file
                if (hashFileValid && !hashEntryPaths.Contains(ap))
                    Add(AmpPackageVerificationSeverity.Warning, "Media",
                        $"Media file '{ap}' is in releases.json but not listed in hashes/files.sha256.json.");
            }

            // Unreferenced media in ZIP (media/ prefix entries not in releases)
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("media/", StringComparison.Ordinal)) continue;
                if (!declaredArchivePaths.Contains(e.FullName))
                    Add(AmpPackageVerificationSeverity.Info, "Media",
                        $"ZIP contains media entry not referenced in releases.json: '{e.FullName}'.");
            }

            // ── 40. Forbidden content scan ────────────────────────────────────
            int forbiddenViolations = 0;
            var jsonEntries = new[] { "manifest.json", "releases.json",
                                      "curation/exclusions.json", "curation/notes.json",
                                      "hashes/files.sha256.json" };

            foreach (var entryName in jsonEntries)
            {
                if (!entrySet.Contains(entryName)) continue;

                string text;
                try
                {
                    using var s = zip.GetEntry(entryName)!.Open();
                    using var r = new StreamReader(s, Encoding.UTF8);
                    text = r.ReadToEnd();
                }
                catch { continue; }

                // Attribution.GeneralCredits contains approved provider names; strip it
                // from manifest.json before scanning so it does not produce false positives.
                var scanText = entryName == "manifest.json"
                    ? StripAttributionForScan(text)
                    : text;

                foreach (var token in ForbiddenErrorTokens)
                {
                    if (scanText.Contains(token, StringComparison.Ordinal))
                    {
                        forbiddenViolations++;
                        Add(AmpPackageVerificationSeverity.Error, "ForbiddenContent",
                            $"'{entryName}' contains forbidden token '{token}'.");
                    }
                }

                foreach (var token in ForbiddenWarningTokens)
                {
                    if (scanText.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        forbiddenViolations++;
                        Add(AmpPackageVerificationSeverity.Warning, "ForbiddenContent",
                            $"'{entryName}' contains provider-specific token '{token}'.");
                    }
                }
            }

            return new AmpPackageVerificationResult(
                AmpFilePath:                ampFilePath,
                FileName:                   fileName,
                FileExists:                 true,
                ZipReadable:                true,
                ManifestPresent:            manifestPresent,
                ManifestValid:              manifestValid,
                ReleasesPresent:            releasesPresent,
                ReleasesValid:              releasesValid,
                HashFilePresent:            hashFilePresent,
                HashFileValid:              hashFileValid,
                ManifestReleaseCount:       manifestReleaseCount,
                ManifestMediaFileCount:     manifestMediaFileCount,
                ReleasesReleaseCount:       releasesReleaseCount,
                ReleasesMediaFileCount:     releasesMediaFileCount,
                HashFileCount:              hashFileCount,
                MediaFilesFound:            mediaFilesFound,
                MediaFilesMissing:          mediaFilesMissing,
                ZeroByteMediaFiles:         zeroByteMedia,
                Sha256Mismatches:           sha256Mismatches,
                ForbiddenContentViolations: forbiddenViolations,
                DuplicateReleaseKeys:       duplicateReleaseKeys,
                DuplicateArchivePaths:      duplicateArchivePaths,
                Issues:                     issues);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static AmpPackageVerificationResult Empty(
        string ampFilePath, string fileName,
        bool fileExists, bool zipReadable,
        List<AmpPackageVerificationIssue> issues)
        => new(ampFilePath, fileName, fileExists, zipReadable,
               false, false, false, false, false, false,
               0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, issues);

    private static string HashStream(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string StripAttributionForScan(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            var sb = new StringBuilder();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("Attribution")) continue;
                sb.Append('"').Append(prop.Name).Append('"');
                sb.Append(':');
                sb.Append(prop.Value.GetRawText());
                sb.Append('\n');
            }
            return sb.ToString();
        }
        catch
        {
            return manifestJson;
        }
    }
}

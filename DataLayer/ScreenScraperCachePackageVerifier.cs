using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkadia.Providers;
using Microsoft.Data.Sqlite;

namespace Arkadia.Data;

// ── Severity ─────────────────────────────────────────────────────────────────

public enum CachePackageVerificationSeverity { Info, Warning, Error }

// ── Issue ─────────────────────────────────────────────────────────────────────

public sealed record CachePackageVerificationIssue(
    CachePackageVerificationSeverity Severity,
    string Area,
    string Message);

// ── Result ────────────────────────────────────────────────────────────────────

public sealed record CachePackageVerificationResult(
    int      PackageId,
    string   PackagePath,
    string   FileName,
    bool     FileExists,
    bool     ZipReadable,
    bool     ManifestPresent,
    bool     GamesListPresent,
    int      IndexedGameCount,
    int      PayloadsExpected,
    int      PayloadsFound,
    int      PayloadJsonValid,
    int      PayloadsMissing,
    int      IndexedMediaCount,
    int      MediaFilesFound,
    int      MediaFilesMissing,
    int      ZeroByteMediaFiles,
    int      SanitizationWarnings,
    int      SanitizationErrors,
    IReadOnlyList<CachePackageVerificationIssue> Issues)
{
    public bool   HasErrors   => Issues.Any(i => i.Severity == CachePackageVerificationSeverity.Error);
    public bool   HasWarnings => Issues.Any(i => i.Severity == CachePackageVerificationSeverity.Warning);
    public string Status      => HasErrors ? "Error" : HasWarnings ? "Warning" : "Valid";

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Package:  {FileName}");
        sb.AppendLine($"Path:     {PackagePath}");
        sb.AppendLine($"Status:   {Status}");
        sb.AppendLine();
        sb.AppendLine("Summary");
        sb.AppendLine($"  Games indexed:         {IndexedGameCount}");
        sb.AppendLine($"  Payloads expected:     {PayloadsExpected}");
        sb.AppendLine($"  Payloads found:        {PayloadsFound}");
        sb.AppendLine($"  Payload JSON valid:    {PayloadJsonValid}");
        sb.AppendLine($"  Payloads missing:      {PayloadsMissing}");
        sb.AppendLine($"  Media indexed:         {IndexedMediaCount}");
        sb.AppendLine($"  Media files found:     {MediaFilesFound}");
        sb.AppendLine($"  Media files missing:   {MediaFilesMissing}");
        sb.AppendLine($"  Zero-byte media:       {ZeroByteMediaFiles}");
        sb.AppendLine($"  Sanitization errors:   {SanitizationErrors}");
        sb.AppendLine($"  Sanitization warnings: {SanitizationWarnings}");

        static string Label(CachePackageVerificationSeverity s) => s switch
        {
            CachePackageVerificationSeverity.Error   => "Errors",
            CachePackageVerificationSeverity.Warning => "Warnings",
            _                                        => "Info",
        };

        foreach (var sev in new[]
        {
            CachePackageVerificationSeverity.Error,
            CachePackageVerificationSeverity.Warning,
            CachePackageVerificationSeverity.Info,
        })
        {
            var group = Issues.Where(i => i.Severity == sev).ToList();
            if (group.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine(Label(sev) + ":");
            foreach (var issue in group)
                sb.AppendLine($"  [{issue.Area}] {issue.Message}");
        }

        return sb.ToString();
    }
}

// ── Verifier ──────────────────────────────────────────────────────────────────

public sealed class ScreenScraperCachePackageVerifier(CatalogService catalog)
{
    // Detects credential query-params; group 1 = param name, group 2 = value
    private static readonly Regex CredentialRx = new(
        @"[?&](devid|devpassword|ssid|sspassword|softname)=([^&""\\]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Same check for the \u0026-escaped ampersand form produced by standard JsonSerializer
    private static readonly Regex EscapedCredentialRx = new(
        @"\\u0026(devid|devpassword|ssid|sspassword|softname)=([^&""\\]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> Placeholders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["devid"]       = "<DEVID>",
            ["devpassword"] = "<DEVPASSWORD>",
            ["ssid"]        = "<SSID>",
            ["sspassword"]  = "<SSPASSWORD>",
            ["softname"]    = "<SOFTNAME>",
        };

    private static readonly Regex CsvLineRx = new(
        @"^""(?<id>[^""]*)"";""(?<name>.*)""$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    // ── Public API ────────────────────────────────────────────────────────────

    public CachePackageVerificationResult Verify(int packageId)
    {
        var issues = new List<CachePackageVerificationIssue>();

        void Add(CachePackageVerificationSeverity s, string area, string msg)
            => issues.Add(new CachePackageVerificationIssue(s, area, msg));

        // ── 1. Load package row ───────────────────────────────────────────────
        var pkg = LoadPackageRow(packageId);
        if (pkg is null)
        {
            Add(CachePackageVerificationSeverity.Error, "Index",
                $"Package ID {packageId} not found in catalog.");
            return Empty(packageId, "", "", false, false, issues);
        }

        var (packagePath, indexedGameCount) = pkg.Value;
        var fileName = Path.GetFileName(packagePath);

        // ── 2. File exists ────────────────────────────────────────────────────
        if (!File.Exists(packagePath))
        {
            Add(CachePackageVerificationSeverity.Error, "File",
                $"Package file not found: {packagePath}");
            return Empty(packageId, packagePath, fileName, false, false, issues);
        }

        // ── 3. ZIP readable ───────────────────────────────────────────────────
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(packagePath); }
        catch (Exception ex)
        {
            Add(CachePackageVerificationSeverity.Error, "File",
                $"Cannot open ZIP: {ex.Message}");
            return Empty(packageId, packagePath, fileName, true, false, issues);
        }

        using (zip)
        {
            // Build entry lookup — O(1) existence and size checks
            var entrySet   = new HashSet<string>(StringComparer.Ordinal);
            var entrySizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                entrySet.Add(e.FullName);
                entrySizes[e.FullName] = e.Length;
            }

            // ── 4. Manifest ───────────────────────────────────────────────────
            bool manifestPresent = entrySet.Contains(ScreenScraperCachePackageLayout.ManifestEntry);
            if (!manifestPresent)
                Add(CachePackageVerificationSeverity.Error, "Manifest",
                    $"{ScreenScraperCachePackageLayout.ManifestEntry} is missing from the ZIP.");

            // ── 5. Gameslist ──────────────────────────────────────────────────
            bool gamesListPresent = entrySet.Contains(ScreenScraperCachePackageLayout.GamesListEntry);
            if (!gamesListPresent)
                Add(CachePackageVerificationSeverity.Error, "Gameslist",
                    $"{ScreenScraperCachePackageLayout.GamesListEntry} is missing from the ZIP.");

            // ── 6. Gameslist row count vs indexed count ───────────────────────
            if (gamesListPresent)
            {
                try
                {
                    int csvCount = CountGameslistRows(zip);
                    if (csvCount != indexedGameCount)
                        Add(CachePackageVerificationSeverity.Warning, "Gameslist",
                            $"gameslist.csv has {csvCount} valid rows but {indexedGameCount} games are indexed.");
                }
                catch (Exception ex)
                {
                    Add(CachePackageVerificationSeverity.Warning, "Gameslist",
                        $"Could not read gameslist.csv: {ex.Message}");
                }
            }

            // ── 7. game_count field vs actual game rows ───────────────────────
            var indexedGames = LoadIndexedGames(packageId);
            if (indexedGames.Count != indexedGameCount)
                Add(CachePackageVerificationSeverity.Warning, "Index",
                    $"cache_packages.game_count ({indexedGameCount}) differs from actual game rows ({indexedGames.Count}).");

            // ── 8. Payload checks ─────────────────────────────────────────────
            int payloadsExpected  = 0;
            int payloadsFound     = 0;
            int payloadJsonValid  = 0;
            int payloadsMissing   = 0;
            int sanitizationWarn  = 0;
            int sanitizationErr   = 0;

            foreach (var (gameId, hasPayload, payloadEntry) in indexedGames)
            {
                if (!hasPayload) continue;
                payloadsExpected++;

                var entryName = payloadEntry.Length > 0
                    ? payloadEntry
                    : ScreenScraperCachePackageLayout.PayloadEntry(gameId);

                if (!entrySet.Contains(entryName))
                {
                    payloadsMissing++;
                    Add(CachePackageVerificationSeverity.Warning, "Payload",
                        $"Missing payload for game {gameId} (expected: {entryName}).");
                    continue;
                }

                payloadsFound++;

                var payloadZipEntry = zip.GetEntry(entryName)!;

                // Zero-byte payload
                if (payloadZipEntry.Length == 0)
                {
                    Add(CachePackageVerificationSeverity.Error, "Payload",
                        $"Payload for game {gameId} is zero bytes ({entryName}).");
                    continue;
                }

                // Read payload text
                string payloadText;
                try
                {
                    using var stream = payloadZipEntry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    payloadText = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    Add(CachePackageVerificationSeverity.Error, "Payload",
                        $"Could not read payload for game {gameId}: {ex.Message}");
                    continue;
                }

                // Parse JSON
                JsonDocument? doc;
                try { doc = JsonDocument.Parse(payloadText); }
                catch
                {
                    Add(CachePackageVerificationSeverity.Error, "Payload",
                        $"Payload for game {gameId} is not valid JSON ({entryName}).");
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("response", out var response) ||
                        !response.TryGetProperty("jeu", out var jeu))
                    {
                        Add(CachePackageVerificationSeverity.Error, "Payload",
                            $"Payload for game {gameId} is missing response.jeu.");
                        continue;
                    }

                    payloadJsonValid++;

                    // ID match check
                    if (jeu.TryGetProperty("id", out var jeuId))
                    {
                        var actualId = jeuId.ValueKind == JsonValueKind.String
                            ? jeuId.GetString() ?? ""
                            : jeuId.GetRawText().Trim('"');
                        if (!string.Equals(actualId, gameId, StringComparison.Ordinal))
                            Add(CachePackageVerificationSeverity.Warning, "Payload",
                                $"Payload for game {gameId} has mismatched jeu.id: {actualId}.");
                    }
                }

                // Sanitization: ssuser
                if (payloadText.Contains("\"ssuser\"", StringComparison.Ordinal))
                {
                    sanitizationErr++;
                    Add(CachePackageVerificationSeverity.Error, "Sanitization",
                        $"Payload for game {gameId} contains response.ssuser (not sanitized).");
                }

                // Sanitization: credential params (literal & form)
                CheckCredentialParams(payloadText, gameId, CredentialRx,
                    ref sanitizationWarn, ref sanitizationErr, issues);

                // Sanitization: escaped \u0026 form
                CheckCredentialParams(payloadText, gameId, EscapedCredentialRx,
                    ref sanitizationWarn, ref sanitizationErr, issues);
            }

            // ── 9. Media checks ───────────────────────────────────────────────
            var indexedMedia  = LoadIndexedMedia(packageId);
            int mediaFound    = 0;
            int mediaMissing  = 0;
            int mediaZeroByte = 0;

            foreach (var zipEntry in indexedMedia)
            {
                if (!entrySet.Contains(zipEntry))
                {
                    mediaMissing++;
                    Add(CachePackageVerificationSeverity.Warning, "Media",
                        $"Indexed media entry missing from ZIP: {zipEntry}");
                    continue;
                }

                if (entrySizes[zipEntry] == 0)
                {
                    mediaZeroByte++;
                    Add(CachePackageVerificationSeverity.Error, "Media",
                        $"Media entry is zero bytes: {zipEntry}");
                    continue;
                }

                mediaFound++;
            }

            return new CachePackageVerificationResult(
                PackageId:           packageId,
                PackagePath:         packagePath,
                FileName:            fileName,
                FileExists:          true,
                ZipReadable:         true,
                ManifestPresent:     manifestPresent,
                GamesListPresent:    gamesListPresent,
                IndexedGameCount:    indexedGameCount,
                PayloadsExpected:    payloadsExpected,
                PayloadsFound:       payloadsFound,
                PayloadJsonValid:    payloadJsonValid,
                PayloadsMissing:     payloadsMissing,
                IndexedMediaCount:   indexedMedia.Count,
                MediaFilesFound:     mediaFound,
                MediaFilesMissing:   mediaMissing,
                ZeroByteMediaFiles:  mediaZeroByte,
                SanitizationWarnings: sanitizationWarn,
                SanitizationErrors:  sanitizationErr,
                Issues:              issues);
        }
    }

    // ── Private: credential check helper ─────────────────────────────────────

    private static void CheckCredentialParams(
        string text, string gameId, Regex rx,
        ref int warnCount, ref int errCount,
        List<CachePackageVerificationIssue> issues)
    {
        foreach (Match m in rx.Matches(text))
        {
            var paramName = m.Groups[1].Value;
            var value     = m.Groups[2].Value;

            if (!Placeholders.TryGetValue(paramName, out var expected)) continue;

            if (!string.Equals(value, expected, StringComparison.Ordinal))
            {
                errCount++;
                issues.Add(new CachePackageVerificationIssue(
                    CachePackageVerificationSeverity.Error, "Sanitization",
                    $"Payload for game {gameId} has unsanitized credential param '{paramName}'."));
            }
        }
    }

    // ── Private: early-exit result builder ───────────────────────────────────

    private static CachePackageVerificationResult Empty(
        int packageId, string packagePath, string fileName,
        bool fileExists, bool zipReadable,
        List<CachePackageVerificationIssue> issues)
        => new(packageId, packagePath, fileName,
               fileExists, zipReadable,
               false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, issues);

    // ── Private: DB helpers ───────────────────────────────────────────────────

    private (string Path, int GameCount)? LoadPackageRow(int packageId)
    {
        using var conn = OpenDb();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT package_path, game_count FROM cache_packages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", packageId);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        return (rdr.GetString(0), rdr.GetInt32(1));
    }

    private List<(string GameId, bool HasPayload, string PayloadEntry)> LoadIndexedGames(int packageId)
    {
        using var conn = OpenDb();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT provider_game_id, has_payload, payload_zip_entry
            FROM cache_package_games
            WHERE package_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", packageId);
        var result = new List<(string, bool, string)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            result.Add((rdr.GetString(0), rdr.GetInt32(1) != 0, rdr.GetString(2)));
        return result;
    }

    private List<string> LoadIndexedMedia(int packageId)
    {
        using var conn = OpenDb();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.zip_entry
            FROM cache_package_media m
            JOIN cache_package_games g ON g.id = m.game_row_id
            WHERE g.package_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", packageId);
        var result = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) result.Add(rdr.GetString(0));
        return result;
    }

    private static int CountGameslistRows(ZipArchive zip)
    {
        var entry = zip.GetEntry(ScreenScraperCachePackageLayout.GamesListEntry)!;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        int  count = 0;
        bool first = true;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            var m = CsvLineRx.Match(line);
            if (!m.Success) continue;
            var id   = m.Groups["id"].Value.Trim();
            var name = m.Groups["name"].Value.Trim();
            if (first)
            {
                first = false;
                if (string.Equals(id, "Game ID", StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) count++;
        }
        return count;
    }

    private SqliteConnection OpenDb()
    {
        var conn = new SqliteConnection($"Data Source={catalog.DbPath}");
        conn.Open();
        return conn;
    }
}

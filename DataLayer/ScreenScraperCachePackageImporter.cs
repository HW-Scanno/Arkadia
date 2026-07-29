using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arkadia.Providers;
using Microsoft.Data.Sqlite;

namespace Arkadia.Data;

public sealed record CachePackageIndexResult(
    int PackageId,
    int GameCount,
    int MediaCount,
    bool WasAlreadyIndexed);

/// <summary>
/// Indexes a ScreenScraper cache ZIP package into catalog.db without extracting any files.
/// </summary>
public sealed class ScreenScraperCachePackageImporter(CatalogService catalog)
{
    // Matches: <gameId>[_<region>]_<index>  (e.g. "39874_us_0", "39874_0")
    private static readonly Regex MediaFilenameRx = new(
        @"^(?<gameId>\d+)(?:_(?<region>[a-z]+))?_(?<index>\d+)$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    // Parses one line of the ScreenScraper semicolon CSV: "id";"name"
    private static readonly Regex CsvLineRx = new(
        @"^""(?<id>[^""]*)"";""(?<name>.*)""$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public CachePackageIndexResult IndexPackage(string zipPath)
    {
        zipPath = Path.GetFullPath(zipPath);

        using var conn = OpenCatalogDb();

        if (IsAlreadyIndexed(conn, zipPath))
            return new CachePackageIndexResult(0, 0, 0, WasAlreadyIndexed: true);

        using var zip = ZipFile.OpenRead(zipPath);

        var manifest = ReadManifest(zip);

        // ── Pass 1: collect payload presence ─────────────────────────────────
        var gameHasPayload    = new HashSet<string>(StringComparer.Ordinal);
        var gamePayloadEntry  = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(ScreenScraperCachePackageLayout.PayloadsPrefix, StringComparison.Ordinal)) continue;
            if (entry.FullName.EndsWith('/')) continue;
            var stem = Path.GetFileNameWithoutExtension(entry.FullName.AsSpan().Slice(ScreenScraperCachePackageLayout.PayloadsPrefix.Length).ToString());
            if (!string.IsNullOrEmpty(stem))
            {
                gameHasPayload.Add(stem);
                gamePayloadEntry[stem] = entry.FullName;
            }
        }

        // ── Pass 2: collect media rows ────────────────────────────────────────
        var gameHasMedia = new HashSet<string>(StringComparer.Ordinal);
        var allMedia     = new List<MediaRow>();

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(ScreenScraperCachePackageLayout.MediaPrefix, StringComparison.Ordinal)) continue;
            if (entry.FullName.EndsWith('/')) continue;

            var parts = entry.FullName.Split('/');
            if (parts.Length != 3) continue; // must be media/<type>/<file>

            var mediaType = parts[1];
            var fileName  = parts[2];
            var ext       = Path.GetExtension(fileName).TrimStart('.');
            var stem      = Path.GetFileNameWithoutExtension(fileName);

            var m = MediaFilenameRx.Match(stem);
            if (!m.Success) continue;

            var gameId = m.Groups["gameId"].Value;
            var region = m.Groups["region"].Success ? m.Groups["region"].Value : "";
            var indexN = int.Parse(m.Groups["index"].Value);

            allMedia.Add(new MediaRow(gameId, mediaType, region, indexN, entry.FullName, ext, entry.Length));
            gameHasMedia.Add(gameId);
        }

        // Group media by game for O(1) lookup during insert
        var mediaByGame = new Dictionary<string, List<MediaRow>>(StringComparer.Ordinal);
        foreach (var row in allMedia)
        {
            if (!mediaByGame.TryGetValue(row.GameId, out var list))
                mediaByGame[row.GameId] = list = [];
            list.Add(row);
        }

        // ── Parse games CSV ───────────────────────────────────────────────────
        var games = ReadGameslistCsv(zip);

        // ── Single transaction: insert everything ─────────────────────────────
        int packageId;
        int gameCount  = 0;
        int mediaCount = 0;

        var gameRows = new List<GameRowInfo>();

        using var tx = conn.BeginTransaction();

        packageId = InsertPackage(conn, zipPath, manifest);

        foreach (var (gameId, title) in games)
        {
            bool   hasPayload   = gameHasPayload.Contains(gameId);
            bool   hasMedia     = gameHasMedia.Contains(gameId);
            string payloadEntry = hasPayload ? gamePayloadEntry.GetValueOrDefault(gameId, "") : "";

            long gameRowId = InsertGame(conn, packageId, gameId, manifest.SystemId,
                title, hasPayload, hasMedia, payloadEntry);
            gameRows.Add(new GameRowInfo(gameRowId, title, hasPayload, payloadEntry));
            gameCount++;

            if (hasMedia && mediaByGame.TryGetValue(gameId, out var rows))
            {
                foreach (var r in rows)
                {
                    InsertMedia(conn, gameRowId, r);
                    mediaCount++;
                }
            }
        }

        tx.Commit();

        PopulateSearchTerms(conn, zip, gameRows);

        return new CachePackageIndexResult(packageId, gameCount, mediaCount, WasAlreadyIndexed: false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SqliteConnection OpenCatalogDb()
    {
        var conn = new SqliteConnection($"Data Source={catalog.DbPath}");
        conn.Open();
        return conn;
    }

    private static bool IsAlreadyIndexed(SqliteConnection conn, string zipPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cache_packages WHERE package_path = $path";
        cmd.Parameters.AddWithValue("$path", zipPath);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static ManifestData ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ScreenScraperCachePackageLayout.ManifestEntry)
            ?? throw new InvalidDataException($"Cache package is missing {ScreenScraperCachePackageLayout.ManifestEntry}.");

        using var stream = entry.Open();
        using var doc    = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        int version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) ? vi : 0;
        if (version != 1)
            throw new InvalidDataException($"Unsupported cache package version: {version}. Expected 1.");

        string provider = root.TryGetProperty("provider", out var pv) ? pv.GetString() ?? "" : "";
        if (!string.Equals(provider, ArkadiaProviders.ScreenScraper, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported provider '{provider}'. Expected '{ArkadiaProviders.ScreenScraper}'.");

        string cacheProviderId = root.TryGetProperty("cacheProviderId", out var cpv) ? cpv.GetString() ?? "" : "";
        if (!string.Equals(cacheProviderId, ArkadiaProviders.ScreenScraperCache, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported cacheProviderId '{cacheProviderId}'.");

        string systemId = "";
        if (root.TryGetProperty("systemId", out var sid))
            systemId = sid.ValueKind == JsonValueKind.Number ? sid.GetInt64().ToString() : sid.GetString() ?? "";
        if (string.IsNullOrEmpty(systemId))
            throw new InvalidDataException("manifest.json is missing required field 'systemId'.");

        string systemName = root.TryGetProperty("systemName", out var sn) ? sn.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(systemName))
            throw new InvalidDataException("manifest.json is missing required field 'systemName'.");

        string builtAtUtc  = root.TryGetProperty("builtAtUtc", out var bat) ? bat.GetString() ?? "" : "";
        int    gameCount   = root.TryGetProperty("gameCount",  out var gc)  && gc.TryGetInt32(out var gci) ? gci : 0;
        string manifestJson = doc.RootElement.GetRawText();

        return new ManifestData(provider, cacheProviderId, systemId, systemName, builtAtUtc, gameCount, manifestJson);
    }

    private static List<(string GameId, string Title)> ReadGameslistCsv(ZipArchive zip)
    {
        var result = new List<(string, string)>();
        var entry  = zip.GetEntry(ScreenScraperCachePackageLayout.GamesListEntry);
        if (entry is null) return result;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        bool first = true;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            // Skip header line (first data line or explicit "Game ID" header)
            if (first)
            {
                first = false;
                var m0 = CsvLineRx.Match(line);
                if (!m0.Success || string.Equals(m0.Groups["id"].Value, "Game ID", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Non-header first line — fall through to parse it
                var id0    = m0.Groups["id"].Value.Trim();
                var title0 = m0.Groups["name"].Value.Trim();
                if (!string.IsNullOrEmpty(id0) && !string.IsNullOrEmpty(title0))
                    result.Add((id0, title0));
                continue;
            }

            var m = CsvLineRx.Match(line);
            if (!m.Success) continue;

            var gameId = m.Groups["id"].Value.Trim();
            var title  = m.Groups["name"].Value.Trim();

            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(title)) continue;

            result.Add((gameId, title));
        }

        return result;
    }

    private static int InsertPackage(SqliteConnection conn, string zipPath, ManifestData manifest)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cache_packages(
                package_path, provider, cache_provider_id, system_id, system_name,
                game_count, built_at_utc, indexed_at_utc, manifest_json, status)
            VALUES(
                $path, $provider, $cacheProviderId, $systemId, $systemName,
                $gameCount, $builtAt, $indexedAt, $manifest, 'indexed')
            """;
        cmd.Parameters.AddWithValue("$path",            zipPath);
        cmd.Parameters.AddWithValue("$provider",        manifest.Provider);
        cmd.Parameters.AddWithValue("$cacheProviderId", manifest.CacheProviderId);
        cmd.Parameters.AddWithValue("$systemId",        manifest.SystemId);
        cmd.Parameters.AddWithValue("$systemName",      manifest.SystemName);
        cmd.Parameters.AddWithValue("$gameCount",       manifest.GameCount);
        cmd.Parameters.AddWithValue("$builtAt",         manifest.BuiltAtUtc);
        cmd.Parameters.AddWithValue("$indexedAt",       DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$manifest",        manifest.ManifestJson);
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt32(idCmd.ExecuteScalar());
    }

    private static long InsertGame(
        SqliteConnection conn, int packageId, string providerGameId, string systemId,
        string title, bool hasPayload, bool hasMedia, string payloadZipEntry)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cache_package_games(
                package_id, provider_game_id, system_id, title,
                has_payload, has_media, payload_zip_entry, scraped_at_utc)
            VALUES($pkgId, $gameId, $sysId, $title, $hasPayload, $hasMedia, $payloadEntry, '')
            """;
        cmd.Parameters.AddWithValue("$pkgId",        packageId);
        cmd.Parameters.AddWithValue("$gameId",       providerGameId);
        cmd.Parameters.AddWithValue("$sysId",        systemId);
        cmd.Parameters.AddWithValue("$title",        title);
        cmd.Parameters.AddWithValue("$hasPayload",   hasPayload ? 1 : 0);
        cmd.Parameters.AddWithValue("$hasMedia",     hasMedia ? 1 : 0);
        cmd.Parameters.AddWithValue("$payloadEntry", payloadZipEntry);
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? throw new InvalidOperationException("INSERT game returned no rowid."));
    }

    private static void InsertMedia(SqliteConnection conn, long gameRowId, MediaRow r)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cache_package_media(
                game_row_id, provider_game_id, media_type, region,
                index_n, zip_entry, file_ext, file_size)
            VALUES($rowId, $gameId, $mediaType, $region, $indexN, $zipEntry, $ext, $fileSize)
            """;
        cmd.Parameters.AddWithValue("$rowId",     gameRowId);
        cmd.Parameters.AddWithValue("$gameId",    r.GameId);
        cmd.Parameters.AddWithValue("$mediaType", r.MediaType);
        cmd.Parameters.AddWithValue("$region",    r.Region);
        cmd.Parameters.AddWithValue("$indexN",    r.IndexN);
        cmd.Parameters.AddWithValue("$zipEntry",  r.ZipEntry);
        cmd.Parameters.AddWithValue("$ext",       r.Ext);
        cmd.Parameters.AddWithValue("$fileSize",  r.FileSize);
        cmd.ExecuteNonQuery();
    }

    // ── Search term population ────────────────────────────────────────────────

    private static void PopulateSearchTerms(
        SqliteConnection conn, ZipArchive zip, List<GameRowInfo> gameRows)
    {
        using var tx = conn.BeginTransaction();

        foreach (var row in gameRows)
        {
            InsertSearchTerm(conn, row.RowId, row.Title, "title");

            if (!row.HasPayload || row.PayloadEntry.Length == 0) continue;

            var entry = zip.GetEntry(row.PayloadEntry);
            if (entry is null) continue;

            using var stream = entry.Open();
            using var doc    = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("response", out var response)) continue;
            if (!response.TryGetProperty("jeu", out var jeu)) continue;

            if (jeu.TryGetProperty("noms", out var noms) && noms.ValueKind == JsonValueKind.Array)
            {
                foreach (var nom in noms.EnumerateArray())
                {
                    if (nom.TryGetProperty("text", out var t) &&
                        t.GetString() is { Length: > 0 } name)
                        InsertSearchTerm(conn, row.RowId, name, "altname");
                }
            }

            if (jeu.TryGetProperty("roms", out var roms) && roms.ValueKind == JsonValueKind.Array)
            {
                foreach (var rom in roms.EnumerateArray())
                {
                    if (rom.TryGetProperty("romfilename", out var rf) &&
                        rf.GetString() is { Length: > 0 } filename)
                    {
                        InsertSearchTerm(conn, row.RowId, filename, "romfilename");
                        var stem = Path.GetFileNameWithoutExtension(filename);
                        if (stem.Length > 0 && !string.Equals(stem, filename, StringComparison.OrdinalIgnoreCase))
                            InsertSearchTerm(conn, row.RowId, stem, "romfilename");
                    }
                }
            }
        }

        tx.Commit();
    }

    private static void InsertSearchTerm(
        SqliteConnection conn, long gameRowId, string term, string termType)
    {
        var normalized = term.Trim().ToLowerInvariant();
        if (normalized.Length == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO cache_package_search_terms(package_game_id, term, term_type, normalized_term)
            VALUES($rowId, $term, $termType, $normalized)
            """;
        cmd.Parameters.AddWithValue("$rowId",      gameRowId);
        cmd.Parameters.AddWithValue("$term",       term);
        cmd.Parameters.AddWithValue("$termType",   termType);
        cmd.Parameters.AddWithValue("$normalized", normalized);
        cmd.ExecuteNonQuery();
    }

    private sealed record GameRowInfo(long RowId, string Title, bool HasPayload, string PayloadEntry);

    private sealed record ManifestData(
        string Provider,
        string CacheProviderId,
        string SystemId,
        string SystemName,
        string BuiltAtUtc,
        int    GameCount,
        string ManifestJson);

    private sealed record MediaRow(
        string GameId,
        string MediaType,
        string Region,
        int    IndexN,
        string ZipEntry,
        string Ext,
        long   FileSize);
}

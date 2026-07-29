using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Arkadia.Data;

public sealed record ScreenScraperCacheCandidate(
    int    PackageId,
    string PackagePath,
    string ProviderGameId,
    string SystemId,
    string SystemName,
    string Title,
    bool   HasPayload,
    bool   HasMedia);

/// <summary>
/// Searches indexed ScreenScraper cache packages in catalog.db without any network calls.
/// </summary>
public sealed class ScreenScraperCacheSearchService(CatalogService catalog)
{
    /// <summary>
    /// Searches cache_package_games by title.
    /// Exact matches appear before contains matches; same-system candidates appear before others.
    /// Only candidates with has_payload=1 and a package_path that still exists on disk are returned.
    /// </summary>
    public IReadOnlyList<ScreenScraperCacheCandidate> Search(
        string query, string? systemId = null, int maxResults = 50)
    {
        query = query.Trim();
        if (query.Length == 0) return [];

        using var conn = new SqliteConnection($"Data Source={catalog.DbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                cp.id,
                cp.package_path,
                cpg.provider_game_id,
                cpg.system_id,
                cp.system_name,
                cpg.title,
                cpg.has_payload,
                cpg.has_media,
                CASE
                    WHEN EXISTS (
                        SELECT 1 FROM cache_package_search_terms st
                        WHERE st.package_game_id = cpg.id
                          AND st.normalized_term  = LOWER($query)
                          AND st.term_type IN ('romfilename', 'shortname')
                    ) THEN 0
                    WHEN LOWER(cpg.title) = LOWER($query) THEN 1
                    ELSE 2
                END AS rank_match
            FROM cache_package_games cpg
            JOIN cache_packages cp ON cpg.package_id = cp.id
            WHERE cpg.has_payload = 1
              AND (
                  LOWER(cpg.title) = LOWER($query)
               OR LOWER(cpg.title) LIKE '%' || LOWER($query) || '%'
               OR EXISTS (
                   SELECT 1 FROM cache_package_search_terms st
                   WHERE st.package_game_id = cpg.id
                     AND (   st.normalized_term = LOWER($query)
                          OR st.normalized_term LIKE '%' || LOWER($query) || '%')
               )
              )
            ORDER BY
                rank_match,
                CASE WHEN cpg.system_id = $systemId THEN 0 ELSE 1 END,
                cpg.title COLLATE NOCASE
            LIMIT $max
            """;
        cmd.Parameters.AddWithValue("$query",    query);
        cmd.Parameters.AddWithValue("$systemId", systemId ?? "");
        cmd.Parameters.AddWithValue("$max",      maxResults);

        var results = new List<ScreenScraperCacheCandidate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(1);
            if (!File.Exists(path)) continue;

            results.Add(new ScreenScraperCacheCandidate(
                PackageId:      reader.GetInt32(0),
                PackagePath:    path,
                ProviderGameId: reader.GetString(2),
                SystemId:       reader.GetString(3),
                SystemName:     reader.GetString(4),
                Title:          reader.GetString(5),
                HasPayload:     reader.GetInt32(6) != 0,
                HasMedia:       reader.GetInt32(7) != 0));
        }

        return results;
    }
}

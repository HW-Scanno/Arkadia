using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Arkadia.Providers;

/// <summary>Metadata and media URLs returned by a successful ScreenScraper query.</summary>
public sealed class ScreenScraperResult
{
    public string  Title            { get; init; } = "";
    public string  OriginalTitle    { get; init; } = "";
    public string  Developer        { get; init; } = "";
    public string  Publisher        { get; init; } = "";
    public string  Year             { get; init; } = "";
    public string  Description      { get; init; } = "";
    public string? CoverUrl         { get; init; }
    /// <summary>File extension for the cover, from the JSON format field (e.g. "png", "jpg").</summary>
    public string  CoverFormat      { get; init; } = "jpg";
    public string? ScreenshotUrl    { get; init; }
    public string  ScreenshotFormat { get; init; } = "png";
    public string? VideoUrl         { get; init; }
    public string  VideoFormat      { get; init; } = "mp4";
    /// <summary>Comma-separated uppercase language codes extracted from the API response, e.g. "EN, FR".</summary>
    public string  Languages        { get; init; } = "";
}

/// <summary>
/// Thin ScreenScraper API v2 client.
/// One public method: <see cref="QueryAsync"/>.
/// Media downloads are handled separately by the caller.
/// </summary>
public static class ScreenScraperClient
{
    private const string BaseUrl  = "https://www.screenscraper.fr/api2/jeuInfos.php";
    private const string SoftName = "Arkadia";

    // ── Platform ID mapping ───────────────────────────────────────────────────
    // Arkadia platformId → ScreenScraper systemeid
    private static readonly Dictionary<string, int> PlatformMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Nintendo
            ["nes"]        = 3,
            ["famicom"]    = 3,
            ["snes"]       = 4,
            ["n64"]        = 14,
            ["gcn"]        = 13,
            ["gamecube"]   = 13,
            ["wii"]        = 16,
            ["wiiu"]       = 18,
            ["gb"]         = 9,
            ["gbc"]        = 10,
            ["gba"]        = 12,
            ["nds"]        = 15,
            ["3ds"]        = 17,
            ["virtualboy"] = 11,
            // Sony
            ["psx"]        = 57,
            ["ps1"]        = 57,
            ["ps2"]        = 58,
            ["ps3"]        = 59,
            ["psp"]        = 61,
            ["psvita"]     = 62,
            // Sega
            ["ms"]         = 2,
            ["mastersystem"]= 2,
            ["sms"]        = 2,
            ["md"]         = 1,
            ["genesis"]    = 1,
            ["megadrive"]  = 1,
            ["segacd"]     = 20,
            ["32x"]        = 19,
            ["saturn"]     = 22,
            ["dreamcast"]  = 23,
            ["gg"]         = 21,
            ["gamegear"]   = 21,
            ["sg1000"]     = 6,
            // NEC
            ["pce"]        = 31,
            ["tg16"]       = 31,
            ["pcecd"]      = 114,
            ["pcfx"]       = 72,
            // SNK
            ["ngp"]        = 25,
            ["ngpc"]       = 82,
            ["neogeo"]     = 142,
            // Atari
            ["a2600"]      = 26,
            ["a5200"]      = 40,
            ["a7800"]      = 41,
            ["lynx"]       = 28,
            ["jaguar"]     = 27,
            ["st"]         = 42,
            // Arcade / MAME
            ["mame"]       = 75,
            ["arcade"]     = 75,
            ["fbneo"]      = 75,
            // Computers
            ["amiga"]      = 64,
            ["c64"]        = 66,
            ["dos"]        = 135,
        };

    public static bool TryResolveSystemId(string platformId, out int systemId)
        => PlatformMap.TryGetValue(platformId, out systemId);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries ScreenScraper for metadata on a single release.
    /// Returns null when the game is not found.
    /// Throws <see cref="ScreenScraperRateLimitException"/> on HTTP 429.
    /// Throws <see cref="HttpRequestException"/> on unrecoverable HTTP errors.
    /// </summary>
    public static async Task<ScreenScraperResult?> QueryAsync(
        string devId, string devPassword,
        string username, string password,
        string platformId, string releaseName,
        bool   isMame,
        CancellationToken ct = default)
    {
        if (!TryResolveSystemId(platformId, out var systemId))
            throw new InvalidOperationException(
                $"No ScreenScraper system ID mapped for platform '{platformId}'.");

        var stem = releaseName;

        // First attempt: romnom with .zip extension
        var firstRomNom = isMame ? $"{stem}.zip" : $"{stem}.zip";
        var url1 = BuildUrl(devId, devPassword, username, password, systemId, firstRomNom, false);
        var result = await TryFetchAsync(url1, ct);

        if (result is null && isMame)
        {
            // MAME fallback: bare shortname without extension
            var url2 = BuildUrl(devId, devPassword, username, password, systemId, stem, false);
            result = await TryFetchAsync(url2, ct);
        }

        if (result is null)
        {
            // Final fallback: text search by name
            var url3 = BuildUrl(devId, devPassword, username, password, systemId, stem, true);
            result = await TryFetchAsync(url3, ct);
        }

        return result;
    }

    // ── Test connection ───────────────────────────────────────────────────────

    /// <summary>
    /// Calls ssuserInfos.php and returns the logged-in username on success.
    /// Throws <see cref="ScreenScraperRateLimitException"/> on 429, or
    /// <see cref="InvalidOperationException"/> if authentication fails.
    /// </summary>
    public static async Task<string> TestConnectionAsync(
        string devId, string devPassword,
        string username, string password,
        CancellationToken ct = default)
    {
        var url = $"https://www.screenscraper.fr/api2/ssuserInfos.php" +
                  $"?devid={Uri.EscapeDataString(devId)}" +
                  $"&devpassword={Uri.EscapeDataString(devPassword)}" +
                  $"&ssid={Uri.EscapeDataString(username)}" +
                  $"&sspassword={Uri.EscapeDataString(password)}" +
                  $"&softname={SoftName}" +
                  $"&output=json";

        var response = await ProviderHelpers.Http.GetAsync(url, ct);

        if ((int)response.StatusCode == 429)
            throw new ScreenScraperRateLimitException();

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("header", out var header) &&
            header.TryGetProperty("success", out var success) &&
            success.GetString() == "false")
        {
            var err = header.TryGetProperty("error", out var e) ? (e.GetString() ?? "auth failed") : "auth failed";
            throw new InvalidOperationException(err);
        }

        // Try to pull the login name from the response
        if (root.TryGetProperty("response", out var resp) &&
            resp.TryGetProperty("ssuser", out var ssuser) &&
            ssuser.TryGetProperty("id", out var id))
            return id.GetString() ?? username;

        return username;
    }

    // ── Valid media extension lists ───────────────────────────────────────────

    public static readonly IReadOnlyList<string> ValidImageExts =
        [".png", ".jpg", ".jpeg", ".webp"];

    public static readonly IReadOnlyList<string> ValidVideoExts =
        [".mp4", ".webm", ".mkv"];

    // ── Download helper (reuses ProviderHelpers.Http) ─────────────────────────

    /// <summary>
    /// Downloads <paramref name="url"/> to a .tmp file then resolves the file extension via
    /// (in priority order): <paramref name="hintFormat"/> from the JSON format field,
    /// the HTTP Content-Type header, and magic-byte sniffing.
    /// Returns the final saved path when a valid extension is detected; null when the file
    /// is skipped (already present), empty, or has an unrecognised media type.
    /// Throws on network errors; caller is responsible for catching.
    /// </summary>
    public static async Task<string?> DownloadMediaAsync(
        string url,
        string destStem,
        string hintFormat,
        IReadOnlyList<string> validExts,
        CancellationToken ct = default)
    {
        // Skip if any file already exists at this stem (regardless of extension)
        var dir      = Path.GetDirectoryName(destStem)!;
        var stemName = Path.GetFileName(destStem);
        if (Directory.Exists(dir) &&
            Directory.EnumerateFiles(dir, stemName + ".*").Any())
            return null;

        var tmpPath = destStem + ".tmp";
        HttpResponseMessage response;
        try
        {
            response = await ProviderHelpers.Http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        try
        {
            await using var src  = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.Create(tmpPath);
            await src.CopyToAsync(dest, ct);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }

        if (new FileInfo(tmpPath).Length == 0) { TryDelete(tmpPath); return null; }

        var ext = ResolveExtension(hintFormat, contentType, tmpPath, validExts);
        if (ext is null) { TryDelete(tmpPath); return null; }

        var finalPath = destStem + ext;
        File.Move(tmpPath, finalPath, overwrite: false);
        return finalPath;
    }

    // ── Extension resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves the media file extension using three fallback tiers:
    /// 1. <paramref name="hintFormat"/> (ScreenScraper JSON "format" field)
    /// 2. HTTP Content-Type header (<paramref name="contentType"/>)
    /// 3. Magic-byte sniffing of the already-downloaded <paramref name="tmpPath"/>
    /// Returns null when none of the tiers yield a value present in <paramref name="validExts"/>.
    /// </summary>
    internal static string? ResolveExtension(
        string hintFormat,
        string contentType,
        string tmpPath,
        IReadOnlyList<string> validExts)
    {
        var valid = new HashSet<string>(
            validExts.Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        // 1. JSON hint
        if (!string.IsNullOrEmpty(hintFormat))
        {
            var ext = hintFormat.StartsWith('.') ? hintFormat.ToLowerInvariant()
                                                 : "." + hintFormat.ToLowerInvariant();
            if (valid.Contains(ext)) return ext;
        }

        // 2. Content-Type
        var fromCt = ContentTypeToExt(contentType);
        if (fromCt is not null && valid.Contains(fromCt)) return fromCt;

        // 3. Magic bytes
        var fromMagic = SniffExtension(tmpPath);
        if (fromMagic is not null && valid.Contains(fromMagic)) return fromMagic;

        return null;
    }

    private static string? ContentTypeToExt(string contentType) => contentType switch
    {
        "image/png"         => ".png",
        "image/jpeg"        => ".jpg",
        "image/webp"        => ".webp",
        "video/mp4"         => ".mp4",
        "video/webm"        => ".webm",
        "video/x-matroska"  => ".mkv",
        "video/mkv"         => ".mkv",
        _                   => null,
    };

    private static string? SniffExtension(string path)
    {
        try
        {
            Span<byte> buf = stackalloc byte[12];
            using var f    = File.OpenRead(path);
            var read = f.Read(buf);
            if (read < 4) return null;

            // PNG: 89 50 4E 47
            if (buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47)
                return ".png";
            // JPEG: FF D8 FF
            if (buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF)
                return ".jpg";
            // RIFF/WebP: 52 49 46 46 .. .. .. .. 57 45 42 50
            if (read >= 12 &&
                buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46 &&
                buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50)
                return ".webp";
            // MP4: ftyp box at offset 4 (66 74 79 70)
            if (read >= 8 &&
                buf[4] == 0x66 && buf[5] == 0x74 && buf[6] == 0x79 && buf[7] == 0x70)
                return ".mp4";
            // WebM/MKV: EBML magic 1A 45 DF A3
            if (buf[0] == 0x1A && buf[1] == 0x45 && buf[2] == 0xDF && buf[3] == 0xA3)
                return ".webm";
        }
        catch { /* best-effort */ }
        return null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string BuildUrl(
        string devId, string devPassword,
        string username, string password,
        int systemId, string queryValue, bool isSearch)
    {
        var encoded = Uri.EscapeDataString(queryValue);
        var queryParam = isSearch ? $"recherche={encoded}" : $"romnom={encoded}";
        return $"{BaseUrl}?devid={Uri.EscapeDataString(devId)}" +
               $"&devpassword={Uri.EscapeDataString(devPassword)}" +
               $"&ssid={Uri.EscapeDataString(username)}" +
               $"&sspassword={Uri.EscapeDataString(password)}" +
               $"&softname={SoftName}" +
               $"&output=json" +
               $"&systemeid={systemId}" +
               $"&{queryParam}";
    }

    private static async Task<ScreenScraperResult?> TryFetchAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await ProviderHelpers.Http.GetAsync(url, ct);
        }
        catch (HttpRequestException)
        {
            // Retry once on transient network error
            await Task.Delay(2000, ct);
            response = await ProviderHelpers.Http.GetAsync(url, ct);
        }

        if ((int)response.StatusCode == 429)
            throw new ScreenScraperRateLimitException();

        if ((int)response.StatusCode >= 500)
        {
            // Retry once on 5xx
            await Task.Delay(2000, ct);
            response = await ProviderHelpers.Http.GetAsync(url, ct);
            if ((int)response.StatusCode >= 500)
                response.EnsureSuccessStatusCode();
        }

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseGameJson(json);
    }

    internal static ScreenScraperResult? ParseGameJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check API-level error
            if (root.TryGetProperty("header", out var header) &&
                header.TryGetProperty("success", out var success) &&
                success.GetString() == "false")
                return null;

            if (!root.TryGetProperty("response", out var response)) return null;
            if (!response.TryGetProperty("jeu",      out var jeu))      return null;

            var (coverUrl, coverFmt) = PickMediaUrl(jeu, "box-2D", "jpg");
            var (ssUrl,    ssFmt)    = PickMediaUrl(jeu, "ss",     "png");
            var (vidUrl,   vidFmt)   = PickMediaUrl(jeu, "video",  "mp4");

            // Decode HTML entities once at the result-construction boundary.
            static string Decode(string s) =>
                s.Length > 0 ? WebUtility.HtmlDecode(s) : s;

            return new ScreenScraperResult
            {
                Title            = Decode(PickName(jeu, preferredRegions: ["wor", "us", "eu", "jp"])),
                OriginalTitle    = Decode(PickName(jeu, preferredRegions: ["jp"])),
                Developer        = Decode(GetText(jeu, "developpeur")),
                Publisher        = Decode(GetText(jeu, "editeur")),
                Year             = PickDate(jeu),
                Description      = Decode(PickSynopsis(jeu)),
                Languages        = PickLanguages(jeu),
                CoverUrl         = coverUrl,
                CoverFormat      = coverFmt,
                ScreenshotUrl    = ssUrl,
                ScreenshotFormat = ssFmt,
                VideoUrl         = vidUrl,
                VideoFormat      = vidFmt,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string PickName(JsonElement jeu, string[] preferredRegions)
    {
        if (!jeu.TryGetProperty("noms", out var noms) || noms.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var region in preferredRegions)
        {
            foreach (var nom in noms.EnumerateArray())
            {
                if (nom.TryGetProperty("region", out var r) && r.GetString() == region &&
                    nom.TryGetProperty("text",   out var t))
                    return t.GetString() ?? "";
            }
        }
        // Fallback: first available
        foreach (var nom in noms.EnumerateArray())
        {
            if (nom.TryGetProperty("text", out var t))
                return t.GetString() ?? "";
        }
        return "";
    }

    private static string GetText(JsonElement jeu, string fieldName)
    {
        if (jeu.TryGetProperty(fieldName, out var field) &&
            field.TryGetProperty("text", out var text))
            return text.GetString() ?? "";
        return "";
    }

    private static string PickDate(JsonElement jeu)
    {
        if (!jeu.TryGetProperty("dates", out var dates) || dates.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var region in new[] { "wor", "us", "eu", "jp" })
        {
            foreach (var d in dates.EnumerateArray())
            {
                if (d.TryGetProperty("region", out var r) && r.GetString() == region &&
                    d.TryGetProperty("text",   out var t))
                {
                    var year = ExtractYear(t.GetString() ?? "");
                    if (year.Length > 0) return year;
                }
            }
        }
        // Fallback: first entry with a parseable year
        foreach (var d in dates.EnumerateArray())
        {
            if (d.TryGetProperty("text", out var t))
            {
                var year = ExtractYear(t.GetString() ?? "");
                if (year.Length > 0) return year;
            }
        }
        return "";
    }

    private static string ExtractYear(string raw)
    {
        if (raw.Length < 4) return "";
        var y = raw[..4];
        return y.All(char.IsDigit) ? y : "";
    }

    internal static string PickLanguages(JsonElement jeu)
    {
        if (!jeu.TryGetProperty("langues", out var langues) || langues.ValueKind != JsonValueKind.Array)
            return "";
        var codes = new List<string>();
        foreach (var l in langues.EnumerateArray())
        {
            if (l.TryGetProperty("shortname", out var sn) &&
                sn.GetString() is { Length: > 0 } code)
                codes.Add(code.ToUpperInvariant());
        }
        return string.Join(", ", codes);
    }

    private static string PickSynopsis(JsonElement jeu)
    {
        if (!jeu.TryGetProperty("synopsis", out var synopses) || synopses.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var lang in new[] { "en", "fr", "de", "es" })
        {
            foreach (var s in synopses.EnumerateArray())
            {
                if (s.TryGetProperty("langue", out var l) && l.GetString() == lang &&
                    s.TryGetProperty("text",   out var t))
                    return t.GetString() ?? "";
            }
        }
        // Fallback: first available
        foreach (var s in synopses.EnumerateArray())
        {
            if (s.TryGetProperty("text", out var t))
                return t.GetString() ?? "";
        }
        return "";
    }

    private static (string? Url, string Format) PickMediaUrl(
        JsonElement jeu, string mediaType, string defaultFormat)
    {
        if (!jeu.TryGetProperty("medias", out var medias) || medias.ValueKind != JsonValueKind.Array)
            return (null, defaultFormat);
        // Prefer world region, then us, then eu, then any
        foreach (var region in new[] { "wor", "us", "eu", "" })
        {
            foreach (var m in medias.EnumerateArray())
            {
                if (!m.TryGetProperty("type", out var mt) || mt.GetString() != mediaType) continue;
                if (region.Length > 0)
                {
                    if (!m.TryGetProperty("region", out var mr) || mr.GetString() != region) continue;
                }
                if (!m.TryGetProperty("url", out var urlProp)) continue;
                var url    = urlProp.GetString();
                if (url is null) continue;
                // Use the JSON format field — it is always a clean extension ("png","jpg","mp4").
                // Never extract extension from the URL itself (ScreenScraper URLs end in .php).
                var format = m.TryGetProperty("format", out var fp) && fp.GetString() is { Length: > 0 } fs
                    ? fs
                    : defaultFormat;
                return (url, format);
            }
        }
        return (null, defaultFormat);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }
}

/// <summary>Thrown when ScreenScraper returns HTTP 429 (rate limited).</summary>
public sealed class ScreenScraperRateLimitException : Exception
{
    public ScreenScraperRateLimitException()
        : base("ScreenScraper rate limit reached. Please wait before trying again.") { }
}

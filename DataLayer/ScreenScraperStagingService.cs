using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Arkadia.Data;

public sealed record ScreenScraperStagingRecord(
    string    Provider,
    string    PackageName,
    string    FolderPath,
    string    Status,
    int       TotalGames,
    int       PayloadCount,
    int       MediaFileCount,
    double    CompletionPercent,
    long      SizeBytes,
    DateTime? LastUpdatedUtc)
{
    public bool IsComplete  => Status == "Complete";
    public bool IsResumable => Status == "Resumable";
    public bool IsUnknown   => Status != "Complete" && Status != "Resumable";

    public string CompletionDisplay => TotalGames > 0
        ? $"{CompletionPercent:F1}%  ({PayloadCount} / {TotalGames} payloads)"
        : "—";

    public string SizeDisplay
    {
        get
        {
            if (SizeBytes < 1_048_576)       return $"{SizeBytes / 1024.0:F1} KB";
            if (SizeBytes < 1_073_741_824)   return $"{SizeBytes / 1_048_576.0:F1} MB";
            return $"{SizeBytes / 1_073_741_824.0:F2} GB";
        }
    }

    public string LastUpdatedDisplay => LastUpdatedUtc?.ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public sealed class ScreenScraperStagingService
{
    private static readonly Regex CsvLineRx = new(
        @"^""(?<id>[^""]*)"";""(?<name>.*)""$",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    private readonly string _baseDir;

    public ScreenScraperStagingService(string baseDir) => _baseDir = baseDir;

    private string StagingProviderRoot =>
        Path.Combine(_baseDir, ArkadiaFolders.StagingCache, ArkadiaFolders.ScrapeCacheProvider);

    public IReadOnlyList<ScreenScraperStagingRecord> LoadStagingRecords()
    {
        var root = StagingProviderRoot;
        if (!Directory.Exists(root))
            return Array.Empty<ScreenScraperStagingRecord>();

        var results = new List<ScreenScraperStagingRecord>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try { results.Add(BuildRecord(dir)); }
            catch { /* skip problematic folders */ }
        }

        return results
            .OrderByDescending(r => r.LastUpdatedUtc ?? DateTime.MinValue)
            .ThenByDescending(r => r.SizeBytes)
            .ToList();
    }

    public IReadOnlyList<ScreenScraperStagingRecord> LoadTopBySize(int count)
        => LoadStagingRecords()
               .OrderByDescending(r => r.SizeBytes)
               .Take(count)
               .ToList();

    /// <summary>
    /// Deletes <paramref name="stagingPath"/> recursively.
    /// Guards against path traversal: the path must be a direct child of the staging provider root,
    /// not equal to the root itself, and must resolve under the root after <c>GetFullPath</c>.
    /// </summary>
    public void DeleteStaging(string stagingPath)
    {
        var root = Path.GetFullPath(StagingProviderRoot);
        var full = Path.GetFullPath(stagingPath);

        // Must be under root (with separator to avoid prefix attacks)
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Path is not under the staging provider root.", nameof(stagingPath));

        // Must not be the root itself
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Cannot delete the staging provider root itself.", nameof(stagingPath));

        Directory.Delete(full, recursive: true);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private ScreenScraperStagingRecord BuildRecord(string dir)
    {
        var packageName = Path.GetFileName(dir);
        var csvPath     = Path.Combine(dir, "gameslist.csv");
        var payloadsDir = Path.Combine(dir, "payloads");
        var mediaDir    = Path.Combine(dir, "media");

        int  totalGames   = 0;
        int  payloadCount = 0;
        int  mediaCount   = 0;

        if (Directory.Exists(payloadsDir))
            payloadCount = Directory.GetFiles(payloadsDir, "*.json").Length;

        if (Directory.Exists(mediaDir))
            mediaCount = CountFilesRecursive(mediaDir);

        if (File.Exists(csvPath))
            totalGames = CountCsvGames(csvPath);

        var sizeBytes   = GetDirectorySize(dir);
        var lastUpdated = GetLastUpdated(dir);

        string status;
        if (totalGames == 0 && payloadCount == 0)
        {
            var hasFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
            status = hasFiles ? "Unknown" : "Empty";
        }
        else if (totalGames > 0 && payloadCount >= totalGames)
            status = "Complete";
        else
            status = "Resumable";

        var pct = totalGames > 0 ? (double)payloadCount / totalGames * 100.0 : 0.0;

        return new ScreenScraperStagingRecord(
            Provider:          ArkadiaFolders.ScrapeCacheProvider,
            PackageName:       packageName,
            FolderPath:        dir,
            Status:            status,
            TotalGames:        totalGames,
            PayloadCount:      payloadCount,
            MediaFileCount:    mediaCount,
            CompletionPercent: pct,
            SizeBytes:         sizeBytes,
            LastUpdatedUtc:    lastUpdated);
    }

    private static int CountCsvGames(string csvPath)
    {
        int  count = 0;
        bool first = true;
        try
        {
            using var reader = new StreamReader(csvPath, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                var m = CsvLineRx.Match(line);
                if (!m.Success) continue;
                var id    = m.Groups["id"].Value.Trim();
                var title = m.Groups["name"].Value.Trim();
                if (first)
                {
                    first = false;
                    if (string.Equals(id, "Game ID", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
                    count++;
            }
        }
        catch { /* ignore inaccessible files */ }
        return count;
    }

    private static int CountFilesRecursive(string dir)
    {
        int count = 0;
        try
        {
            foreach (var _ in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                count++;
        }
        catch { /* ignore inaccessible */ }
        return count;
    }

    private static long GetDirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; }
                catch { /* skip */ }
            }
        }
        catch { /* ignore */ }
        return total;
    }

    private static DateTime? GetLastUpdated(string dir)
    {
        DateTime? newest = null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (newest is null || t > newest.Value)
                        newest = t;
                }
                catch { /* skip */ }
            }
        }
        catch { /* ignore */ }
        return newest;
    }
}

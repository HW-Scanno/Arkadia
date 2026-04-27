using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace Arkadia;

internal static class ProviderHelpers
{
    internal static readonly HttpClient Http;

    static ProviderHelpers()
    {
        Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
        Http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Arkadia/1.0 DAT-Downloader (+https://github.com/arkadia)");
        Http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,application/json,*/*");
    }

    internal static string GetRedumpOutputDir()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        return Path.Combine(AppContext.BaseDirectory, "incoming-dats", $"Redump_{date}");
    }

    internal static string GetNoIntroOutputDir()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        return Path.Combine(AppContext.BaseDirectory, "incoming-dats", $"No-Intro_{date}");
    }

    internal static string GetTosecOutputDir()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        return Path.Combine(AppContext.BaseDirectory, "incoming-dats", $"TOSEC_{date}");
    }

    internal static string GetEggmansworldOutputDir()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        return Path.Combine(AppContext.BaseDirectory, "incoming-dats", $"Eggmansworld_{date}");
    }

    internal static string GetMameCacheRootDir() =>
        Path.Combine(AppContext.BaseDirectory, "incoming-dats", "mame");

    internal static string MameCategoryIniPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "mame", "category.ini");

    internal static async Task<bool> DownloadCategoryIniAsync(Action<string, string> log)
    {
        const string url =
            "https://raw.githubusercontent.com/AntoPISA/MAME_SupportFiles/main/category.ini/category.ini";
        try
        {
            var content = await Http.GetStringAsync(url);
            if (string.IsNullOrWhiteSpace(content))
            {
                log("Download failed: response was empty.", "#EF5350");
                return false;
            }
            if (!content.Contains("[Arcade"))
            {
                log("Downloaded category.ini appears invalid (no [Arcade section). File not saved.", "#EF5350");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(MameCategoryIniPath)!);
            await File.WriteAllTextAsync(MameCategoryIniPath, content, Encoding.UTF8);
            log($"category.ini saved ({content.Length:N0} chars).", "#4CAF50");
            return true;
        }
        catch (Exception ex)
        {
            log($"Download failed: {ex.Message}", "#EF5350");
            return false;
        }
    }

    internal static string UniqueFilePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;

        var ext  = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        for (int i = 2; i <= 99; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }

        return Path.Combine(dir, $"{stem} ({DateTime.Now:HHmmss}){ext}");
    }

    internal static string UniqueDirPath(string parentDir, string folderName)
    {
        var path = Path.Combine(parentDir, folderName);
        if (!Directory.Exists(path)) return path;

        for (int i = 2; i <= 99; i++)
        {
            path = Path.Combine(parentDir, $"{folderName} ({i})");
            if (!Directory.Exists(path)) return path;
        }

        return Path.Combine(parentDir, $"{folderName} ({DateTime.Now:HHmmss})");
    }

    internal static string ExtractDatFromZip(string zipPath, string outputDir)
    {
        using var archive = SharpCompress.Archives.ArchiveFactory.Open(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;

            var fileName = Path.GetFileName(entry.Key ?? "");
            if (!fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                continue;

            var destPath = UniqueFilePath(outputDir, fileName);

            using var src  = entry.OpenEntryStream();
            using var dest = File.Create(destPath);
            src.CopyTo(dest);

            return destPath;
        }

        throw new InvalidOperationException("Downloaded archive contained no .dat file.");
    }

    internal static (int Extracted, int Skipped) ExtractTosecArchive(
        string archivePath, string outputDir)
    {
        int extracted = 0, skipped = 0;

        using var archive = SharpCompress.Archives.ArchiveFactory.Open(archivePath);

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;

            var key      = entry.Key ?? "";
            var fileName = Path.GetFileName(key);
            if (string.IsNullOrEmpty(fileName)) continue;

            var subDir  = Path.GetDirectoryName(key) ?? "";
            var destDir = string.IsNullOrEmpty(subDir)
                ? outputDir
                : Path.Combine(outputDir, subDir);

            Directory.CreateDirectory(destDir);

            var destPath = Path.Combine(destDir, fileName);
            if (File.Exists(destPath)) { skipped++; continue; }

            using var src  = entry.OpenEntryStream();
            using var dest = File.Create(destPath);
            src.CopyTo(dest);
            extracted++;
        }

        return (extracted, skipped);
    }

    internal static void AppendLog(
        StackPanel panel, ScrollViewer scroll, string text, string color = "#888899")
    {
        panel.Children.Add(new TextBlock
        {
            Text         = $"[{DateTime.Now:HH:mm:ss}]  {text}",
            FontSize     = 11,
            FontFamily   = new FontFamily("Consolas,Courier New,monospace"),
            Foreground   = new SolidColorBrush(Color.Parse(color)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 0, 0, 2),
        });
        scroll.ScrollToEnd();
    }
}

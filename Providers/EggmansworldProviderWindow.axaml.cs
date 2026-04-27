using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class EggmansworldProviderWindow : Window
{
    private sealed record EggmansworldAsset(string Name, string DownloadUrl, long Size);
    private sealed record EggmansworldRelease(string TagName, string DisplayLabel, List<EggmansworldAsset> Assets);

    private readonly List<(Border Row, CheckBox Box, EggmansworldRelease Entry)> _rows = [];
    private CancellationTokenSource? _cts;

    public EggmansworldProviderWindow() { InitializeComponent(); }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnEggSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var q = EggSearch.Text?.Trim() ?? "";
        int visible = 0;
        foreach (var (row, _, entry) in _rows)
        {
            bool show = q.Length == 0 ||
                        entry.DisplayLabel.Contains(q, StringComparison.OrdinalIgnoreCase);
            row.IsVisible = show;
            if (show) visible++;
        }
        EggCountLabel.Text = visible == _rows.Count
            ? $"{_rows.Count} releases"
            : $"{visible} / {_rows.Count} releases";
    }

    // ── Select / Clear ────────────────────────────────────────────────────────

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        foreach (var (row, box, _) in _rows) { if (row.IsVisible) box.IsChecked = true; }
        UpdateSelectedCount();
    }

    private void OnClearAll(object? sender, RoutedEventArgs e)
    {
        foreach (var (_, box, _) in _rows) box.IsChecked = false;
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        int n = _rows.Count(r => r.Box.IsChecked == true);
        EggSelectedCount.Text = n == 0 ? "0 selected" : $"{n} selected";
        UpdateDownloadButtonState();
    }

    private void UpdateDownloadButtonState()
    {
        bool busy = CancelButton.IsVisible;
        DownloadButton.IsEnabled = !busy && _rows.Any(r => r.Box.IsChecked == true);
    }

    // ── Footer handlers ───────────────────────────────────────────────────────

    private void OnRefreshList(object? sender, RoutedEventArgs e)     => RefreshReleasesAsync();
    private void OnDownloadSelected(object? sender, RoutedEventArgs e) => DownloadSelectedAsync();

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        AppendLog("Cancelling…", "#FFA726");
        _cts?.Cancel();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async void RefreshReleasesAsync()
    {
        SetBusy(true);
        EggPacksPanel.Children.Clear();
        _rows.Clear();
        EggCountLabel.Text    = "";
        EggStatusText.Text    = "Loading release list…";
        EggSelectedCount.Text = "0 selected";

        using var cts = new CancellationTokenSource();
        _cts = cts;

        AppendLog("Fetching releases from GitHub…");

        try
        {
            var releases = await FetchReleasesAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            BuildRows(releases);
            EggStatusText.Text = $"{releases.Count} release(s) available.";
            EggCountLabel.Text = $"{releases.Count} releases";
            AppendLog($"Loaded {releases.Count} release(s).", "#4CAF50");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Refresh cancelled.", "#888899");
            EggStatusText.Text = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Error fetching list: {ex.Message}", "#EF5350");
            EggStatusText.Text = "Failed to load. Check network connection and try again.";
        }
        finally
        {
            SetBusy(false);
            _cts = null;
        }
    }

    private void BuildRows(List<EggmansworldRelease> releases)
    {
        foreach (var release in releases)
        {
            var assetSummary = release.Assets.Count == 1
                ? "1 asset"
                : $"{release.Assets.Count} assets";

            var label = $"{release.DisplayLabel}  ({assetSummary})";

            var cb = new CheckBox
            {
                Content    = label,
                FontSize   = 12,
                Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                Padding    = new Avalonia.Thickness(12, 5, 12, 5),
            };
            cb.IsCheckedChanged += (_, _) => UpdateSelectedCount();

            var row = new Border
            {
                Child           = cb,
                BorderBrush     = new SolidColorBrush(Color.Parse("#141420")),
                BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            };

            EggPacksPanel.Children.Add(row);
            _rows.Add((row, cb, release));
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async void DownloadSelectedAsync()
    {
        var selected = _rows
            .Where(r => r.Box.IsChecked == true)
            .Select(r => r.Entry)
            .ToList();

        if (selected.Count == 0) return;

        SetBusy(true);

        var outputDir   = ProviderHelpers.GetEggmansworldOutputDir();
        Directory.CreateDirectory(outputDir);

        var relativeOut = Path.GetRelativePath(AppContext.BaseDirectory, outputDir);
        AppendLog($"Starting download — {selected.Count} release(s)");
        AppendLog($"Output: {relativeOut}");

        using var cts = new CancellationTokenSource();
        _cts = cts;

        int totalFiles = 0, failed = 0;
        bool cancelled = false;

        for (int i = 0; i < selected.Count && !cancelled; i++)
        {
            if (cts.IsCancellationRequested) { cancelled = true; break; }

            var release = selected[i];
            AppendLog($"[{i + 1}/{selected.Count}]  Release: {release.DisplayLabel}  ({release.Assets.Count} asset(s))");

            foreach (var asset in release.Assets)
            {
                if (cts.IsCancellationRequested) { cancelled = true; break; }

                AppendLog($"     Downloading: {asset.Name}…");
                SetProgress(asset.Name, 0, -1);

                string savedPath = "";
                try
                {
                    var progressReporter = new Progress<(string Name, long Received, long Total)>(p =>
                        SetProgress(p.Name, p.Received, p.Total));

                    await Task.Run(async () =>
                    {
                        savedPath = await DownloadAssetAsync(asset, outputDir, progressReporter, cts.Token);
                    }, cts.Token);

                    AppendLog($"     Saved: {Path.GetFileName(savedPath)}", "#4CAF50");
                    totalFiles++;
                }
                catch (OperationCanceledException)
                {
                    AppendLog($"     Cancelled: {asset.Name}", "#888899");
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    AppendLog($"     Failed: {asset.Name}  —  {ex.Message}", "#EF5350");
                    failed++;
                }
            }
        }

        ResetProgress();
        AppendLog(
            $"Done.  Files: {totalFiles}  Failed: {failed}" + (cancelled ? "  (cancelled)" : ""),
            "#7B68EE");

        SetBusy(false);
        _cts = null;
    }

    // ── Network / parsing ─────────────────────────────────────────────────────

    private static async Task<List<EggmansworldRelease>> FetchReleasesAsync(CancellationToken ct)
    {
        const string apiUrl = "https://api.github.com/repos/Eggmansworld/Datfiles/releases?per_page=100";

        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await ProviderHelpers.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseReleases(json);
    }

    private static List<EggmansworldRelease> ParseReleases(string json)
    {
        var releases = new List<EggmansworldRelease>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return releases;

        foreach (var rel in root.EnumerateArray())
        {
            string tagName = rel.TryGetProperty("tag_name", out var t)  ? t.GetString() ?? "" : "";
            string name    = rel.TryGetProperty("name",     out var nm) ? nm.GetString() ?? tagName : tagName;

            string date = "";
            if (rel.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(pub.GetString(), out var dt))
                    date = dt.ToString("yyyy-MM-dd");
            }

            var assets = new List<EggmansworldAsset>();
            if (rel.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name",                 out var an)  ? an.GetString()  ?? "" : "";
                    string dlUrl     = asset.TryGetProperty("browser_download_url", out var au)  ? au.GetString()  ?? "" : "";
                    long   size      = asset.TryGetProperty("size",                 out var sz)  ? sz.GetInt64()         : 0L;

                    if (!string.IsNullOrEmpty(assetName) && !string.IsNullOrEmpty(dlUrl))
                        assets.Add(new EggmansworldAsset(assetName, dlUrl, size));
                }
            }

            if (assets.Count == 0) continue;

            var displayLabel = string.IsNullOrEmpty(date) ? name : $"{name}  ·  {date}";
            releases.Add(new EggmansworldRelease(tagName, displayLabel, assets));
        }

        return releases;
    }

    private static async Task<string> DownloadAssetAsync(
        EggmansworldAsset                                  asset,
        string                                             outputDir,
        IProgress<(string Name, long Received, long Total)> progress,
        CancellationToken                                  ct)
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            using (var resp = await ProviderHelpers.Http.GetAsync(
                       asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1L;

                using var netStream  = await resp.Content.ReadAsStreamAsync(ct);
                using var fileStream = File.Create(tempFile);

                var  buffer   = new byte[65536];
                long received = 0;
                int  read;
                while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    progress.Report((asset.Name, received, total));
                }
            }

            ct.ThrowIfCancellationRequested();

            var ext      = Path.GetExtension(asset.Name);
            bool isArchive = string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ext, ".7z",  StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase);

            if (isArchive)
            {
                var folderName = Path.GetFileNameWithoutExtension(asset.Name);
                var targetDir  = ProviderHelpers.UniqueDirPath(outputDir, folderName);
                Directory.CreateDirectory(targetDir);
                ProviderHelpers.ExtractTosecArchive(tempFile, targetDir);
                return targetDir;
            }
            else
            {
                var destPath = ProviderHelpers.UniqueFilePath(outputDir, asset.Name);
                File.Move(tempFile, destPath);
                tempFile = "";  // moved — skip delete in finally
                return destPath;
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempFile))
                try { File.Delete(tempFile); } catch { }
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        CancelButton.IsVisible  = busy;

        if (!busy) ResetProgress();

        UpdateDownloadButtonState();
    }

    private void SetProgress(string name, long received, long total)
    {
        EggProgressLabel.Text = name;

        if (total > 0)
        {
            EggDownloadProgress.IsIndeterminate = false;
            EggDownloadProgress.Value           = (double)received / total * 100.0;
            EggProgressDetail.Text              = $"{received / 1024:N0} KB / {total / 1024:N0} KB";
        }
        else if (received > 0)
        {
            EggDownloadProgress.IsIndeterminate = true;
            EggProgressDetail.Text              = $"{received / 1024:N0} KB";
        }
        else
        {
            EggDownloadProgress.IsIndeterminate = true;
            EggProgressDetail.Text              = "";
        }
    }

    private void ResetProgress()
    {
        EggProgressLabel.Text               = "";
        EggProgressDetail.Text              = "";
        EggDownloadProgress.IsIndeterminate = false;
        EggDownloadProgress.Value           = 0;
    }

    private void AppendLog(string text, string color = "#888899") =>
        ProviderHelpers.AppendLog(EggLogPanel, EggLogScrollViewer, text, color);
}

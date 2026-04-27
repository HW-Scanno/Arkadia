using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class TosecProviderWindow : Window
{
    // CategoryUrl: relative path, e.g. /downloads/category/59-2025-03-13
    private sealed record TosecEntry(string CategoryUrl, string Label, string Date);

    private readonly List<(Border Row, CheckBox Box, TosecEntry Entry)> _tosecRows = [];
    private CancellationTokenSource? _cts;

    public TosecProviderWindow() { InitializeComponent(); }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnTosecSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var q = TosecSearch.Text?.Trim() ?? "";
        int visible = 0;
        foreach (var (row, _, entry) in _tosecRows)
        {
            bool show = q.Length == 0 ||
                        entry.Label.Contains(q, StringComparison.OrdinalIgnoreCase);
            row.IsVisible = show;
            if (show) visible++;
        }
        TosecCountLabel.Text = visible == _tosecRows.Count
            ? $"{_tosecRows.Count} packs"
            : $"{visible} / {_tosecRows.Count} packs";
    }

    // ── Select / Clear ────────────────────────────────────────────────────────

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        foreach (var (row, box, _) in _tosecRows) { if (row.IsVisible) box.IsChecked = true; }
        UpdateSelectedCount();
    }

    private void OnClearAll(object? sender, RoutedEventArgs e)
    {
        foreach (var (_, box, _) in _tosecRows) box.IsChecked = false;
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        int n = _tosecRows.Count(r => r.Box.IsChecked == true);
        TosecSelectedCount.Text = n == 0 ? "0 selected" : $"{n} selected";
        UpdateDownloadButtonState();
    }

    private void UpdateDownloadButtonState()
    {
        bool busy = CancelButton.IsVisible;
        DownloadButton.IsEnabled = !busy && _tosecRows.Any(r => r.Box.IsChecked == true);
    }

    // ── Footer button handlers ────────────────────────────────────────────────

    private void OnRefreshList(object? sender, RoutedEventArgs e) => RefreshTosecAsync();

    private void OnDownloadSelected(object? sender, RoutedEventArgs e) => DownloadTosecSelectedAsync();

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

    private async void RefreshTosecAsync()
    {
        SetBusy(true);
        TosecPacksPanel.Children.Clear();
        _tosecRows.Clear();
        TosecCountLabel.Text    = "";
        TosecStatusText.Text    = "Loading pack list…";
        TosecSelectedCount.Text = "0 selected";

        using var cts = new CancellationTokenSource();
        _cts = cts;

        AppendLog("Fetching TOSEC pack list from tosecdev.org…");

        try
        {
            var entries = await FetchTosecPacksAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            BuildTosecRows(entries);
            TosecStatusText.Text = $"{entries.Count} pack(s) available.";
            TosecCountLabel.Text = $"{entries.Count} packs";
            AppendLog($"Loaded {entries.Count} pack(s).", "#4CAF50");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Refresh cancelled.", "#888899");
            TosecStatusText.Text = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Error fetching list: {ex.Message}", "#EF5350");
            TosecStatusText.Text = "Failed to load. Check network connection and try again.";
        }
        finally
        {
            SetBusy(false);
            _cts = null;
        }
    }

    private void BuildTosecRows(List<TosecEntry> entries)
    {
        foreach (var entry in entries)
        {
            var cb = new CheckBox
            {
                Content    = entry.Label,
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

            TosecPacksPanel.Children.Add(row);
            _tosecRows.Add((row, cb, entry));
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async void DownloadTosecSelectedAsync()
    {
        var selected = _tosecRows
            .Where(r => r.Box.IsChecked == true)
            .Select(r => r.Entry)
            .ToList();

        if (selected.Count == 0) return;

        SetBusy(true);

        var outputDir = ProviderHelpers.GetTosecOutputDir();
        Directory.CreateDirectory(outputDir);

        var relativeOut = Path.GetRelativePath(AppContext.BaseDirectory, outputDir);
        AppendLog($"Starting download — {selected.Count} pack(s)");
        AppendLog($"Output: {relativeOut}");

        using var cts = new CancellationTokenSource();
        _cts = cts;

        int totalExtracted = 0, totalSkipped = 0, failed = 0;

        for (int i = 0; i < selected.Count; i++)
        {
            if (cts.IsCancellationRequested) break;

            var entry = selected[i];
            AppendLog($"[{i + 1}/{selected.Count}]  Downloading: {entry.Label}…");
            SetTosecProgress(entry.Label, 0, -1);

            try
            {
                int extracted = 0, skipped = 0;

                var progressReporter = new Progress<(string Name, long Received, long Total)>(p =>
                    SetTosecProgress(p.Name, p.Received, p.Total));

                await Task.Run(async () =>
                {
                    (extracted, skipped) = await DownloadTosecPackAsync(
                        entry, outputDir, progressReporter, cts.Token);
                }, cts.Token);

                totalExtracted += extracted;
                totalSkipped   += skipped;

                AppendLog(
                    $"     Extracted: {extracted}  Skipped (collision): {skipped}",
                    "#4CAF50");
            }
            catch (OperationCanceledException)
            {
                AppendLog($"     Cancelled: {entry.Label}", "#888899");
                break;
            }
            catch (Exception ex)
            {
                AppendLog($"     Failed: {entry.Label}  —  {ex.Message}", "#EF5350");
                failed++;
            }
        }

        ResetTosecProgress();
        bool wasCancelled = cts.IsCancellationRequested;
        AppendLog(
            $"Done.  Extracted: {totalExtracted}  Skipped: {totalSkipped}  Failed: {failed}" +
            (wasCancelled ? "  (cancelled)" : ""),
            "#7B68EE");

        SetBusy(false);
        _cts = null;
    }

    // ── Network / parsing ─────────────────────────────────────────────────────

    private static async Task<List<TosecEntry>> FetchTosecPacksAsync(CancellationToken ct)
    {
        const string listUrl = "https://www.tosecdev.org/downloads/category/22-datfiles";

        using var req = new HttpRequestMessage(HttpMethod.Get, listUrl);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");

        using var resp = await ProviderHelpers.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var html = await resp.Content.ReadAsStringAsync(ct);
        return ParseTosecCategoryPage(html);
    }

    // Matches: pd-subcategory"><a href="/downloads/category/59-2025-03-13">2025-03-13</a>
    private static readonly Regex SubcategoryPattern = new(
        @"pd-subcategory""><a href=""(/downloads/category/\d+-([\d-]+))"">",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<TosecEntry> ParseTosecCategoryPage(string html)
    {
        var entries = new List<TosecEntry>();

        foreach (Match m in SubcategoryPattern.Matches(html))
        {
            var categoryUrl = m.Groups[1].Value;
            var date        = m.Groups[2].Value;
            entries.Add(new TosecEntry(categoryUrl, $"TOSEC Complete Pack  ·  {date}", date));
        }

        return entries.OrderByDescending(e => e.Date).ToList();
    }

    // Matches: href="/downloads/category/59-2025-03-13?download=117:tosec-..."
    private static readonly Regex DownloadLinkPattern = new(
        @"href=""(/downloads/category/[^""]+\?download=[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static async Task<string> ResolveDownloadUrlAsync(TosecEntry entry, CancellationToken ct)
    {
        var pageUrl = $"https://www.tosecdev.org{entry.CategoryUrl}";

        using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");

        using var resp = await ProviderHelpers.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var html = await resp.Content.ReadAsStringAsync(ct);

        var m = DownloadLinkPattern.Match(html);
        if (!m.Success)
            throw new InvalidOperationException($"No download link found on page for {entry.Label}.");

        return $"https://www.tosecdev.org{m.Groups[1].Value}";
    }

    private static async Task<(int Extracted, int Skipped)> DownloadTosecPackAsync(
        TosecEntry                                         entry,
        string                                             outputDir,
        IProgress<(string Name, long Received, long Total)> progress,
        CancellationToken                                  ct)
    {
        var downloadUrl = await ResolveDownloadUrlAsync(entry, ct);
        ct.ThrowIfCancellationRequested();

        var tempFile = Path.GetTempFileName();

        try
        {
            using (var resp = await ProviderHelpers.Http.GetAsync(
                       downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
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
                    progress.Report((entry.Label, received, total));
                }
            }

            ct.ThrowIfCancellationRequested();
            return ProviderHelpers.ExtractTosecArchive(tempFile, outputDir);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        CancelButton.IsVisible  = busy;

        if (!busy) ResetTosecProgress();

        UpdateDownloadButtonState();
    }

    private void SetTosecProgress(string name, long received, long total)
    {
        TosecProgressLabel.Text = name;

        if (total > 0)
        {
            TosecDownloadProgress.IsIndeterminate = false;
            TosecDownloadProgress.Value = (double)received / total * 100.0;
            TosecProgressDetail.Text   = $"{received / 1024:N0} KB / {total / 1024:N0} KB";
        }
        else if (received > 0)
        {
            TosecDownloadProgress.IsIndeterminate = true;
            TosecProgressDetail.Text = $"{received / 1024:N0} KB";
        }
        else
        {
            TosecDownloadProgress.IsIndeterminate = true;
            TosecProgressDetail.Text = "";
        }
    }

    private void ResetTosecProgress()
    {
        TosecProgressLabel.Text               = "";
        TosecProgressDetail.Text              = "";
        TosecDownloadProgress.IsIndeterminate = false;
        TosecDownloadProgress.Value           = 0;
    }

    private void AppendLog(string text, string color = "#888899") =>
        ProviderHelpers.AppendLog(TosecLogPanel, TosecLogScrollViewer, text, color);
}

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

public partial class RedumpProviderWindow : Window
{
    private sealed record RedumpEntry(string Id, string Name, string DownloadUrl);

    private readonly List<(Border Row, CheckBox Box, RedumpEntry Entry)> _rows = [];
    private CancellationTokenSource? _cts;

    public RedumpProviderWindow() { InitializeComponent(); }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnRedumpSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var q = RedumpSearch.Text?.Trim() ?? "";
        int visible = 0;
        foreach (var (row, _, entry) in _rows)
        {
            bool show = q.Length == 0 ||
                        entry.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
            row.IsVisible = show;
            if (show) visible++;
        }
        SystemCountLabel.Text = visible == _rows.Count
            ? $"{_rows.Count} systems"
            : $"{visible} / {_rows.Count} systems";
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
        RedumpSelectedCount.Text = n == 0 ? "0 selected" : $"{n} selected";
        UpdateDownloadButtonState();
    }

    private void UpdateDownloadButtonState()
    {
        bool busy = CancelButton.IsVisible;
        DownloadButton.IsEnabled = !busy && _rows.Any(r => r.Box.IsChecked == true);
    }

    // ── Footer button handlers ────────────────────────────────────────────────

    private void OnRefreshList(object? sender, RoutedEventArgs e) => RefreshRedumpAsync();

    private void OnDownloadSelected(object? sender, RoutedEventArgs e) => DownloadRedumpSelectedAsync();

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

    private async void RefreshRedumpAsync()
    {
        SetBusy(true);
        SystemsPanel.Children.Clear();
        _rows.Clear();
        SystemCountLabel.Text    = "";
        RedumpStatusText.Text    = "Loading system list…";
        RedumpSelectedCount.Text = "0 selected";

        using var cts = new CancellationTokenSource();
        _cts = cts;

        AppendLog("Fetching Redump system list from redump.org…");

        try
        {
            var entries = await FetchRedumpSystemsAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            BuildRedumpRows(entries);
            RedumpStatusText.Text = $"{entries.Count} systems available.";
            SystemCountLabel.Text = $"{entries.Count} systems";
            AppendLog($"Loaded {entries.Count} systems.", "#4CAF50");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Refresh cancelled.", "#888899");
            RedumpStatusText.Text = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Error fetching list: {ex.Message}", "#EF5350");
            RedumpStatusText.Text = "Failed to load. Check network connection and try again.";
        }
        finally
        {
            SetBusy(false);
            _cts = null;
        }
    }

    private void BuildRedumpRows(List<RedumpEntry> entries)
    {
        foreach (var entry in entries)
        {
            var cb = new CheckBox
            {
                Content    = entry.Name,
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

            SystemsPanel.Children.Add(row);
            _rows.Add((row, cb, entry));
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async void DownloadRedumpSelectedAsync()
    {
        var selected = _rows
            .Where(r => r.Box.IsChecked == true)
            .Select(r => r.Entry)
            .ToList();

        if (selected.Count == 0) return;

        SetBusy(true);

        var outputDir = ProviderHelpers.GetRedumpOutputDir();
        Directory.CreateDirectory(outputDir);

        var relativeOut = Path.GetRelativePath(AppContext.BaseDirectory, outputDir);
        AppendLog($"Starting download — {selected.Count} system(s)");
        AppendLog($"Output: {relativeOut}");

        using var cts = new CancellationTokenSource();
        _cts = cts;

        int succeeded = 0, failed = 0;

        for (int i = 0; i < selected.Count; i++)
        {
            if (cts.IsCancellationRequested) break;

            var entry = selected[i];
            AppendLog($"[{i + 1}/{selected.Count}]  Downloading: {entry.Name}…");
            SetProgress(entry.Name, 0, -1);

            try
            {
                string? savedPath = null;

                var progressReporter = new Progress<(string Name, long Received, long Total)>(p =>
                    SetProgress(p.Name, p.Received, p.Total));

                await Task.Run(async () =>
                {
                    savedPath = await DownloadRedumpDatAsync(
                        entry, outputDir, progressReporter, cts.Token);
                }, cts.Token);

                AppendLog(
                    $"     Saved: {Path.GetFileName(savedPath ?? entry.Name + ".dat")}",
                    "#4CAF50");
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                AppendLog($"     Cancelled: {entry.Name}", "#888899");
                break;
            }
            catch (Exception ex)
            {
                AppendLog($"     Failed: {entry.Name}  —  {ex.Message}", "#EF5350");
                failed++;
            }
        }

        ResetProgress();
        bool wasCancelled = cts.IsCancellationRequested;
        AppendLog(
            $"Done.  Succeeded: {succeeded}  Failed: {failed}" +
            (wasCancelled ? "  (cancelled)" : ""),
            "#7B68EE");

        SetBusy(false);
        _cts = null;
    }

    // ── Network / parsing ─────────────────────────────────────────────────────

    private static async Task<List<RedumpEntry>> FetchRedumpSystemsAsync(CancellationToken ct)
    {
        const string url = "https://old.redump.info/downloads";
        using var resp = await ProviderHelpers.Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ParseRedumpSystems(html);
    }

    private static List<RedumpEntry> ParseRedumpSystems(string html)
    {
        var entries = new List<RedumpEntry>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rowRx  = new Regex(@"<tr[^>]*>(.*?)</tr>",                  RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var nameRx = new Regex(@"<td[^>]*>\s*([^<]+?)\s*</td>",        RegexOptions.IgnoreCase);
        var hrefRx = new Regex(@"href=""([^""]*?/datfile/[^""]+?)""",   RegexOptions.IgnoreCase);

        foreach (Match row in rowRx.Matches(html))
        {
            var inner = row.Groups[1].Value;

            var hrefMatch = hrefRx.Match(inner);
            if (!hrefMatch.Success) continue;

            var nameMatch = nameRx.Match(inner);
            if (!nameMatch.Success) continue;

            var rawHref     = hrefMatch.Groups[1].Value.Trim();
            var downloadUrl = rawHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? rawHref
                : "https://old.redump.info" + rawHref;

            var id   = rawHref.TrimEnd('/').Split('/').Last();
            var name = System.Net.WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());

            if (id.Length > 0 && name.Length > 0 && seen.Add(id))
                entries.Add(new RedumpEntry(id, name, downloadUrl));
        }

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<string> DownloadRedumpDatAsync(
        RedumpEntry                                        entry,
        string                                             outputDir,
        IProgress<(string Name, long Received, long Total)> progress,
        CancellationToken                                  ct)
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            using (var resp = await ProviderHelpers.Http.GetAsync(
                       entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
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
                    progress.Report((entry.Name, received, total));
                }
            }

            ct.ThrowIfCancellationRequested();
            return ProviderHelpers.ExtractDatFromZip(tempFile, outputDir);
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

        if (!busy) ResetProgress();

        UpdateDownloadButtonState();
    }

    private void SetProgress(string name, long received, long total)
    {
        ProgressLabel.Text = name;

        if (total > 0)
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = (double)received / total * 100.0;
            ProgressDetail.Text   = $"{received / 1024:N0} KB / {total / 1024:N0} KB";
        }
        else if (received > 0)
        {
            DownloadProgress.IsIndeterminate = true;
            ProgressDetail.Text = $"{received / 1024:N0} KB";
        }
        else
        {
            DownloadProgress.IsIndeterminate = true;
            ProgressDetail.Text = "";
        }
    }

    private void ResetProgress()
    {
        ProgressLabel.Text               = "";
        ProgressDetail.Text              = "";
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value           = 0;
    }

    private void AppendLog(string text, string color = "#888899") =>
        ProviderHelpers.AppendLog(LogPanel, LogScrollViewer, text, color);
}

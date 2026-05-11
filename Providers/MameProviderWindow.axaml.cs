using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Arkadia;

public partial class MameProviderWindow : Window
{
    private sealed record MameRelease(
        string TagName,
        string DisplayLabel,
        string AssetName,
        string DownloadUrl,
        string PublishedDate,
        string HtmlUrl);

    private readonly List<(Border Row, CheckBox Box, MameRelease Entry)> _rows = [];
    private MameRelease?             _selected;
    private bool                     _updatingSelection;
    private CancellationTokenSource? _cts;
    private readonly CatalogService? _catalogService;
    private bool                     _categoryIniPromptShown;

    public MameProviderWindow() { InitializeComponent(); }

    public MameProviderWindow(CatalogService catalog) : this()
    {
        _catalogService = catalog;
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnMameSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var q = MameSearch.Text?.Trim() ?? "";
        int visible = 0;
        foreach (var (row, _, entry) in _rows)
        {
            bool show = q.Length == 0 ||
                        entry.DisplayLabel.Contains(q, StringComparison.OrdinalIgnoreCase);
            row.IsVisible = show;
            if (show) visible++;
        }
        MameCountLabel.Text = visible == _rows.Count
            ? $"{_rows.Count} releases"
            : $"{visible} / {_rows.Count} releases";
    }

    // ── Footer handlers ───────────────────────────────────────────────────────

    private void OnRefreshList(object? sender, RoutedEventArgs e)    => RefreshReleasesAsync();
    private void OnDownloadAndCache(object? sender, RoutedEventArgs e) => DownloadAndCacheAsync();

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

    private async void OnOpenPlaylistGenerator(object? sender, RoutedEventArgs e) =>
        await new MamePlaylistWindow().ShowDialog(this);

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async void RefreshReleasesAsync()
    {
        SetBusy(true);
        MameReleasesPanel.Children.Clear();
        _rows.Clear();
        _selected = null;
        MameCountLabel.Text  = "";
        MameStatusText.Text  = "Loading release list…";
        MameSelectedInfo.Text = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;

        AppendLog("Fetching MAME releases from GitHub…");

        try
        {
            var releases = await FetchMameReleasesAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            if (releases.Count == 0)
            {
                MameStatusText.Text = "No Windows x64 binary assets found. " +
                    "MAME may have changed release naming — see log for details.";
                MameCountLabel.Text = "0 releases";
                AppendLog("No Windows x64 binary assets matched (mame####b_x64.exe). " +
                          "Check log above for the HTTP status and raw asset names.", "#FFA726");
                return;
            }

            BuildRows(releases);
            MameStatusText.Text  = $"{releases.Count} release(s) available.  Select one and click Download & Cache.";
            MameCountLabel.Text  = $"{releases.Count} releases";
            AppendLog($"Loaded {releases.Count} release(s).", "#4CAF50");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Refresh cancelled.", "#888899");
            MameStatusText.Text = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Error fetching release list: {ex.Message}", "#EF5350");
            MameStatusText.Text = "Failed to load. Check network connection and try again.";
        }
        finally
        {
            SetBusy(false);
            _cts = null;
        }
    }

    private void BuildRows(List<MameRelease> releases)
    {
        foreach (var release in releases)
        {
            var entry = release;

            var cb = new CheckBox
            {
                Content    = entry.DisplayLabel,
                FontSize   = 12,
                Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                Padding    = new Avalonia.Thickness(12, 5, 12, 5),
            };

            cb.IsCheckedChanged += (_, _) =>
            {
                if (_updatingSelection) return;

                if (cb.IsChecked == true)
                {
                    _updatingSelection = true;
                    try
                    {
                        foreach (var (_, other, _) in _rows)
                            if (!ReferenceEquals(other, cb))
                                other.IsChecked = false;
                        _selected = entry;
                    }
                    finally { _updatingSelection = false; }
                }
                else
                {
                    if (ReferenceEquals(_selected, entry))
                        _selected = null;
                }

                UpdateSelectedInfo();
                UpdateDownloadButtonState();
            };

            var row = new Border
            {
                Child           = cb,
                BorderBrush     = new SolidColorBrush(Color.Parse("#141420")),
                BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            };

            MameReleasesPanel.Children.Add(row);
            _rows.Add((row, cb, entry));
        }
    }

    // ── Download & Cache pipeline ─────────────────────────────────────────────

    private async void DownloadAndCacheAsync()
    {
        if (_selected is null) return;

        var release = _selected;

        // ── Preflight: 7z must be available before anything else ─────────────
        var sevenZip = FindSevenZip();
        if (sevenZip is null)
        {
            AppendLog("7z not found. MAME SFX extraction requires 7z.", "#EF5350");
            await ShowMissingSevenZipDialog();
            return; // no download, no temp files created
        }
        AppendLog($"7z: {sevenZip}");

        // ── Preliminary version from release metadata ─────────────────────────
        var prelimVersion = DeriveVersionFromString(release.AssetName)
                         ?? DeriveVersionFromString(release.TagName);
        if (prelimVersion is null)
        {
            AppendLog("Cannot determine preliminary version from asset name or tag. Aborting.", "#EF5350");
            return;
        }
        AppendLog($"Preliminary version: MAME {prelimVersion}");

        SetBusy(true);

        using var cts = new CancellationTokenSource();
        _cts = cts;

        string? tempZip     = null;
        string? newCacheDir = null;

        try
        {
            var cacheRoot = ProviderHelpers.GetMameCacheRootDir();
            var cacheDir  = Path.Combine(cacheRoot, prelimVersion);

            // ── Cache safety check ────────────────────────────────────────────
            if (File.Exists(Path.Combine(cacheDir, "meta.json")))
            {
                AppendLog(
                    $"MAME {prelimVersion} already cached at " +
                    $"{Path.GetRelativePath(AppContext.BaseDirectory, cacheDir)}. Skipping.", "#FFA726");
                MameStatusText.Text = $"MAME {prelimVersion} is already cached.";
                return;
            }

            // ── Step 1: Download ──────────────────────────────────────────────
            AppendLog($"Downloading {release.AssetName}…");
            tempZip = Path.GetTempFileName();

            var dlProgress = new Progress<(string Name, long Received, long Total)>(p =>
                SetProgress(p.Name, p.Received, p.Total));
            await DownloadToTempAsync(release, tempZip, dlProgress, cts.Token);
            AppendLog("Download complete.", "#4CAF50");

            // ── Step 2: Extract with 7z directly into the versioned cache dir ─
            Directory.CreateDirectory(cacheDir);
            newCacheDir = cacheDir;

            AppendLog($"Extracting into {Path.GetRelativePath(AppContext.BaseDirectory, cacheDir)}…");
            SetProgressBusy("Extracting with 7z…");
            await ExtractWith7zAsync(sevenZip, tempZip, cacheDir, cts.Token);
            AppendLog("Extraction complete.", "#4CAF50");

            // Temp archive is no longer needed
            try { File.Delete(tempZip); } catch { }
            tempZip = null;

            // ── Step 3: Locate extracted MAME binary ──────────────────────────
            var binaryPath = FindMameBinary(cacheDir)
                ?? throw new InvalidOperationException(
                    "No MAME binary found after 7z extraction. " +
                    "The archive may not have extracted correctly.");
            var binaryName = Path.GetFileName(binaryPath);
            AppendLog($"Found binary: {binaryName}");

            // ── Step 4: Verify version matches preliminary ────────────────────
            AppendLog("Running mame -version…");
            SetProgressBusy("Verifying version…");
            var actualVersion = await DetectVersionAsync(binaryPath, release, cts.Token)
                ?? throw new InvalidOperationException(
                    "Could not determine MAME version from extracted binary.");
            AppendLog($"Binary reports: MAME {actualVersion}", "#4CAF50");

            if (!string.Equals(actualVersion, prelimVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Version mismatch: preliminary was {prelimVersion}, " +
                    $"binary reports {actualVersion}. Incomplete cache will be removed.");

            AppendLog("Version verified.", "#4CAF50");

            // ── Step 5: Generate listxml ──────────────────────────────────────
            AppendLog("Generating listxml — this will take several minutes…", "#FFA726");
            SetProgressBusy("Generating listxml…");
            var listxmlPath = Path.Combine(cacheDir, "listxml.xml");
            await GenerateListXmlAsync(binaryPath, listxmlPath, cts.Token);
            AppendLog("listxml.xml written.", "#4CAF50");

            // ── Step 6: Count machines ────────────────────────────────────────
            AppendLog("Counting machine entries…");
            SetProgressBusy("Counting machines…");
            long machineCount = await CountMachinesAsync(listxmlPath, cts.Token);
            AppendLog($"Machine count: {machineCount:N0}");

            // ── Step 7: Write meta.json (last — marks cache as valid) ─────────
            WriteMeta(cacheDir, actualVersion, binaryName, machineCount, listxmlPath, release);
            newCacheDir = null; // success — do not clean up

            var relOut = Path.GetRelativePath(AppContext.BaseDirectory, cacheDir);
            AppendLog($"Cache complete: {relOut}", "#7B68EE");
            MameStatusText.Text = $"MAME {actualVersion} cached successfully.";

            await PromptCategoryIniIfNeededAsync();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Operation cancelled.", "#888899");
            MameStatusText.Text = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"Failed: {ex.Message}", "#EF5350");
            MameStatusText.Text = "Operation failed. See log for details.";
        }
        finally
        {
            if (tempZip is not null)     try { File.Delete(tempZip); }                catch { }
            if (newCacheDir is not null) try { Directory.Delete(newCacheDir, true); } catch { }
            ResetProgress();
            SetBusy(false);
            _cts = null;
        }
    }

    // ── Category.ini prompt (post-cache) ─────────────────────────────────────

    private async Task PromptCategoryIniIfNeededAsync()
    {
        if (_categoryIniPromptShown) return;
        _categoryIniPromptShown = true;

        if (File.Exists(ProviderHelpers.MameCategoryIniPath)) return;

        var dlg = new CategoryIniPromptDialog();
        await dlg.ShowDialog(this);

        if (!dlg.ShouldDownload) return;

        AppendLog("Downloading category.ini…");
        bool ok = await ProviderHelpers.DownloadCategoryIniAsync(AppendLog);
        if (!ok)
            AppendLog("category.ini download failed. You can retry later from the MAME Playlist window.", "#FFA726");
    }

    // ── 7z preflight ─────────────────────────────────────────────────────────

    private string? FindSevenZip()
    {
        // 1. Tool system (primary — same registry as Transforms)
        if (_catalogService is not null)
        {
            try
            {
                var record = _catalogService.LoadTools()
                    .FirstOrDefault(t => t.Id == "7zip");

                if (record is not null)
                {
                    var toolPath = Path.Combine(
                        AppContext.BaseDirectory, "tools",
                        record.FolderName, record.ExecutableName);

                    if (File.Exists(toolPath))
                    {
                        AppendLog($"7z resolved from Tools: {toolPath}");
                        return toolPath;
                    }

                    AppendLog(
                        $"7z record found in Tools but file missing: {toolPath}", "#FFA726");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Tool system lookup failed: {ex.Message}", "#888899");
            }
        }

        // 2. Bundled tools\7zip\7zip.exe (direct path, no DB needed)
        var bundled = ProviderHelpers.Find7zip();
        if (bundled is not null)
        {
            AppendLog($"7z resolved from bundled tools: {bundled}");
            return bundled;
        }

        // 3. 7z.exe anywhere on PATH (system install)
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "7z.exe");
            if (File.Exists(candidate))
            {
                AppendLog($"7z resolved from PATH: {candidate}");
                return candidate;
            }
        }

        // 4. Default 7-Zip installation paths on Windows (system install)
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "7-Zip", "7z.exe");
            if (File.Exists(candidate))
            {
                AppendLog($"7z resolved from Program Files: {candidate}");
                return candidate;
            }
        }

        return null;
    }

    private async Task ShowMissingSevenZipDialog()
    {
        var ok = new Button
        {
            Content                 = "OK",
            Background              = new SolidColorBrush(Color.Parse("#242433")),
            Foreground              = new SolidColorBrush(Color.Parse("#F0F0F0")),
            BorderThickness         = new Avalonia.Thickness(0),
            CornerRadius            = new Avalonia.CornerRadius(5),
            Padding                 = new Avalonia.Thickness(28, 7),
            FontSize                = 12,
            HorizontalAlignment     = HorizontalAlignment.Center,
            Margin                  = new Avalonia.Thickness(0, 14, 0, 0),
        };

        var dlg = new Window
        {
            Title                 = "7z Required",
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = new SolidColorBrush(Color.Parse("#0F0F14")),
            FontFamily            = new FontFamily("Inter,Segoe UI,sans-serif"),
            Content               = new StackPanel
            {
                Margin   = new Avalonia.Thickness(28, 24, 28, 24),
                Spacing  = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text       = "MAME extraction requires 7z.",
                        FontSize   = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#F0F0F0")),
                    },
                    new TextBlock
                    {
                        Text       = "Configure or install 7z first.",
                        FontSize   = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#AAAABC")),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    ok,
                },
            },
        };

        ok.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
    }

    // ── 7z extraction ─────────────────────────────────────────────────────────

    private static async Task ExtractWith7zAsync(
        string sevenZipPath, string archivePath, string outputDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(sevenZipPath)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        // x  = extract with full paths
        // -o = output directory (no space between flag and path)
        // -y = assume Yes to all prompts (non-interactive)
        // -bso0 = suppress standard output stream
        // -bsp0 = suppress progress output stream
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add($"-o{outputDir}");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-bso0");
        psi.ArgumentList.Add("-bsp0");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var proc       = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 7z process.");

        using var ctReg  = ct.Register(
            () => { try { proc.Kill(entireProcessTree: true); } catch { } });
        using var tmrReg = timeoutCts.Token.Register(
            () => { try { proc.Kill(entireProcessTree: true); } catch { } });

        // Drain both streams concurrently to avoid buffer deadlocks
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(CancellationToken.None);

        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
        {
            var stderr = stderrTask.Result.Trim();
            var detail = stderr.Length > 0
                ? stderr[..Math.Min(300, stderr.Length)]
                : $"exit code {proc.ExitCode}";
            throw new InvalidOperationException($"7z extraction failed: {detail}");
        }
    }

    // ── Network / GitHub API ──────────────────────────────────────────────────

    private async Task<List<MameRelease>> FetchMameReleasesAsync(CancellationToken ct)
    {
        const string apiUrl = "https://api.github.com/repos/mamedev/mame/releases?per_page=30";

        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await ProviderHelpers.Http.SendAsync(req, ct);

        var statusCode = (int)resp.StatusCode;
        var json       = await resp.Content.ReadAsStringAsync(ct);

        AppendLog($"GitHub API: HTTP {statusCode}  ·  {json.Length:N0} bytes received");

        if (!resp.IsSuccessStatusCode)
        {
            var preview = json.Length > 200 ? json[..200] : json;
            AppendLog($"Response: {preview}", "#EF5350");

            if (statusCode is 403 or 429)
                AppendLog("Likely GitHub API rate limit (60 req/h unauthenticated). " +
                          "Wait a few minutes and retry.", "#FFA726");

            resp.EnsureSuccessStatusCode();
        }

        return ParseMameReleases(json);
    }

    // MAME release asset naming (as of 2024+):
    //   mame0287b_x64.exe   — Windows x64 binary  ← we want this
    //   mame0287b_arm64.exe — Windows ARM64 binary
    //   mame0287lx.zip      — Linux build
    //   mame0287s.exe       — Source self-extractor
    private List<MameRelease> ParseMameReleases(string json)
    {
        var releases = new List<MameRelease>();

        using var doc  = JsonDocument.Parse(json);
        var       root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            var preview = json.Length > 200 ? json[..200] : json;
            AppendLog($"Unexpected JSON root ({root.ValueKind}). Preview: {preview}", "#EF5350");
            return releases;
        }

        int totalEntries = root.GetArrayLength();

        foreach (var rel in root.EnumerateArray())
        {
            string tagName = rel.TryGetProperty("tag_name", out var t)  ? t.GetString() ?? "" : "";
            string htmlUrl = rel.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";

            string date = "";
            if (rel.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String)
                if (DateTime.TryParse(pub.GetString(), out var dt))
                    date = dt.ToString("yyyy-MM-dd");

            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                string assetName = asset.TryGetProperty("name",                 out var an) ? an.GetString() ?? "" : "";
                string dlUrl     = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(assetName) || string.IsNullOrEmpty(dlUrl)) continue;

                // Match Windows x64 binary: mame####b_x64.exe
                // Exclude arm64, source (.../s.exe), and Linux (lx.zip)
                if (!assetName.EndsWith("b_x64.exe", StringComparison.OrdinalIgnoreCase)) continue;

                var ver   = DeriveVersionFromString(tagName) ?? tagName;
                var label = string.IsNullOrEmpty(date)
                    ? $"MAME {ver}  ·  {assetName}"
                    : $"MAME {ver}  ·  {date}  ·  {assetName}";

                releases.Add(new MameRelease(tagName, label, assetName, dlUrl, date, htmlUrl));
                break; // one x64 asset per release
            }
        }

        AppendLog($"Releases in response: {totalEntries}  ·  Windows x64 matched: {releases.Count}");
        return releases;
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private static async Task DownloadToTempAsync(
        MameRelease                                        release,
        string                                             tempPath,
        IProgress<(string Name, long Received, long Total)> progress,
        CancellationToken                                  ct)
    {
        using var resp = await ProviderHelpers.Http.GetAsync(
            release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1L;
        using var netStream  = await resp.Content.ReadAsStreamAsync(ct);
        using var fileStream = File.Create(tempPath);

        var  buffer   = new byte[65536];
        long received = 0;
        int  read;
        while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress.Report((release.AssetName, received, total));
        }
    }

    // ── Extraction ────────────────────────────────────────────────────────────

    private static void ExtractMameArchive(string archivePath, string targetDir)
    {
        var fullDestRoot = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;  // directory entry

            var relNorm  = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.GetFullPath(Path.Combine(targetDir, relNorm));
            if (!destPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using var src  = entry.Open();
            using var dest = File.Create(destPath);
            src.CopyTo(dest);
        }
    }

    // ── Binary discovery ──────────────────────────────────────────────────────

    private static string? FindMameBinary(string dir)
    {
        var mameExe = Path.Combine(dir, "mame.exe");
        if (File.Exists(mameExe)) return mameExe;

        var mame64Exe = Path.Combine(dir, "mame64.exe");
        if (File.Exists(mame64Exe)) return mame64Exe;

        // Fallback: any mame*.exe at root level
        return Directory
            .GetFiles(dir, "mame*.exe", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    // ── Version detection ─────────────────────────────────────────────────────

    private static async Task<string?> DetectVersionAsync(
        string binaryPath, MameRelease release, CancellationToken ct)
    {
        // Primary: mame -version
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var psi = new ProcessStartInfo(binaryPath, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = Path.GetDirectoryName(binaryPath)!,
            };

            using var proc = Process.Start(psi)!;
            using var _    = linked.Token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();

            var m = Regex.Match(stdout.Trim(), @"^v(\d+\.\d+)", RegexOptions.Multiline);
            if (m.Success) return m.Groups[1].Value;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to filename fallbacks */ }

        // Fallback 1: archive filename  e.g. mame0274b_64bit.zip → 0.274
        var fnMatch = Regex.Match(release.AssetName, @"mame(\d{4})", RegexOptions.IgnoreCase);
        if (fnMatch.Success)
        {
            var d = fnMatch.Groups[1].Value;
            return $"{d[0]}.{d[1..]}";
        }

        // Fallback 2: tag name  e.g. mame0274 → 0.274
        var tagMatch = Regex.Match(release.TagName, @"mame(\d{4})", RegexOptions.IgnoreCase);
        if (tagMatch.Success)
        {
            var d = tagMatch.Groups[1].Value;
            return $"{d[0]}.{d[1..]}";
        }

        return null;
    }

    private static string? DeriveVersionFromString(string s)
    {
        var m = Regex.Match(s, @"mame(\d{4})", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var d = m.Groups[1].Value;
        return $"{d[0]}.{d[1..]}";
    }

    // ── listxml generation ────────────────────────────────────────────────────

    private static async Task GenerateListXmlAsync(
        string binaryPath, string listxmlPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(binaryPath, "-listxml")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = Path.GetDirectoryName(binaryPath)!,
        };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var proc       = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MAME process for -listxml.");

        // Cancellation or timeout kills the process, which closes stdout and ends CopyToAsync
        using var ctReg  = ct.Register(
            () => { try { proc.Kill(entireProcessTree: true); } catch { } });
        using var tmrReg = timeoutCts.Token.Register(
            () => { try { proc.Kill(entireProcessTree: true); } catch { } });

        try
        {
            using var outFile = File.Create(listxmlPath);
            var stderrTask    = proc.StandardError.ReadToEndAsync();
            await proc.StandardOutput.BaseStream.CopyToAsync(outFile, CancellationToken.None);
            await proc.WaitForExitAsync(CancellationToken.None);

            ct.ThrowIfCancellationRequested();
            timeoutCts.Token.ThrowIfCancellationRequested();

            if (proc.ExitCode != 0)
            {
                var stderr = await stderrTask;
                throw new InvalidOperationException(
                    $"mame -listxml exited with code {proc.ExitCode}. {stderr.Trim()}");
            }
        }
        catch
        {
            try { File.Delete(listxmlPath); } catch { }
            throw;
        }
    }

    // ── Machine count ─────────────────────────────────────────────────────────

    private static async Task<long> CountMachinesAsync(string listxmlPath, CancellationToken ct)
    {
        long count = 0;
        using var reader = new StreamReader(listxmlPath);
        string?   line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            if (line.Contains("<machine ", StringComparison.Ordinal))
                count++;
        return count;
    }

    // ── meta.json ─────────────────────────────────────────────────────────────

    private static void WriteMeta(
        string      cacheDir,
        string      version,
        string      binaryName,
        long        machineCount,
        string      listxmlPath,
        MameRelease release)
    {
        var meta = new
        {
            version,
            versionRaw          = release.TagName,
            acquiredUtc         = DateTime.UtcNow,
            sourceUrl           = release.HtmlUrl,
            archiveName         = release.AssetName,
            binaryName,
            listxmlGeneratedUtc = DateTime.UtcNow,
            machineCount,
            listxmlSizeBytes    = new FileInfo(listxmlPath).Length,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            meta, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(cacheDir, "meta.json"), json);
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void UpdateSelectedInfo()
    {
        MameSelectedInfo.Text = _selected is null
            ? ""
            : $"Selected: {_selected.DisplayLabel}";
    }

    private void UpdateDownloadButtonState()
    {
        bool busy = CancelButton.IsVisible;
        DownloadButton.IsEnabled = !busy && _selected is not null;
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        CancelButton.IsVisible  = busy;
        if (!busy) ResetProgress();
        UpdateDownloadButtonState();
    }

    private void SetProgress(string name, long received, long total)
    {
        MameProgressLabel.Text = name;

        if (total > 0)
        {
            MameDownloadProgress.IsIndeterminate = false;
            MameDownloadProgress.Value           = (double)received / total * 100.0;
            MameProgressDetail.Text              = $"{received / 1024:N0} KB / {total / 1024:N0} KB";
        }
        else if (received > 0)
        {
            MameDownloadProgress.IsIndeterminate = true;
            MameProgressDetail.Text              = $"{received / 1024:N0} KB";
        }
        else
        {
            MameDownloadProgress.IsIndeterminate = true;
            MameProgressDetail.Text              = "";
        }
    }

    private void SetProgressBusy(string label)
    {
        MameProgressLabel.Text               = label;
        MameProgressDetail.Text              = "";
        MameDownloadProgress.IsIndeterminate = true;
    }

    private void ResetProgress()
    {
        MameProgressLabel.Text               = "";
        MameProgressDetail.Text              = "";
        MameDownloadProgress.IsIndeterminate = false;
        MameDownloadProgress.Value           = 0;
    }

    private void AppendLog(string text, string color = "#888899") =>
        ProviderHelpers.AppendLog(MameLogPanel, MameLogScrollViewer, text, color);
}

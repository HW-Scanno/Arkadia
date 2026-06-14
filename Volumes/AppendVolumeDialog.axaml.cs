using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Volumes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Arkadia;

// ── Row view-models ───────────────────────────────────────────────────────────

public sealed class AppendPlanRow
{
    public string Action    { get; set; } = "";
    /// <summary>Raw action key for filter ("append-copy" or "append-skip").</summary>
    public string ActionKey { get; set; } = "";
    public string Release   { get; set; } = "";
    public string FileName  { get; set; } = "";
    public string SizeLabel { get; set; } = "";
    public string Reason    { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
}

public sealed class AppendProgressRow
{
    public string Action   { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Detail   { get; set; } = "";
}

// ── Dialog ────────────────────────────────────────────────────────────────────

public partial class AppendVolumeDialog : Window
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly AppendVolumePlan                          _plan;
    private readonly CatalogService                            _catalog;
    private          bool                                      _executing;

    private readonly List<AppendPlanRow>                      _allPlanRows  = [];
    private readonly ObservableCollection<AppendPlanRow>      _planRows     = [];
    private readonly ObservableCollection<AppendProgressRow>  _progressRows = [];

    private const int MaxProgressRows = 500;

    // ── Public result ─────────────────────────────────────────────────────────

    public AppendVolumeResult? ExecutionResult { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────

#pragma warning disable CS8618
    public AppendVolumeDialog() { InitializeComponent(); }
#pragma warning restore CS8618

    public AppendVolumeDialog(AppendVolumePlan plan, CatalogService catalog)
    {
        _plan    = plan;
        _catalog = catalog;

        InitializeComponent();

        PlanRowsList.ItemsSource     = _planRows;
        ProgressRowsList.ItemsSource = _progressRows;

        DisplayPlan(plan);
    }

    // ── Plan display ──────────────────────────────────────────────────────────

    private void DisplayPlan(AppendVolumePlan plan)
    {
        PlanVolumeLabel.Text = plan.VolumeLabel;
        PlanVolumeStats.Text =
            $"Capacity {FormatBytes(plan.TargetCapacityBytes)}  |  " +
            $"Used {FormatBytes(plan.TargetUsedBytes)}  |  " +
            $"Free {FormatBytes(plan.TargetFreeBytes)}";

        // Stats panel
        StatTotalDa.Text      = plan.TotalDerivedArtifactsForDatLine.ToString("N0");
        StatTotal.Text        = plan.TotalCandidates.ToString("N0");
        StatPlanned.Text      = $"{plan.PlannedCount:N0} file(s)";
        StatSkipped.Text      = plan.SkippedCount.ToString("N0");
        StatUnwanted.Text     = plan.ReleaseUnwantedSkipped.ToString("N0");
        StatAssigned.Text     = plan.AlreadyAssignedSkipped.ToString("N0");
        StatMissing.Text      = plan.ArchiveMissingSkipped.ToString("N0");
        StatCollision.Text    = plan.TargetCollisionSkipped.ToString("N0");
        StatFree.Text         = FormatBytes(plan.TargetFreeBytes);
        StatBytes.Text        = FormatBytes(plan.PlannedBytes);
        StatArchiveFiles.Text = plan.ActiveArchivePhysicalFileCount.ToString("N0");
        StatArchiveKnown.Text = plan.ActiveArchiveKnownWantedFileCount.ToString("N0");
        PlanStatsPanel.IsVisible = true;

        // Build all rows
        _allPlanRows.Clear();
        foreach (var entry in plan.Entries)
        {
            bool isCopy = entry.Action == AppendEntryAction.Copy;
            _allPlanRows.Add(new AppendPlanRow
            {
                Action     = isCopy ? "append-copy" : "append-skip",
                ActionKey  = isCopy ? "append-copy" : "append-skip",
                Release    = TruncateRelease(entry.ReleaseName),
                FileName   = entry.FileName,
                SizeLabel  = entry.SizeBytes > 0 ? FormatBytes(entry.SizeBytes) : "",
                Reason     = entry.Reason,
                SourcePath = entry.ArchivePath,
                TargetPath = entry.TargetPath,
            });
        }
        ApplyFilter();

        // Skip summary
        if (plan.SkipReasonCounts.Count > 0)
        {
            var lines = plan.SkipReasonCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}: {kv.Value}")
                .ToList();
            SkipSummaryText.Text       = "Skipped — " + string.Join("  |  ", lines);
            SkipSummaryPanel.IsVisible = true;
        }
        else
        {
            SkipSummaryPanel.IsVisible = false;
        }

        // Hint and button
        if (plan.CanExecute)
        {
            PlanHint.Text = $"Ready — {plan.PlannedCount} file(s) will be copied from archive to volume " +
                            $"({FormatBytes(plan.PlannedBytes)}).";
            ExecuteButton.IsEnabled = true;
        }
        else
        {
            PlanHint.Text = plan.DominantReasonHint.Length > 0
                ? plan.DominantReasonHint
                : "No append candidates found for this DAT line.";
            ExecuteButton.IsEnabled = false;
        }
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    private void OnFilterChanged(object? sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        bool showAll     = FilterAll.IsChecked     == true;
        bool showPlanned = FilterPlanned.IsChecked == true;
        bool showSkipped = FilterSkipped.IsChecked == true;

        _planRows.Clear();
        foreach (var row in _allPlanRows)
        {
            bool include = showAll
                || (showPlanned && row.ActionKey == "append-copy")
                || (showSkipped && row.ActionKey == "append-skip");
            if (include) _planRows.Add(row);
        }
    }

    // ── Execute ───────────────────────────────────────────────────────────────

    private async void OnExecute(object? sender, RoutedEventArgs e)
    {
        if (!_plan.CanExecute) return;

        _executing = true;

        PlanView.IsVisible      = false;
        ProgressView.IsVisible  = true;
        ExecuteButton.IsEnabled = false;

        ProgressHeader.Text = $"Appending to  {_plan.VolumeLabel}  —  {_plan.PlannedCount} file(s)";

        long totalBytes     = _plan.PlannedBytes;
        long copiedBytes    = 0;
        long verifiedBytes  = 0;
        int  filesComplete  = 0;
        var  start          = DateTime.UtcNow;

        var progressHandler = new Progress<AppendVolumeProgress>(ap =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_progressRows.Count >= MaxProgressRows)
                    _progressRows.RemoveAt(0);
                _progressRows.Add(new AppendProgressRow
                {
                    Action   = ap.Action,
                    FileName = ap.FileName,
                    Detail   = ap.Detail,
                });

                if (ap.Action == "append-copied")
                {
                    filesComplete++;
                    var entry = _plan.Entries.FirstOrDefault(
                        x => string.Equals(x.FileName, ap.FileName, StringComparison.OrdinalIgnoreCase)
                             && x.Action == AppendEntryAction.Copy);
                    if (entry is not null)
                    {
                        copiedBytes   += entry.SizeBytes;
                        verifiedBytes += entry.SizeBytes;
                    }
                    UpdateBars(copiedBytes, verifiedBytes, totalBytes);
                    UpdateStatusLine(filesComplete, _plan.PlannedCount, copiedBytes, totalBytes, start);
                }

                _ = Dispatcher.UIThread.InvokeAsync(
                    () => ProgressRowsScroll.ScrollToEnd(),
                    DispatcherPriority.Background);
            });
        });

        var svc = new AppendVolumeService(_catalog);

        AppendVolumeResult result;
        try
        {
            result = await Task.Run(() => svc.Execute(_plan, progressHandler));
        }
        catch (Exception ex)
        {
            result = new AppendVolumeResult { ErrorCount = 1, LogLines = [$"exception: {ex.Message}"] };
        }

        ExecutionResult = result;
        _executing      = false;

        Dispatcher.UIThread.Post(() =>
        {
            SetBarFill(CopiedBarTrack,   CopiedBarFill,   1.0);
            SetBarFill(VerifiedBarTrack, VerifiedBarFill, 1.0);
            CopiedBytesLabel.Text   = FormatBytes(result.BytesCopied);
            VerifiedBytesLabel.Text = FormatBytes(result.BytesCopied);

            if (result.ErrorCount == 0)
            {
                PhaseLabel.Text       = $"Done — {result.CopiedCount} file(s) copied and verified.";
                PhaseLabel.Foreground = new SolidColorBrush(Color.Parse("#81C784"));
            }
            else
            {
                PhaseLabel.Text       = $"Completed with {result.ErrorCount} error(s). {result.CopiedCount} file(s) copied.";
                PhaseLabel.Foreground = new SolidColorBrush(Color.Parse("#EF5350"));
            }

            CloseButton.IsEnabled = true;
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateBars(long copied, long verified, long total)
    {
        if (total <= 0) return;
        SetBarFill(CopiedBarTrack,   CopiedBarFill,   Math.Clamp(copied   / (double)total, 0, 1));
        SetBarFill(VerifiedBarTrack, VerifiedBarFill, Math.Clamp(verified / (double)total, 0, 1));
        CopiedBytesLabel.Text   = FormatBytes(copied);
        VerifiedBytesLabel.Text = FormatBytes(verified);
    }

    private void UpdateStatusLine(int done, int total, long bytes, long totalBytes, DateTime start)
    {
        var elapsed = DateTime.UtcNow - start;
        double speed = elapsed.TotalSeconds > 0.5
            ? bytes / elapsed.TotalSeconds / (1024.0 * 1024) : 0;
        ProgressStatusLine.Text =
            $"Files: {done:N0} / {total:N0}  |  " +
            $"Bytes: {FormatBytes(bytes)}  |  " +
            $"Speed: {speed:F1} MB/s  |  " +
            $"Elapsed: {elapsed:hh\\:mm\\:ss}";
    }

    private static void SetBarFill(Border track, Border fill, double fraction)
    {
        double w = track.Bounds.Width;
        if (w <= 0) return;
        fill.Width = Math.Max(0, w * Math.Clamp(fraction, 0, 1));
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (!_executing) Close(false);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_executing) e.Cancel = true;
        base.OnClosing(e);
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    private static string FormatBytes(long b)
    {
        if (b <= 0)                    return "0 B";
        if (b < 1024L)                 return $"{b} B";
        if (b < 1024L * 1024)          return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)   return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string TruncateRelease(string name)
    {
        const int max = 22;
        return name.Length <= max ? name : name[..max] + "…";
    }
}

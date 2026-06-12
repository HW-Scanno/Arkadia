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

public sealed class FillbackPlanRow
{
    public string Action    { get; set; } = "";
    public string Release   { get; set; } = "";
    public string FileName  { get; set; } = "";
    public string SizeLabel { get; set; } = "";
    public string Reason    { get; set; } = "";
}

public sealed class FillbackProgressRow
{
    public string Action   { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Detail   { get; set; } = "";
}

// ── Target candidate (volume + resolved root path) ────────────────────────────

public sealed class FillbackTargetCandidate
{
    public required string VolumeId    { get; init; }
    public required string VolumeLabel { get; init; }
    public required string RootPath    { get; init; }
    public required string DiskLabel   { get; init; }

    public override string ToString() => VolumeLabel;
}

// ── Dialog ────────────────────────────────────────────────────────────────────

public partial class FillbackVolumeDialog : Window
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly string                              _sourceVolumeId;
    private readonly string                              _sourceVolumeLabel;
    private readonly string                              _sourceRootPath;
    private readonly string                              _sourceDiskLabel;
    private readonly List<FillbackTargetCandidate>       _candidates;
    private readonly CatalogService                      _catalog;
    private readonly DatLineStore                        _store;

    private VolumeFillbackPlan?                          _currentPlan;
    private bool                                         _executing;

    private readonly ObservableCollection<FillbackPlanRow>     _planRows     = [];
    private readonly ObservableCollection<FillbackProgressRow> _progressRows = [];

    private const int MaxProgressRows = 500;

    // ── Public result ─────────────────────────────────────────────────────────

    public VolumeFillbackResult? ExecutionResult { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────

#pragma warning disable CS8618
    public FillbackVolumeDialog() { InitializeComponent(); }
#pragma warning restore CS8618

    public FillbackVolumeDialog(
        string                         sourceVolumeId,
        string                         sourceVolumeLabel,
        string                         sourceRootPath,
        string                         sourceDiskLabel,
        List<FillbackTargetCandidate>  candidates,
        CatalogService                 catalog,
        DatLineStore                   store)
    {
        _sourceVolumeId    = sourceVolumeId;
        _sourceVolumeLabel = sourceVolumeLabel;
        _sourceRootPath    = sourceRootPath;
        _sourceDiskLabel   = sourceDiskLabel;
        _candidates        = candidates;
        _catalog           = catalog;
        _store             = store;

        InitializeComponent();

        // Plan view — source header
        PlanSourceLabel.Text = sourceVolumeLabel;
        PlanSourceDisk.Text  = sourceDiskLabel.Length > 0 ? sourceDiskLabel : "";

        // Target ComboBox
        TargetComboBox.ItemsSource = candidates;
        if (candidates.Count == 0)
        {
            PlanHint.Text = "No valid target volumes are available for this source.";
            ExecuteButton.IsEnabled = false;
        }

        PlanRowsList.ItemsSource     = _planRows;
        ProgressRowsList.ItemsSource = _progressRows;
    }

    // ── Plan phase ────────────────────────────────────────────────────────────

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_executing) return;

        var candidate = TargetComboBox.SelectedItem as FillbackTargetCandidate;
        if (candidate is null)
        {
            ClearPlan();
            return;
        }

        BuildAndDisplayPlan(candidate);
    }

    private void BuildAndDisplayPlan(FillbackTargetCandidate candidate)
    {
        var source = _catalog.GetVolumeById(_sourceVolumeId);
        var target = _catalog.GetVolumeById(candidate.VolumeId);

        if (source is null || target is null)
        {
            ClearPlan();
            PlanHint.Text = "Volume record not found.";
            return;
        }

        var planner = new VolumeFillbackPlanner(_catalog);
        _currentPlan = planner.Plan(
            source, target,
            _sourceRootPath, candidate.RootPath,
            _sourceDiskLabel, candidate.DiskLabel,
            _store);

        DisplayPlan(_currentPlan);
    }

    private void DisplayPlan(VolumeFillbackPlan plan)
    {
        // Stats
        StatMode.Text        = plan.OperationMode == FillbackOperationMode.MoveSameDisk
                               ? "Move (same disk)" : "Copy → Verify → Delete (cross-disk)";
        StatTargetFree.Text  = FormatBytes(plan.TargetFreeBytes);
        StatPlanned.Text     = $"{FormatBytes(plan.PlannedBytes)} ({plan.PlannedCount} files)";
        StatRemaining.Text   = FormatBytes(plan.RemainingTargetFreeBytes);
        StatSourceAfter.Text = FormatBytes(plan.SourceBytesAfter);
        StatTargetAfter.Text = FormatBytes(plan.TargetBytesAfter);
        StatCount.Text       = plan.PlannedCount.ToString("N0");
        StatSkipped.Text     = plan.SkippedCount.ToString("N0");
        PlanStatsPanel.IsVisible = true;

        // Issues / warnings
        var allMessages = plan.Issues.Concat(plan.Warnings).ToList();
        if (allMessages.Count > 0)
        {
            IssuesText.Text     = string.Join("\n", allMessages);
            IssuesPanel.IsVisible = true;
        }
        else
        {
            IssuesPanel.IsVisible = false;
        }

        // Plan rows
        _planRows.Clear();
        foreach (var entry in plan.Entries)
        {
            _planRows.Add(new FillbackPlanRow
            {
                Action    = EntryActionLabel(entry.Action),
                Release   = TruncateRelease(entry.ReleaseName),
                FileName  = entry.ArtifactFileName,
                SizeLabel = entry.SizeBytes > 0 ? FormatBytes(entry.SizeBytes) : "",
                Reason    = entry.Reason,
            });
        }

        // Skip summary panel
        if (plan.SkipReasonCounts.Count > 0)
        {
            var lines = plan.SkipReasonCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}: {kv.Value} file(s)")
                .ToList();
            SkipSummaryText.Text     = "Skipped — " + string.Join("  |  ", lines);
            SkipSummaryPanel.IsVisible = true;
        }
        else
        {
            SkipSummaryPanel.IsVisible = false;
        }

        // Hint and button
        if (plan.CanExecute)
        {
            PlanHint.Text = plan.OperationMode == FillbackOperationMode.MoveSameDisk
                ? $"Ready — {plan.PlannedCount} file(s) will be moved on the same disk."
                : $"Ready — {plan.PlannedCount} file(s) will be copied, verified, and removed from source.";
            ExecuteButton.IsEnabled = true;
        }
        else if (plan.Issues.Count > 0)
        {
            PlanHint.Text           = "Cannot execute — resolve issues above.";
            ExecuteButton.IsEnabled = false;
        }
        else if (plan.PlannedCount == 0 && plan.SkipReasonCounts.Count > 0)
        {
            var dominant = plan.SkipReasonCounts.OrderByDescending(kv => kv.Value).First();
            var advice   = dominant.Key switch
            {
                VolumeFillbackPlanner.SkipReason.SourceFileMissing =>
                    "Run 'Verify Volume' on the source first to migrate files to flat layout.",
                VolumeFillbackPlanner.SkipReason.AlreadyOnTarget =>
                    "All files are already on the target volume.",
                VolumeFillbackPlanner.SkipReason.TooLargeForRemainingTargetSpace =>
                    "No single file fits in the remaining target free space.",
                VolumeFillbackPlanner.SkipReason.TargetCollision =>
                    "Target path conflicts — resolve collisions before executing.",
                _ => ""
            };
            PlanHint.Text = string.IsNullOrEmpty(advice)
                ? $"No files were planned. Dominant skip reason: {dominant.Key} ({dominant.Value})."
                : $"No files were planned. Dominant skip reason: {dominant.Key} ({dominant.Value}). {advice}";
            ExecuteButton.IsEnabled = false;
        }
        else
        {
            PlanHint.Text           = "Nothing to move (no planned entries).";
            ExecuteButton.IsEnabled = false;
        }
    }

    private void ClearPlan()
    {
        _currentPlan = null;
        _planRows.Clear();
        PlanStatsPanel.IsVisible   = false;
        IssuesPanel.IsVisible      = false;
        SkipSummaryPanel.IsVisible = false;
        ExecuteButton.IsEnabled    = false;
        PlanHint.Text              = "Select a target volume to build the plan.";
    }

    // ── Execute ───────────────────────────────────────────────────────────────

    private async void OnExecute(object? sender, RoutedEventArgs e)
    {
        if (_currentPlan is null || !_currentPlan.CanExecute) return;

        _executing = true;
        var plan   = _currentPlan;

        // Transition to progress view
        PlanView.IsVisible      = false;
        ProgressView.IsVisible  = true;
        ExecuteButton.IsEnabled = false;

        ProgressHeader.Text = plan.OperationMode == FillbackOperationMode.MoveSameDisk
            ? $"Moving  {plan.SourceVolumeLabel}  →  {plan.TargetVolumeLabel}"
            : $"Copying  {plan.SourceVolumeLabel}  →  {plan.TargetVolumeLabel}";

        BarMovedLabel.Text  = plan.OperationMode == FillbackOperationMode.MoveSameDisk
            ? "MOVED" : "COPIED";

        long totalBytes     = plan.PlannedBytes;
        long processedBytes = 0;
        int  filesComplete  = 0;
        var  start          = DateTime.UtcNow;

        var progressHandler = new Progress<FillbackProgress>(fp =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                // Append row
                if (_progressRows.Count >= MaxProgressRows)
                    _progressRows.RemoveAt(0);
                _progressRows.Add(new FillbackProgressRow
                {
                    Action   = fp.Action,
                    FileName = fp.FileName,
                    Detail   = fp.Detail,
                });

                // Update bars on terminal success actions
                if (fp.Action is "fillback-moved" or "fillback-copied-verified-deleted")
                {
                    filesComplete++;
                    // Find size from plan entry
                    var entry = plan.Entries.FirstOrDefault(
                        x => string.Equals(x.ArtifactFileName, fp.FileName, StringComparison.OrdinalIgnoreCase)
                             && x.Action is FillbackEntryAction.Move or FillbackEntryAction.CopyVerifyDelete);
                    if (entry is not null) processedBytes += entry.SizeBytes;

                    UpdateBars(processedBytes, totalBytes);
                    UpdateStatusLine(filesComplete, plan.PlannedCount, processedBytes, totalBytes, start);
                }

                _ = Dispatcher.UIThread.InvokeAsync(
                    () => ProgressRowsScroll.ScrollToEnd(),
                    DispatcherPriority.Background);
            });
        });

        var svc = new VolumeFillbackService(_catalog);

        VolumeFillbackResult result;
        try
        {
            result = await Task.Run(() => svc.Execute(plan, _store, progressHandler));
        }
        catch (Exception ex)
        {
            result = new VolumeFillbackResult { ErrorCount = 1, LogLines = [$"exception: {ex.Message}"] };
        }

        ExecutionResult = result;
        _executing      = false;

        // Final bar state
        Dispatcher.UIThread.Post(() =>
        {
            SetBarFill(MovedBarTrack,    MovedBarFill,    1.0);
            SetBarFill(VerifiedBarTrack, VerifiedBarFill, 1.0);
            MovedBytesLabel.Text    = FormatBytes(result.BytesMoved);
            VerifiedBytesLabel.Text = FormatBytes(result.BytesMoved);

            if (result.ErrorCount == 0)
            {
                var msg = result.SourceEmpty
                    ? $"Done — {result.MovedCount + result.CopiedCount} file(s) moved. Source volume is now empty."
                    : $"Done — {result.MovedCount + result.CopiedCount} file(s) moved.";
                if (result.SourceEmpty)
                    msg += "\nSource volume is now empty. You may Reabsorb or retire it separately.";
                PhaseLabel.Text       = msg;
                PhaseLabel.Foreground = new SolidColorBrush(Color.Parse("#81C784"));
            }
            else
            {
                PhaseLabel.Text       = $"Completed with {result.ErrorCount} error(s). " +
                                        $"{result.MovedCount + result.CopiedCount} file(s) moved.";
                PhaseLabel.Foreground = new SolidColorBrush(Color.Parse("#EF5350"));
            }

            CloseButton.IsEnabled = true;
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateBars(long processedBytes, long totalBytes)
    {
        if (totalBytes <= 0) return;
        double frac = Math.Clamp(processedBytes / (double)totalBytes, 0, 1);
        SetBarFill(MovedBarTrack,    MovedBarFill,    frac);
        SetBarFill(VerifiedBarTrack, VerifiedBarFill, frac);
        MovedBytesLabel.Text    = FormatBytes(processedBytes);
        VerifiedBytesLabel.Text = FormatBytes(processedBytes);
    }

    private void UpdateStatusLine(int filesComplete, int totalFiles,
        long processedBytes, long totalBytes, DateTime start)
    {
        var elapsed  = DateTime.UtcNow - start;
        double speed = elapsed.TotalSeconds > 0.5
            ? processedBytes / elapsed.TotalSeconds / (1024.0 * 1024) : 0;

        ProgressStatusLine.Text =
            $"Files: {filesComplete:N0} / {totalFiles:N0}  |  " +
            $"Bytes: {FormatBytes(processedBytes)}  |  " +
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

    private static string EntryActionLabel(FillbackEntryAction action) => action switch
    {
        FillbackEntryAction.Move             => "fillback-moving",
        FillbackEntryAction.CopyVerifyDelete => "fillback-copying",
        FillbackEntryAction.Skip             => "fillback-skip",
        FillbackEntryAction.Error            => "fillback-error",
        _                                    => "fillback-skip",
    };

    private static string TruncateRelease(string name)
    {
        const int max = 22;
        return name.Length <= max ? name : name[..max] + "…";
    }
}

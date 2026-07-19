using System.Collections.Generic;
using System.Collections.ObjectModel;
using Arkadia.Ingestion;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class IngestionProgressDialog : Window
{
    private bool _isRunning = true;

    private readonly List<IngestionOperation>              _allOps      = [];
    private readonly ObservableCollection<IngestionOperation> _filteredOps = [];

    public IngestionProgressDialog() : this("") { }

    public IngestionProgressDialog(string title)
    {
        InitializeComponent();
        OpTitle.Text        = title;
        OpsList.ItemsSource = _filteredOps;
    }

    // ── Progress update ───────────────────────────────────────────────────────

    public void Update(IngestionProgress p)
    {
        if (p.PhaseText.Length > 0)
            PhaseText.Text = p.PhaseText;

        OpProgress.IsIndeterminate = p.IsIndeterminate;
        OpProgress.Maximum         = p.Total > 0 ? p.Total : 100;
        if (p.Processed is { } proc) { OpProgress.Value = proc; CountProcessed.Text = proc.ToString("N0"); }
        if (p.Accepted  is { } acc)  CountAccepted.Text  = acc.ToString("N0");
        if (p.Rejected  is { } rej)  CountRejected.Text  = rej.ToString("N0");

        if (p.NewOperation is { } op)
            AppendOperation(op);
    }

    private void AppendOperation(IngestionOperation op)
    {
        _allOps.Add(op);

        if (PassesFilter(op))
        {
            _filteredOps.Add(op);
            UpdateCountLabel();
            ScrollToEnd();
        }
    }

    private bool PassesFilter(IngestionOperation op)
    {
        var action = op.Action;
        if (action == "hash"               && FilterHash.IsChecked      == true) return true;
        if (action == "copy"               && FilterCopy.IsChecked      == true) return true;
        if (action == "stage-moved"        && FilterCopy.IsChecked      == true) return true;
        if (action == "delete"             && FilterDelete.IsChecked    == true) return true;
        // "release-input-assembled" = staging → source (was "source-promoted")
        if (action == "release-input-assembled" && FilterSource.IsChecked == true) return true;
        if (action == "transform"                && FilterTransform.IsChecked == true) return true;
        if (action == "staging-resumed"          && FilterTransform.IsChecked == true) return true;
        if (action == "derived-committed"        && FilterTransform.IsChecked == true) return true;
        if (action == "already-present"          && FilterTransform.IsChecked == true) return true;
        if (action == "rebuild-required"         && FilterTransform.IsChecked == true) return true;
        if (action == "stale-artifact-overwritten" && FilterTransform.IsChecked == true) return true;
        if (action == "skip"               && FilterSkip.IsChecked      == true) return true;
        // "unwanted-classified" = Phase 6 match; "unwanted-moved" = Phase 8 move to incoming-skip
        if (action == "unwanted-classified" && FilterSkip.IsChecked     == true) return true;
        if (action == "unwanted-moved"     && FilterSkip.IsChecked      == true) return true;
        // Stale staging/source relocation for now-unwanted releases → Skip bucket
        if (action == "stale-staging-unwanted-moved" && FilterSkip.IsChecked == true) return true;
        if (action == "stale-source-unwanted-moved"  && FilterSkip.IsChecked == true) return true;
        // incomplete-skipped / archive-collision / archive-validation-blocked are failure-like
        if (action == "incomplete-skipped"         && FilterFailed.IsChecked == true) return true;
        if (action == "archive-collision"          && FilterFailed.IsChecked == true) return true;
        if (action == "archive-validation-blocked" && FilterFailed.IsChecked == true) return true;
        if (action.EndsWith("-failed")     && FilterFailed.IsChecked    == true) return true;
        // catch-all (e.g. discarded-by-strategy, archive-deleted, unrecognized) — bucket with Skip
        if (action != "hash" && action != "copy" && action != "stage-moved" && action != "delete"
            && action != "release-input-assembled" && action != "transform"
            && action != "derived-committed" && action != "already-present"
            && action != "rebuild-required" && action != "stale-artifact-overwritten"
            && action != "incomplete-skipped" && action != "archive-collision"
            && action != "archive-validation-blocked"
            && !action.EndsWith("-failed")
            && FilterSkip.IsChecked == true) return true;
        return false;
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e)
    {
        _filteredOps.Clear();
        foreach (var op in _allOps)
            if (PassesFilter(op))
                _filteredOps.Add(op);

        UpdateCountLabel();
        ScrollToEnd();
    }

    private void UpdateCountLabel()
    {
        OpsCountLabel.Text = _allOps.Count == _filteredOps.Count
            ? $"{_allOps.Count} ops"
            : $"{_filteredOps.Count} / {_allOps.Count} ops";
    }

    private void ScrollToEnd() =>
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => OpsScrollViewer.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);

    // Accent colors for a few key rows; everything else uses the muted default.
    private static readonly Dictionary<string, string> CounterAccent = new()
    {
        ["Files matched"]             = "#F0F0F0",
        ["Files staged"]              = "#4CAF50",
        ["Derived artifacts created"] = "#26C6DA",
        ["Releases present"]          = "#7B68EE",
        ["Files skipped"]             = "#FFA726",
        ["Unwanted skipped"]          = "#9E9E9E",
        ["Transforms failed"]         = "#E57373",
    };

    private static Grid BuildCounterRow(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var labelBlock = new TextBlock
        {
            Text       = label,
            FontSize   = 12,
            Foreground = new SolidColorBrush(Color.Parse("#888899")),
        };
        Grid.SetColumn(labelBlock, 0);

        var accent = CounterAccent.TryGetValue(label, out var hex) ? hex : "#888899";
        var valueBlock = new TextBlock
        {
            Text       = value,
            FontSize   = 12,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.Parse(accent)),
        };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        return grid;
    }

    // ── Completion ────────────────────────────────────────────────────────────

    public void SetCompleted(IngestionResult result)
    {
        _isRunning = false;

        // Progress bar stays visible — snap to 100% and update phase label.
        OpProgress.IsIndeterminate = false;
        OpProgress.Maximum         = 100;
        OpProgress.Value           = 100;
        PhaseText.Text             = "Completed.";

        // Sync top counters with final result values. "SKIPPED" here covers both
        // non-unwanted skips and unwanted moves so it never reads 0 while the ops
        // list is full of unwanted entries.
        CountProcessed.Text = result.FilesScanned.ToString("N0");
        CountAccepted.Text  = result.FilesMatched.ToString("N0");
        CountRejected.Text  = (result.FilesSkipped + result.UnwantedSkipped).ToString("N0");

        if (result.Error is null)
        {
            SummaryPanel.IsVisible = true;
            SumStatusTitle.Text    = result.Status switch
            {
                IngestionStatus.Success        => "INGESTION COMPLETED",
                IngestionStatus.PartialSuccess => "PARTIAL SUCCESS",
                _                              => "FAILED",
            };

            // Render the shared core counter set — same source as the final log.
            SummaryCountersPanel.Children.Clear();
            foreach (var (label, value) in IngestionSummary.CoreCounters(result))
                SummaryCountersPanel.Children.Add(BuildCounterRow(label, value));

            var note = IngestionSummary.AllUnwantedNote(result);
            if (note is not null)
            {
                SumNote.Text      = note;
                SumNote.IsVisible = true;
            }

            if (result.TransformsFailed > 0 || result.ReleasesIncomplete > 0)
            {
                FailedPanel.IsVisible = true;

                if (result.TransformsFailed > 0 && result.ReleasesIncomplete > 0)
                {
                    FailedTitle.Text   = "TRANSFORMS FAILED + INCOMPLETE RELEASES";
                    FailedMessage.Text =
                        $"{result.TransformsFailed} release(s) had transform errors; " +
                        $"{result.ReleasesIncomplete} release(s) were incomplete (missing files). " +
                        "See the ingestion log for details.";
                }
                else if (result.TransformsFailed > 0)
                {
                    FailedTitle.Text   = "TRANSFORMS FAILED";
                    FailedMessage.Text =
                        $"{result.TransformsFailed} release(s) had transform errors — see the ingestion log for details.";
                }
                else
                {
                    FailedTitle.Text   = "INCOMPLETE RELEASES";
                    FailedMessage.Text =
                        $"{result.ReleasesIncomplete} release(s) were incomplete (missing expected files). " +
                        "Incoming archives have been preserved. Check staging and add the missing files.";
                }
            }
        }
        else
        {
            FailedPanel.IsVisible = true;
            FailedTitle.Text      = "OPERATION FAILED";
            FailedMessage.Text    = result.Error;
        }

        OkButton.IsEnabled = true;
    }

    public void SetFailed(string errorMessage)
    {
        _isRunning = false;

        OpProgress.IsIndeterminate = false;
        OpProgress.Maximum         = 100;
        OpProgress.Value           = 100;
        PhaseText.Text             = "Failed.";

        FailedPanel.IsVisible  = true;
        FailedMessage.Text     = errorMessage;
        OkButton.IsEnabled     = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isRunning)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
}

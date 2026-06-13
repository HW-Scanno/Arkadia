using System.Collections.Generic;
using System.Collections.ObjectModel;
using Arkadia.Ingestion;
using Avalonia.Controls;
using Avalonia.Interactivity;

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
        if (action == "source-promoted"    && FilterSource.IsChecked    == true) return true;
        if (action == "transform"                && FilterTransform.IsChecked == true) return true;
        if (action == "derived-committed"        && FilterTransform.IsChecked == true) return true;
        if (action == "already-present"          && FilterTransform.IsChecked == true) return true;
        if (action == "rebuild-required"         && FilterTransform.IsChecked == true) return true;
        if (action == "stale-artifact-overwritten" && FilterTransform.IsChecked == true) return true;
        if (action == "skip"               && FilterSkip.IsChecked      == true) return true;
        if (action == "unwanted-skipped"   && FilterSkip.IsChecked      == true) return true;
        if (action == "unwanted-skip-failed" && FilterFailed.IsChecked  == true) return true;
        // incomplete-skipped is failure-like: show under Failed filter
        if (action == "incomplete-skipped" && FilterFailed.IsChecked    == true) return true;
        if (action.EndsWith("-failed")     && FilterFailed.IsChecked    == true) return true;
        // catch-all (e.g. discarded-by-strategy, archive-deleted, unrecognized) — bucket with Skip
        if (action != "hash" && action != "copy" && action != "stage-moved" && action != "delete"
            && action != "source-promoted" && action != "transform"
            && action != "derived-committed" && action != "already-present"
            && action != "rebuild-required" && action != "stale-artifact-overwritten"
            && action != "incomplete-skipped"
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

    // ── Completion ────────────────────────────────────────────────────────────

    public void SetCompleted(IngestionResult result)
    {
        _isRunning = false;

        // Progress bar stays visible — snap to 100% and update phase label.
        OpProgress.IsIndeterminate = false;
        OpProgress.Maximum         = 100;
        OpProgress.Value           = 100;
        PhaseText.Text             = "Completed.";

        // Sync top counters with final result values.
        CountProcessed.Text = result.FilesScanned.ToString("N0");
        CountAccepted.Text  = result.FilesMatched.ToString("N0");
        CountRejected.Text  = result.FilesSkipped.ToString("N0");

        if (result.Error is null)
        {
            SummaryPanel.IsVisible = true;
            SumStatusTitle.Text    = result.Status switch
            {
                IngestionStatus.Success        => "INGESTION COMPLETED",
                IngestionStatus.PartialSuccess => "PARTIAL SUCCESS",
                _                              => "FAILED",
            };
            SumScanned.Text  = result.FilesScanned.ToString("N0");
            SumMatched.Text  = result.FilesMatched.ToString("N0");
            SumCopied.Text   = result.FilesCopied.ToString("N0");
            SumPresent.Text  = result.ReleasesPresent.ToString("N0");
            SumSkipped.Text  = result.FilesSkipped.ToString("N0");
            if (result.UnwantedSkipped > 0)
            {
                SumUnwanted.Text         = result.UnwantedSkipped.ToString("N0");
                SumUnwantedRow.IsVisible = true;
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

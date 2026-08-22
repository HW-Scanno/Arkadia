using System.Threading.Tasks;
using Arkadia.Ingestion;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Sequential Group Verify progress window with an inline Retry/Stop decision on a leaf failure (the ONLY two
/// choices — no Skip). Stays open during the run; drives the pure <see cref="GroupVerifyOrchestrator"/> through
/// <see cref="SetProgress"/>, <see cref="PromptRetryStop"/>, and the completion/stopped summaries.
/// </summary>
public partial class GroupVerifyDialog : Window
{
    private TaskCompletionSource<GroupVerifyDecision>? _decision;

    public GroupVerifyDialog() : this("Group") { }

    public GroupVerifyDialog(string groupName)
    {
        InitializeComponent();
        HeaderName.Text = $"Verify — {groupName}";
    }

    /// <summary>Updates the live progress (called before each leaf and after each success). 1-based position.</summary>
    public void SetProgress(int position, int total, int completed, string leafName)
    {
        CurrentLabel.Text   = $"Current:  {position} / {total}";
        LeafLabel.Text      = leafName;
        CompletedLabel.Text = $"Successfully verified:  {completed} / {total}";
    }

    /// <summary>
    /// Shows the failure panel and awaits the user's Retry/Stop choice (the paused decision). The progress spinner
    /// is hidden while paused; on the returned choice the panel is cleared so the run can continue or stop.
    /// </summary>
    public Task<GroupVerifyDecision> PromptRetryStop(
        string leafName, string leafId, int position, int total, int completed, string reason)
    {
        _decision = new TaskCompletionSource<GroupVerifyDecision>();

        Spinner.IsVisible       = false;
        FailurePanel.IsVisible  = true;
        FailureTitle.Text       = $"Verification failed — leaf {position} / {total}";
        FailureReason.Text      = reason.Length > 0 ? reason : "The leaf verification did not complete.";
        FailureContext.Text     = $"{leafName}  ({leafId})    ·    {completed} leaf(s) already verified";
        LeafLabel.Text          = leafName;
        RetryButton.IsVisible   = true;
        StopButton.IsVisible    = true;

        return _decision.Task;
    }

    private void ClearFailurePanel()
    {
        FailurePanel.IsVisible = false;
        RetryButton.IsVisible  = false;
        StopButton.IsVisible   = false;
        Spinner.IsVisible      = true;
    }

    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        ClearFailurePanel();
        _decision?.TrySetResult(GroupVerifyDecision.Retry);
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _decision?.TrySetResult(GroupVerifyDecision.Stop);
        // Panel/summary are set by the caller after Stop resolves.
    }

    /// <summary>Final state — all leaves passed.</summary>
    public void SetCompletedSummary(string summary)
    {
        ProgressPanel.IsVisible = false;
        FailurePanel.IsVisible  = false;
        RetryButton.IsVisible   = false;
        StopButton.IsVisible    = false;
        SummaryLabel.IsVisible  = true;
        SummaryLabel.Text       = summary;
        CloseButton.IsVisible   = true;
    }

    /// <summary>Final state — the user stopped after a failure.</summary>
    public void SetStoppedSummary(string summary)
    {
        ProgressPanel.IsVisible = false;
        FailurePanel.IsVisible  = false;
        RetryButton.IsVisible   = false;
        StopButton.IsVisible    = false;
        SummaryLabel.IsVisible  = true;
        SummaryLabel.Text       = summary;
        CloseButton.IsVisible   = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

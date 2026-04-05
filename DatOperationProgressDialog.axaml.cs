using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class DatOperationProgressDialog : Window
{
    private bool _isRunning = true;

    // Parameterless ctor required by Avalonia XAML compiler
    public DatOperationProgressDialog() : this("") { }

    public DatOperationProgressDialog(string operationTitle)
    {
        InitializeComponent();
        OpTitle.Text = operationTitle;
    }

    // Called from background thread — marshalled to UI thread by Progress<T>.
    public void Update(DatOperationProgress p)
    {
        PhaseText.Text              = p.PhaseText;
        OpProgress.IsIndeterminate  = p.IsIndeterminate;
        OpProgress.Maximum          = p.Total > 0 ? p.Total : 100;
        OpProgress.Value            = p.Processed;
        CountProcessed.Text         = p.Processed.ToString("N0");
        CountAccepted.Text          = p.Accepted.ToString("N0");
        CountRejected.Text          = p.Rejected.ToString("N0");
    }

    public void SetImportCompleted(string platformName, string datLineId, int imported)
    {
        _isRunning = false;

        RunningPanel.IsVisible        = false;
        ImportSummaryPanel.IsVisible  = true;

        ImportSumPlatform.Text = platformName;
        ImportSumId.Text       = datLineId;
        ImportSumCount.Text    = imported.ToString("N0");

        OkButton.IsEnabled = true;
    }

    public void SetUpdateCompleted(string platformName, string datLineId, ReconciliationResult result)
    {
        _isRunning = false;

        RunningPanel.IsVisible       = false;
        UpdateSummaryPanel.IsVisible = true;

        UpdateSumPlatform.Text = platformName;
        UpdateSumId.Text       = datLineId;
        UpdateSumKept.Text     = result.Kept.ToString("N0");
        UpdateSumOutdated.Text = result.Outdated.ToString("N0");
        UpdateSumPending.Text  = result.Pending.ToString("N0");
        UpdateSumMissing.Text  = result.Missing.ToString("N0");

        OkButton.IsEnabled = true;
    }

    public void SetFailed(string errorMessage)
    {
        _isRunning = false;

        RunningPanel.IsVisible = false;
        FailedPanel.IsVisible  = true;
        FailedMessage.Text     = errorMessage;

        OkButton.IsEnabled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isRunning)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
}

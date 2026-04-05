using Arkadia.Ingestion;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class IngestionProgressDialog : Window
{
    private bool _isRunning = true;

    // Parameterless ctor required by Avalonia XAML compiler
    public IngestionProgressDialog() : this("") { }

    public IngestionProgressDialog(string title)
    {
        InitializeComponent();
        OpTitle.Text = title;
    }

    // ── Progress update (marshalled to UI thread by Progress<T>) ──────────────

    public void Update(IngestionProgress p)
    {
        if (p.PhaseText.Length > 0)
            PhaseText.Text = p.PhaseText;

        OpProgress.IsIndeterminate = p.IsIndeterminate;
        OpProgress.Maximum         = p.Total > 0 ? p.Total : 100;
        OpProgress.Value           = p.Processed;
        CountProcessed.Text        = p.Processed.ToString("N0");
        CountAccepted.Text         = p.Accepted.ToString("N0");
        CountRejected.Text         = p.Rejected.ToString("N0");

        if (p.NewOperation is { } op)
            AppendOperation(op);
    }

    private void AppendOperation(IngestionOperation op)
    {
        OpsPanel.IsVisible = true;

        var color = op.Action switch
        {
            "copy"    => "#4CAF50",
            "archive" => "#7B68EE",
            "delete"  => "#888899",
            _         => "#FFA726",  // skip, *-failed
        };

        var row = new TextBlock
        {
            Text         = $"{op.Object}  |  {op.Action}  |  {op.Destination}",
            FontSize     = 10,
            FontFamily   = new FontFamily("Consolas,Courier New,monospace"),
            Foreground   = new SolidColorBrush(Color.Parse(color)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        OpsList.Children.Add(row);

        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => OpsScrollViewer.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Completion ────────────────────────────────────────────────────────────

    public void SetCompleted(IngestionResult result)
    {
        _isRunning             = false;
        RunningPanel.IsVisible = false;

        if (result.Success)
        {
            SummaryPanel.IsVisible = true;
            SumScanned.Text  = result.FilesScanned.ToString("N0");
            SumMatched.Text  = result.FilesMatched.ToString("N0");
            SumCopied.Text   = result.FilesCopied.ToString("N0");
            SumPresent.Text  = result.ReleasesPresent.ToString("N0");
            SumSkipped.Text  = result.FilesSkipped.ToString("N0");
        }
        else
        {
            FailedPanel.IsVisible  = true;
            FailedMessage.Text     = result.Error ?? "Unknown error";
        }

        OkButton.IsEnabled = true;
    }

    public void SetFailed(string errorMessage)
    {
        _isRunning             = false;
        RunningPanel.IsVisible = false;
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

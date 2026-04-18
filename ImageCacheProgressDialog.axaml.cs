using System.Collections.ObjectModel;
using Arkadia.Ingestion;
using Arkadia.Systems;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class ImageCacheProgressDialog : Window
{
    private bool _isRunning = true;

    private readonly ObservableCollection<IngestionOperation> _ops = [];

    public ImageCacheProgressDialog() : this("") { }

    public ImageCacheProgressDialog(string title)
    {
        InitializeComponent();
        OpTitle.Text        = title;
        OpsList.ItemsSource = _ops;
    }

    public void Update(ImageCacheProgress p)
    {
        if (p.PhaseText.Length > 0)
            PhaseText.Text = p.PhaseText;

        OpProgress.IsIndeterminate = p.IsIndeterminate;
        OpProgress.Maximum         = p.Total > 0 ? p.Total : 100;
        OpProgress.Value           = p.Processed;
        CountSources.Text          = p.Processed.ToString("N0");
        CountGenerated.Text        = p.Generated.ToString("N0");

        if (p.NewOperation is { } op)
        {
            _ops.Add(op);
            ScrollToEnd();
        }
    }

    public void SetCompleted(ImageCacheResult result)
    {
        _isRunning = false;

        OpProgress.IsIndeterminate = false;
        OpProgress.Maximum         = 100;
        OpProgress.Value           = 100;
        PhaseText.Text             = "Completed.";

        CountSources.Text   = result.SourcesProcessed.ToString("N0");
        CountGenerated.Text = result.CachedGenerated.ToString("N0");

        SummaryPanel.IsVisible = true;
        SumSources.Text        = result.SourcesProcessed.ToString("N0");
        SumGenerated.Text      = result.CachedGenerated.ToString("N0");

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

    private void ScrollToEnd() =>
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => OpsScrollViewer.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isRunning)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
}

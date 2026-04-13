using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class DatLineVerifyDialog : Window
{
    private readonly ObservableCollection<VerifyRow> _rows = [];
    private const int MaxRows = 1000;

    public DatLineVerifyDialog() { InitializeComponent(); }

    public DatLineVerifyDialog(string datLineName, string platformDesc)
    {
        InitializeComponent();
        HeaderText.Text      = $"DAT Verify Mode  —  Platform: {platformDesc}  —  DAT Line: {datLineName}";
        RowsList.ItemsSource = _rows;
        UpdateStats(0, 0, 0, 0, 0, 0);
        StatusLine.Text  = "Preparing…";
        PhaseLabel.Text  = "";
    }

    // ── Public update API (call on UI thread) ─────────────────────────────────

    public void AppendRow(string volume, string result, string path, string detail)
    {
        if (_rows.Count >= MaxRows) _rows.RemoveAt(0);
        _rows.Add(new VerifyRow { Volume = volume, Result = result, Path = path, Detail = detail });
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => RowsScroll.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    public void UpdateStats(int totalVols, int verifiedVols, int skippedVols,
                            int expected, int verified, int missing, int mismatch = 0)
    {
        StatVolumes.Text     = totalVols.ToString("N0");
        StatVerifiedVols.Text= verifiedVols.ToString("N0");
        StatSkipped.Text     = skippedVols.ToString("N0");
        StatExpected.Text    = expected.ToString("N0");
        StatVerified.Text    = verified.ToString("N0");
        StatMissing.Text     = missing.ToString("N0");
        StatMismatch.Text    = mismatch.ToString("N0");
    }

    public void SetStatus(string text) => StatusLine.Text = text;

    public void SetCompleted(string summary)
    {
        PhaseLabel.Text       = summary;
        PhaseLabel.Foreground = new SolidColorBrush(Color.Parse("#81C784"));
        CloseButton.IsEnabled = true;
    }

    public void SetFailed(string error)
    {
        PhaseLabel.Text       = $"Failed: {error}";
        PhaseLabel.Foreground = new SolidColorBrush(Color.Parse("#EF5350"));
        CloseButton.IsEnabled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!CloseButton.IsEnabled) e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

using System.Collections.ObjectModel;
using Arkadia.Volumes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class DatLineVerifyDialog : Window, IDatLineVerifyProgress
{
    private readonly ObservableCollection<VerifyRow> _rows = [];
    private const int MaxRows = 1000;

    public DatLineVerifyDialog() { InitializeComponent(); InitCommon(); }

    public DatLineVerifyDialog(string datLineName, string platformDesc)
    {
        InitializeComponent();
        InitCommon();
        HeaderText.Text = $"DAT Verify Mode  —  Platform: {platformDesc}  —  DAT Line: {datLineName}";
    }

    private void InitCommon()
    {
        RowsList.ItemsSource = _rows;
        UpdateStats(0, 0, 0, 0, 0, 0);
        StatusLine.Text = "Preparing…";
        PhaseLabel.Text = "";
    }

    // ── Verify Volume presentation mode (V-G5) ────────────────────────────────
    // Repurposes this shared dialog for a single-volume verify: truthful (non-read-only) subtitle, a
    // volume-centric header (owner shown as Collection for Group, DAT Line for Single), and the seven counters
    // relabelled/populated from VolumeVerifyResult. DAT-line ("Verify ALL") and other modes are unaffected.

    /// <summary>Switches the dialog into Verify Volume presentation. Call once, right after construction.</summary>
    public void ConfigureVolumeMode(string volumeLabel, string ownerDescription, bool isGroup)
    {
        HeaderText.Text   = VolumeVerifyPresentation.Header(volumeLabel, ownerDescription, isGroup);
        SubtitleText.Text = VolumeVerifyPresentation.Subtitle;

        var labels = VolumeVerifyPresentation.CounterLabels;
        LblC0.Text = labels[0]; LblC1.Text = labels[1]; LblC2.Text = labels[2]; LblC3.Text = labels[3];
        LblC4.Text = labels[4]; LblC5.Text = labels[5]; LblC6.Text = labels[6];
    }

    /// <summary>Populates the seven Volume counters from a (possibly aggregated) VolumeVerifyResult.</summary>
    public void UpdateVolumeStats(VolumeVerifyResult r)
    {
        var v = VolumeVerifyPresentation.Counters(r);
        StatVolumes.Text      = v[0].ToString("N0");   // SCANNED
        StatVerifiedVols.Text = v[1].ToString("N0");   // RESTORED
        StatSkipped.Text      = v[2].ToString("N0");   // MOVED OUT
        StatExpected.Text     = v[3].ToString("N0");   // RECONCILED
        StatVerified.Text     = v[4].ToString("N0");   // VERIFIED
        StatMissing.Text      = v[5].ToString("N0");   // MISSING
        StatMismatch.Text     = v[6].ToString("N0");   // ERRORS
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

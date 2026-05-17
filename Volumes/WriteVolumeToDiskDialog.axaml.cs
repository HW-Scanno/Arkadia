using System;
using System.Collections.ObjectModel;
using Arkadia.Volumes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class WriteVolumeToDiskDialog : Window
{
    private readonly long _totalBytes;
    private readonly int  _totalFiles;
    private readonly ObservableCollection<WriteVolumeRow> _rows = [];

    private const int MaxRows = 500;

    public WriteVolumeToDiskDialog() { InitializeComponent(); }

    public WriteVolumeToDiskDialog(string header, long totalBytes, int totalFiles)
    {
        _totalBytes = totalBytes;
        _totalFiles = totalFiles;
        InitializeComponent();
        HeaderText.Text      = header;
        RowsList.ItemsSource = _rows;
        ToCopyLabel.Text     = FormatBytes(totalBytes);
        CopiedLabel.Text     = FormatBytes(0);
        VerifiedLabel.Text   = FormatBytes(0);
        PhaseLabel.Text      = "Preparing…";
    }

    // ── Public update API (must be called on UI thread) ───────────────────────

    /// <summary>
    /// Appends a row to the operation table. Call on UI thread.
    /// Trims from the front when the row limit is reached.
    /// </summary>
    public void AppendRow(string action, string path, string sizeLabel)
    {
        if (_rows.Count >= MaxRows)
            _rows.RemoveAt(0);
        _rows.Add(new WriteVolumeRow { Action = action, Path = path, SizeLabel = sizeLabel });
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => RowsScroll.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Updates the three bars, byte labels, and the status line. Call on UI thread.
    /// </summary>
    public void UpdateStats(long copiedBytes, long verifiedBytes,
                            int filesProcessed, TimeSpan elapsed)
    {
        // Bar widths — scale to track pixel width; guard against zero before layout
        SetBarFill(ToCopyBarTrack, ToCopyBarFill,
            _totalBytes > 0 ? Math.Max(0, _totalBytes - copiedBytes) / (double)_totalBytes : 1.0);
        SetBarFill(CopiedBarTrack, CopiedBarFill,
            _totalBytes > 0 ? Math.Clamp(copiedBytes   / (double)_totalBytes, 0, 1) : 0);
        SetBarFill(VerifiedBarTrack, VerifiedBarFill,
            _totalBytes > 0 ? Math.Clamp(verifiedBytes / (double)_totalBytes, 0, 1) : 0);

        ToCopyLabel.Text   = FormatBytes(Math.Max(0, _totalBytes - copiedBytes));
        CopiedLabel.Text   = FormatBytes(copiedBytes);
        VerifiedLabel.Text = FormatBytes(verifiedBytes);

        double etaSec   = AppendVerifier.CalculateEtaSeconds(copiedBytes, verifiedBytes, _totalBytes, elapsed);
        double doneWork = copiedBytes + verifiedBytes;
        double speedBps = elapsed.TotalSeconds > 0.5 ? doneWork / elapsed.TotalSeconds : 0;
        double speedMBs = speedBps / (1024.0 * 1024);

        StatusLine.Text =
            $"Files: {filesProcessed:N0} / {_totalFiles:N0}  |  " +
            $"Copied: {FormatBytes(copiedBytes)}  |  " +
            $"Verified: {FormatBytes(verifiedBytes)}  |  " +
            $"Speed: {speedMBs:F1} MB/s  |  " +
            $"ETA: {TimeSpan.FromSeconds(etaSec):hh\\:mm\\:ss}  |  " +
            $"Elapsed: {elapsed:hh\\:mm\\:ss}";

        PhaseLabel.Text = copiedBytes < _totalBytes ? "Copying…" : "Verifying…";
    }

    public void SetCompleted(int filesCopied, long bytesCopied, string dstPath,
                             string? completionText = null)
    {
        // Snap bars to final state
        SetBarFill(ToCopyBarTrack, ToCopyBarFill, 0);
        SetBarFill(CopiedBarTrack,   CopiedBarFill,   1);
        SetBarFill(VerifiedBarTrack, VerifiedBarFill, 1);
        ToCopyLabel.Text   = FormatBytes(0);
        CopiedLabel.Text   = FormatBytes(bytesCopied);
        VerifiedLabel.Text = FormatBytes(bytesCopied);

        PhaseLabel.Text = completionText
            ?? $"Completed — {filesCopied} file(s) written and verified.";
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

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void SetBarFill(Border track, Border fill, double fraction)
    {
        double trackWidth = track.Bounds.Width;
        if (trackWidth <= 0) return;
        fill.Width = Math.Max(0, trackWidth * Math.Clamp(fraction, 0, 1));
    }

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "0 B";
        if (b < 1024L)                return $"{b} B";
        if (b < 1024L * 1024)         return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)  return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}

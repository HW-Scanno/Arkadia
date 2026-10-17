using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Maintenance;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Settings → Clean Empty Folders. Scan → preview → explicit confirmation (the "Remove" button) → removal → summary.
/// The CLEANING console state is owned by <see cref="EmptyFolderCleaner"/> and exists only while the scan or the
/// removal is really running — never while this window shows the preview or the final summary.
/// </summary>
public partial class CleanEmptyFoldersDialog : Window
{
    private enum Phase { Scanning, Preview, Removing, Done }

    private readonly EmptyFolderCleaner _cleaner;
    private readonly IReadOnlyList<CleanupRoot> _roots;
    private CancellationTokenSource _cts = new();
    private EmptyFolderScan? _scan;
    private Phase _phase = Phase.Scanning;

    /// <summary>Designer/runtime-loader constructor: no roots, so it can never touch anything.</summary>
    public CleanEmptyFoldersDialog() : this(new EmptyFolderCleaner(), Array.Empty<CleanupRoot>()) { }

    public CleanEmptyFoldersDialog(EmptyFolderCleaner cleaner, IReadOnlyList<CleanupRoot> roots)
    {
        _cleaner = cleaner;
        _roots   = roots;
        InitializeComponent();
        Show(Phase.Scanning, "Scanning managed folders…", "");
        Opened += async (_, _) => await RunScanAsync();
    }

    private bool Busy => _phase is Phase.Scanning or Phase.Removing;

    private void Show(Phase phase, string headline, string detail)
    {
        _phase = phase;
        HeadlineText.Text = headline;
        DetailBox.Text    = detail;
        BusyBar.IsVisible = Busy;
        RemoveButton.IsVisible = phase == Phase.Preview && _scan is { CandidateCount: > 0 };
        RemoveButton.Content   = _scan is null ? "Remove empty folders" : $"Remove {_scan.CandidateCount} empty folder{(_scan.CandidateCount == 1 ? "" : "s")}";
        CancelButton.Content   = phase == Phase.Done || (phase == Phase.Preview && !RemoveButton.IsVisible) ? "Close" : "Cancel";
    }

    private async Task RunScanAsync()
    {
        var conflict = _cleaner.ConflictingActivity();
        if (conflict is not null) { Show(Phase.Done, "Cannot clean right now", conflict); return; }

        try
        {
            var token = _cts.Token;
            _scan = await Task.Run(() => _cleaner.Scan(_roots, token));
        }
        catch (Exception ex)
        {
            Show(Phase.Done, "The scan failed", ex.Message);
            return;
        }

        if (_scan.Cancelled) { _phase = Phase.Done; Close(); return; }
        Show(Phase.Preview, EmptyFolderReport.Headline(_scan), EmptyFolderReport.Preview(_scan));
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_phase != Phase.Preview || _scan is null) return;

        // Re-check right before the work starts: the preview may have been open for a while.
        var conflict = _cleaner.ConflictingActivity();
        if (conflict is not null) { Show(Phase.Done, "Cannot clean right now", conflict); return; }

        var scan = _scan;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Show(Phase.Removing, $"Removing empty folders… 0 of {scan.CandidateCount}", "");
        var progress = new Progress<EmptyFolderProgress>(p =>
        {
            if (_phase == Phase.Removing)
                HeadlineText.Text = $"Removing empty folders… {p.Done} of {p.Total}";
        });

        try
        {
            var result = await Task.Run(() => _cleaner.Execute(scan, token, progress));
            var headline = result.Cancelled ? "Cleanup cancelled" : result.Failed.Count > 0 ? "Cleanup finished with errors" : "Cleanup finished";
            Show(Phase.Done, headline, EmptyFolderReport.Summary(result));
        }
        catch (Exception ex)
        {
            Show(Phase.Done, "The cleanup failed", ex.Message);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (Busy) { _cts.Cancel(); return; }       // stop at the next folder; the summary then appears
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Busy) { e.Cancel = true; _cts.Cancel(); }      // never abandon a running removal: cancel it and wait
        base.OnClosing(e);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Arkadia.LocalArchive;

// ── Progress row view-model ───────────────────────────────────────────────────

public sealed class LocalArchiveProgressRow
{
    public string Action   { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Detail   { get; set; } = "";
}

// ── Dialog ────────────────────────────────────────────────────────────────────

public partial class LocalArchiveVerifyDialog : Window
{
    private readonly string                    _platformId;
    private readonly string                    _datLineId;
    private readonly DatLineStore              _store;
    private readonly LocalArchiveVerifyService _service;
    private readonly IReadOnlyDictionary<string, AssignedVolumeInfo>? _assignedVolumes;

    private LocalArchiveVerifyPlan? _plan;
    private bool                    _isRunning = true;

    private readonly List<LocalArchiveProgressRow>              _allRows      = [];
    private readonly ObservableCollection<LocalArchiveProgressRow> _filteredRows = [];

    private const int MaxRows = 2000;

    // Live counters updated as progress events arrive.
    private int _scanned, _wantedOk, _unwanted, _unknown, _mismatch,
                _redundant, _unavailable, _repairable, _repaired;

#pragma warning disable CS8618
    public LocalArchiveVerifyDialog() { InitializeComponent(); }
#pragma warning restore CS8618

    public LocalArchiveVerifyDialog(
        string                                           platformId,
        string                                           datLineId,
        DatLineStore                                     store,
        LocalArchiveVerifyService                        service,
        IReadOnlyDictionary<string, AssignedVolumeInfo>? assignedVolumes = null)
    {
        _platformId      = platformId;
        _datLineId       = datLineId;
        _store           = store;
        _service         = service;
        _assignedVolumes = assignedVolumes;

        InitializeComponent();
        RowsList.ItemsSource  = _filteredRows;
        ArchiveDirLabel.Text  = $"archive\\{platformId}\\{datLineId}";
        PhaseLabel.Text       = "Preparing scan…";
        StatusLabel.Text      = "";
        UpdateStatLabels();

        Opened += async (_, _) => await RunVerifyAsync();
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    private async Task RunVerifyAsync()
    {
        _isRunning = true;
        PhaseLabel.Text = "Scanning archive…";

        // Progress<T> captures UI SynchronizationContext — callbacks fire on UI thread.
        var progress = new Progress<LocalArchiveVerifyProgress>(OnScanProgress);

        _plan = await Task.Run(() =>
            _service.Verify(_platformId, _datLineId, _store, progress, _assignedVolumes));

        // Final stats from the completed plan.
        _scanned     = _plan.FilesScanned;
        _wantedOk    = _plan.WantedOk;
        _unwanted    = _plan.UnwantedArtifacts;
        _unknown     = _plan.UnknownFiles;
        _mismatch    = _plan.HashMismatches;
        _redundant   = _plan.RedundantCopies;
        _unavailable = _plan.VolumeUnavailableWarnings;
        _repairable  = _plan.RepairableCount;
        UpdateStatLabels();

        ScanProgress.IsIndeterminate = false;
        ScanProgress.Value           = 100;
        PhaseLabel.Text              = _plan.IsClean
            ? "Scan complete — archive is clean."
            : $"Scan complete — {_plan.RepairableCount} repairable item(s) found.";

        if (_plan.AbsentFromArchiveCount > 0)
            StatusLabel.Text =
                $"Note: {_plan.AbsentFromArchiveCount} DB artifact(s) have no physical file in archive " +
                "(not shown here — use Archive Completeness for a full audit).";
        else if (_plan.IsClean)
            StatusLabel.Text = "Archive is clean.";
        else
            StatusLabel.Text = $"{_plan.RepairableCount} repairable item(s). Click Repair All to move them to incoming-skip.";

        RepairButton.IsEnabled = _plan.RepairableCount > 0;
        CloseButton.IsEnabled  = true;
        _isRunning             = false;
    }

    private void OnScanProgress(LocalArchiveVerifyProgress p)
    {
        // Running on the UI thread (Progress<T> marshals back).
        switch (p.Action)
        {
            case "archive-wanted-ok":         _wantedOk++;     _scanned++; break;
            case "archive-unwanted-found":    _unwanted++;     _scanned++; break;
            case "archive-unknown-found":     _unknown++;      _scanned++; break;
            case "archive-hash-mismatch":     _mismatch++;     _scanned++; break;
            case "archive-collision":                          _scanned++; break;
            case "archive-redundant-copy":    _redundant++;    _scanned++; break;
            case "archive-volume-unavailable": _unavailable++; _scanned++; break;
        }

        // Repairable = unwanted + unknown + mismatch + redundant (unavailable is not repairable)
        _repairable = _unwanted + _unknown + _mismatch + _redundant;

        UpdateStatLabels();
        AppendProgressRow(p.Action, p.FileName, p.Detail);

        PhaseLabel.Text = $"Scanning… {_scanned} files processed";
    }

    // ── Repair ────────────────────────────────────────────────────────────────

    private async void OnRepair(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        RepairButton.IsEnabled = false;
        CloseButton.IsEnabled  = false;
        _isRunning             = true;
        StatusLabel.Text       = "Repairing…";
        ScanProgress.IsIndeterminate = true;
        PhaseLabel.Text        = "Moving repairable files to incoming-skip…";

        var progress = new Progress<LocalArchiveVerifyProgress>(OnRepairProgress);
        var result   = await Task.Run(() => _service.Repair(_plan, _store, progress));

        ScanProgress.IsIndeterminate = false;
        ScanProgress.Value           = 100;
        PhaseLabel.Text = result.Success
            ? $"Repair complete — {result.MovedToSkip} file(s) moved, {result.RemovedDbRows} DB row(s) removed."
            : $"Repair failed: {result.ErrorMessage}";
        StatusLabel.Text = "";

        _repaired = result.MovedToSkip;
        UpdateStatLabels();

        _isRunning            = false;
        CloseButton.IsEnabled = true;
    }

    private void OnRepairProgress(LocalArchiveVerifyProgress p)
    {
        AppendProgressRow(p.Action, p.FileName, p.Detail);
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    private void OnFilterChanged(object? sender, RoutedEventArgs e)
    {
        _filteredRows.Clear();
        foreach (var row in _allRows)
            if (PassesFilter(row))
                _filteredRows.Add(row);
        UpdateRowCount();
        ScrollToEnd();
    }

    private bool PassesFilter(LocalArchiveProgressRow row)
    {
        var a = row.Action;
        if (FilterScan.IsChecked    == true &&
            (a == "archive-found-file" || a == "archive-hashing"))
            return true;
        if (FilterWanted.IsChecked  == true && a == "archive-wanted-ok")  return true;
        if (FilterUnwanted.IsChecked == true && a == "archive-unwanted-found") return true;
        if (FilterUnknown.IsChecked  == true && a == "archive-unknown-found")  return true;
        if (FilterMismatch.IsChecked == true && a == "archive-hash-mismatch")  return true;
        if (FilterMismatch.IsChecked == true && a == "archive-collision")       return true;
        if (FilterRedundant.IsChecked    == true && a == "archive-redundant-copy")     return true;
        if (FilterUnavailable.IsChecked  == true && a == "archive-volume-unavailable") return true;
        if (FilterRepair.IsChecked   == true &&
            (a == "archive-repair-moving"       || a == "archive-repair-moved" ||
             a == "archive-repair-skipped"      || a == "archive-error"        ||
             a == "archive-redundant-moved"     || a == "archive-volume-copy-missing"))
            return true;
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AppendProgressRow(string action, string fileName, string detail)
    {
        var row = new LocalArchiveProgressRow
            { Action = action, FileName = fileName, Detail = detail };

        _allRows.Add(row);
        if (_allRows.Count > MaxRows)
            _allRows.RemoveAt(0);

        if (PassesFilter(row))
        {
            _filteredRows.Add(row);
            UpdateRowCount();
            ScrollToEnd();
        }
    }

    private void UpdateStatLabels()
    {
        StatScanned.Text      = _scanned.ToString("N0");
        StatWantedOk.Text     = _wantedOk.ToString("N0");
        StatUnwanted.Text     = _unwanted.ToString("N0");
        StatUnknown.Text      = _unknown.ToString("N0");
        StatMismatch.Text     = _mismatch.ToString("N0");
        StatRedundant.Text    = _redundant.ToString("N0");
        StatUnavailable.Text  = _unavailable.ToString("N0");
        StatRepairable.Text   = _repairable.ToString("N0");
        StatRepaired.Text     = _repaired.ToString("N0");
    }

    private void UpdateRowCount()
    {
        RowCountLabel.Text = _allRows.Count == _filteredRows.Count
            ? $"{_allRows.Count} events"
            : $"{_filteredRows.Count} / {_allRows.Count} events";
    }

    private void ScrollToEnd() =>
        _ = Dispatcher.UIThread.InvokeAsync(
            () => RowsScroll.ScrollToEnd(),
            DispatcherPriority.Background);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isRunning) e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

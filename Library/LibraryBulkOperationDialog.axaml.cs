using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Arkadia.Library;

public partial class LibraryBulkOperationDialog : Window
{
    private readonly string                    _datLineId;
    private readonly string                    _datLineLabel;
    private readonly string                    _dbPath;
    private readonly LibraryBulkOperationPlanner _planner;
    private readonly LibraryBulkOperationService _service;

    private LibraryBulkOperationPlan?  _plan;
    private LibraryBulkOperationType   _currentOp = LibraryBulkOperationType.HideFromCatalog;

    // State machine: Input → Preview → Running → Result
    private enum Step { Input, Preview, Running, Result }
    private Step _step = Step.Input;

    public LibraryBulkOperationDialog(
        string          datLineId,
        string          datLineLabel,
        string          dbPath,
        DatLineStore    store,
        string          appRoot,
        CatalogService  catalog)
    {
        _datLineId    = datLineId;
        _datLineLabel = datLineLabel;
        _dbPath       = dbPath;
        _planner      = new LibraryBulkOperationPlanner(store);
        _service      = new LibraryBulkOperationService(appRoot, catalog);

        InitializeComponent();

        HeaderSubtitle.Text = datLineLabel;
        SetStep(Step.Input);
    }

    // ── Step transitions ──────────────────────────────────────────────────────

    private void SetStep(Step step)
    {
        _step = step;
        InputPanel.IsVisible    = step == Step.Input;
        PreviewPanel.IsVisible  = step == Step.Preview;
        ProgressPanel.IsVisible = step == Step.Running;
        ResultPanel.IsVisible   = step == Step.Result;

        BackBtn.IsVisible     = step == Step.Preview;
        PreviewBtn.IsVisible  = step is Step.Input or Step.Preview;
        ExecuteBtn.IsVisible  = step == Step.Preview;
        CloseBtn.IsVisible    = step is Step.Input or Step.Result;

        if (step == Step.Input)
        {
            PreviewBtn.Content  = "Preview…";
            PreviewBtn.IsEnabled = true;
        }
        else if (step == Step.Preview)
        {
            PreviewBtn.Content   = "Re-preview";
            PreviewBtn.IsEnabled = true;
            UpdateExecuteEnabled();
        }
        else if (step == Step.Running)
        {
            PreviewBtn.IsVisible = false;
            BackBtn.IsVisible    = false;
            CloseBtn.IsVisible   = false;
        }
        else if (step == Step.Result)
        {
            PreviewBtn.IsVisible = false;
            ExecuteBtn.IsVisible = false;
        }
    }

    // ── Input controls ────────────────────────────────────────────────────────

    private void OnMatchTextChanged(object? sender, TextChangedEventArgs e) { }

    private LibraryBulkOperationType GetSelectedOperation()
    {
        if (OpPurge.IsChecked   == true) return LibraryBulkOperationType.PurgeAndMarkUnwanted;
        if (OpRestore.IsChecked == true) return LibraryBulkOperationType.RestoreWanted;
        if (OpShow.IsChecked    == true) return LibraryBulkOperationType.ShowInCatalog;
        return LibraryBulkOperationType.HideFromCatalog;
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    private void OnPreview(object? sender, RoutedEventArgs e)
    {
        var matchText = MatchTextBox.Text?.Trim() ?? string.Empty;
        if (matchText.Length == 0)
        {
            MatchHint.Text     = "Please enter a search term to preview matches.";
            MatchHint.Foreground = Avalonia.Media.Brushes.Orange;
            return;
        }

        MatchHint.Text       = "Case-insensitive substring match against release name.";
        MatchHint.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse("#555577"));

        _currentOp = GetSelectedOperation();
        _plan      = _planner.Plan(_datLineId, _datLineLabel, matchText, _currentOp);

        PopulatePreview(_plan);
        SetStep(Step.Preview);
    }

    private void PopulatePreview(LibraryBulkOperationPlan plan)
    {
        StatMatched.Text    = plan.TotalMatches.ToString("N0");
        StatActionable.Text = plan.ActionableCount.ToString("N0");
        StatNoOp.Text       = plan.NoOpCount.ToString("N0");
        StatFiles.Text      = plan.TotalArchiveFiles.ToString("N0");
        StatBytes.Text      = plan.TotalArchiveBytes > 0
            ? FormatBytes(plan.TotalArchiveBytes)
            : "—";

        MatchList.ItemsSource = plan.Rows;

        // Large-batch warning
        if (plan.IsLargeBatch && !plan.IsVeryLargeBatch)
        {
            LargeBatchWarning.IsVisible   = true;
            LargeBatchWarningText.Text    =
                $"⚠ Large batch: {plan.ActionableCount} releases will be affected. " +
                "Review the list carefully before executing.";
            TypedConfirmPanel.IsVisible   = false;
        }
        else if (plan.IsVeryLargeBatch)
        {
            LargeBatchWarning.IsVisible   = true;
            LargeBatchWarningText.Text    =
                $"⚠ Very large batch: {plan.ActionableCount} releases will be affected. " +
                "Type the confirmation phrase below to unlock execution.";
            TypedConfirmPanel.IsVisible   = true;
            TypedConfirmLabel.Text        =
                $"Type exactly: {plan.ConfirmationPhrase}";
            TypedConfirmBox.Text          = string.Empty;
        }
        else
        {
            LargeBatchWarning.IsVisible   = false;
            TypedConfirmPanel.IsVisible   = false;
        }
    }

    private void OnTypedConfirmChanged(object? sender, TextChangedEventArgs e)
        => UpdateExecuteEnabled();

    private void UpdateExecuteEnabled()
    {
        if (_plan is null) { ExecuteBtn.IsEnabled = false; return; }
        if (_plan.ActionableCount == 0) { ExecuteBtn.IsEnabled = false; return; }

        if (_plan.IsVeryLargeBatch)
        {
            var typed = TypedConfirmBox.Text?.Trim() ?? string.Empty;
            ExecuteBtn.IsEnabled = string.Equals(
                typed, _plan.ConfirmationPhrase, StringComparison.Ordinal);
        }
        else
        {
            ExecuteBtn.IsEnabled = true;
        }
    }

    // ── Execute ───────────────────────────────────────────────────────────────

    private async void OnExecute(object? sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        SetStep(Step.Running);

        int totalActionable = _plan.ActionableCount;
        int done            = 0;
        int succeeded       = 0;
        int failed          = 0;

        ProgressBar.Maximum = totalActionable > 0 ? totalActionable : 1;
        ProgressBar.Value   = 0;
        StatDone.Text       = "0";
        StatSucceeded.Text  = "0";
        StatFailed.Text     = "0";
        ProgressLabel.Text  = "Starting…";

        var progress = new Progress<LibraryBulkOperationProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                done      = p.Done;
                succeeded += p.Success ? 1 : 0;
                failed    += p.Success ? 0 : 1;

                ProgressBar.Value  = p.Done;
                StatDone.Text      = p.Done.ToString("N0");
                StatSucceeded.Text = succeeded.ToString("N0");
                StatFailed.Text    = failed.ToString("N0");
                ProgressLabel.Text = p.ReleaseName;
            });
        });

        LibraryBulkOperationResult result = null!;
        try
        {
            result = await Task.Run(
                () => _service.Execute(_plan, _dbPath, progress));
        }
        catch (Exception ex)
        {
            result = new LibraryBulkOperationResult
            {
                Succeeded = 0,
                Failed    = 1,
                Skipped   = 0,
                Errors    = new[] { ex.Message },
            };
        }

        ShowResult(result);
    }

    private void ShowResult(LibraryBulkOperationResult result)
    {
        var op = OperationLabel(_currentOp);
        ResultSummary.Text =
            $"{op} complete.\n" +
            $"Succeeded: {result.Succeeded}  •  " +
            $"Skipped: {result.Skipped}  •  " +
            $"Failed: {result.Failed}";

        if (result.Errors.Count > 0)
        {
            ErrorBox.IsVisible = true;
            ErrorList.Children.Clear();
            foreach (var err in result.Errors)
            {
                var tb = new Avalonia.Controls.TextBlock
                {
                    Text       = err,
                    FontSize   = 11,
                    Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#EF9A9A")),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                };
                ErrorList.Children.Add(tb);
            }
        }
        else
        {
            ErrorBox.IsVisible = false;
        }

        SetStep(Step.Result);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void OnBack(object? sender, RoutedEventArgs e) => SetStep(Step.Input);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string OperationLabel(LibraryBulkOperationType op) => op switch
    {
        LibraryBulkOperationType.HideFromCatalog      => "Hide from Catalog",
        LibraryBulkOperationType.ShowInCatalog        => "Show in Catalog",
        LibraryBulkOperationType.PurgeAndMarkUnwanted => "Purge and Mark Unwanted",
        LibraryBulkOperationType.RestoreWanted        => "Restore Wanted",
        _                                             => op.ToString(),
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576L)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024L)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }
}

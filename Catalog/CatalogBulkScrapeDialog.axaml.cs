using System;
using System.Collections.Generic;
using System.Threading;
using Arkadia.Data;
using Arkadia.Library;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Arkadia;

public partial class CatalogBulkScrapeDialog : Window
{
    public CatalogBulkScrapeDialog() : this(null!, [], null, []) { }

    private readonly CatalogBulkScrapeService                _service;
    private readonly IReadOnlyList<LibraryEntry>             _allEntries;
    private readonly LibraryEntry?                           _selected;
    private readonly IReadOnlyList<MetadataValueMappingRecord> _mappings;
    private CancellationTokenSource?                         _cts;

    public CatalogBulkScrapeDialog(
        CatalogBulkScrapeService                  service,
        IReadOnlyList<LibraryEntry>               allEntries,
        LibraryEntry?                             selected,
        IReadOnlyList<MetadataValueMappingRecord> mappings)
    {
        InitializeComponent();
        _service    = service;
        _allEntries = allEntries;
        _selected   = selected;
        _mappings   = mappings;

        ScopeCurrentRelease.IsEnabled = selected is not null;
        HeaderSubtitle.Text = $"{allEntries.Count} entries in current DAT line";
    }

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        var scope   = GetSelectedScope();
        var entries = _service.FilterEntries(_allEntries, scope, _selected);

        if (entries.Count == 0) return;

        var options = new BulkScrapeOptions(
            Scope:                  scope,
            AutoApplyEmptyFieldsOnly: OptAutoApplyEmpty.IsChecked    == true,
            ExtractMissingMedia:      OptExtractMedia.IsChecked       == true,
            RespectExcludedMedia:     OptRespectExcluded.IsChecked    == true,
            OverwriteExistingMedia:   OptOverwriteExisting.IsChecked  == true);

        SettingsPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        StartBtn.IsVisible      = false;
        StopBtn.IsVisible       = true;
        ProgressBar.Maximum     = entries.Count;
        ProgressBar.Value       = 0;
        StatProcessed.Text      = $"0 / {entries.Count}";

        _cts = new CancellationTokenSource();

        var progress = new Progress<BulkScrapeProgress>(p =>
        {
            ProgressBar.Value  = p.Processed;
            StatProcessed.Text = $"{p.Processed} / {entries.Count}";
            StatMatched.Text   = p.Matched.ToString();
            StatNoMatch.Text   = p.NoMatch.ToString();
            StatAmbiguous.Text = p.Ambiguous.ToString();
            StatErrors.Text    = p.Errors.ToString();
            if (p.CurrentName.Length > 0)
                ProgressCurrentLabel.Text = p.CurrentName;
        });

        BulkScrapeReport report;
        try
        {
            report = await _service.RunAsync(entries, options, _mappings, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            ProgressCurrentLabel.Text = "Stopped.";
            StopBtn.IsVisible         = false;
            return;
        }

        ProgressPanel.IsVisible = false;
        ReportPanel.IsVisible   = true;
        StopBtn.IsVisible       = false;
        ShowReport(report, entries.Count);
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private BulkScrapeScope GetSelectedScope()
    {
        if (ScopeCurrentRelease.IsChecked == true) return BulkScrapeScope.CurrentRelease;
        if (ScopeEntireDat.IsChecked      == true) return BulkScrapeScope.EntireDat;
        return BulkScrapeScope.MissingOnly;
    }

    private void ShowReport(BulkScrapeReport report, int processedCount)
    {
        ReportSummary.Text =
            $"Processed {processedCount} — " +
            $"{report.TotalMatched} matched, {report.TotalNoMatch} no match, " +
            $"{report.TotalAmbiguous} ambiguous, {report.TotalErrors} errors. " +
            $"Fields applied: {report.TotalMetadataApplied}. Media: {report.TotalMediaExtracted} files.";

        foreach (var r in report.Results)
        {
            var (label, hex) = r.Status switch
            {
                BulkScrapeStatus.Matched   => ("Matched",   "#4CAF50"),
                BulkScrapeStatus.NoMatch   => ("No Match",  "#888899"),
                BulkScrapeStatus.Ambiguous => ("Ambiguous", "#FFD54F"),
                BulkScrapeStatus.Error     => ("Error",     "#EF5350"),
                _                          => ("?",         "#888899"),
            };

            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("72,*,Auto"),
                Margin            = new Avalonia.Thickness(0, 2, 0, 2),
            };

            row.Children.Add(new TextBlock
            {
                Text              = label,
                Foreground        = new SolidColorBrush(Color.Parse(hex)),
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                [Grid.ColumnProperty] = 0,
            });

            row.Children.Add(new TextBlock
            {
                Text              = r.ReleaseName,
                Foreground        = new SolidColorBrush(Color.Parse("#CCCCDD")),
                FontSize          = 11,
                TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Avalonia.Thickness(8, 0, 0, 0),
                [Grid.ColumnProperty] = 1,
            });

            string? detail = r.Status == BulkScrapeStatus.Matched
                ? $"+{r.MetadataFieldsApplied}f +{r.MediaExtracted}m"
                : r.ErrorMessage;

            if (detail is not null)
            {
                row.Children.Add(new TextBlock
                {
                    Text              = detail,
                    Foreground        = new SolidColorBrush(Color.Parse(
                        r.Status == BulkScrapeStatus.Error ? "#EF5350" : "#555577")),
                    FontSize          = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    Margin            = new Avalonia.Thickness(8, 0, 0, 0),
                    [Grid.ColumnProperty] = 2,
                });
            }

            ReportListPanel.Children.Add(row);
        }
    }
}

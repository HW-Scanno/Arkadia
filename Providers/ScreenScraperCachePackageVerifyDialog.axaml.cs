using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class ScreenScraperCachePackageVerifyDialog : Window
{
    private readonly CachePackageVerificationResult _result;

    public ScreenScraperCachePackageVerifyDialog() : this(null!) { }

    public ScreenScraperCachePackageVerifyDialog(CachePackageVerificationResult result)
    {
        InitializeComponent();
        _result = result;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Populate();
    }

    // ── Data population ───────────────────────────────────────────────────────

    private void Populate()
    {
        var r = _result;

        // Header
        HeaderFilename.Text = r.FileName.Length > 0 ? r.FileName : "(unknown)";

        // Status badge
        (StatusText.Text, StatusBadge.Background, StatusText.Foreground) = r.Status switch
        {
            "Valid"   => ("Valid",   new SolidColorBrush(Color.Parse("#152415")),
                          new SolidColorBrush(Color.Parse("#4CAF50"))),
            "Warning" => ("Warning", new SolidColorBrush(Color.Parse("#1E1A10")),
                          new SolidColorBrush(Color.Parse("#E0A040"))),
            _         => ("Error",   new SolidColorBrush(Color.Parse("#2A1215")),
                          new SolidColorBrush(Color.Parse("#EF5350"))),
        };

        var errCount  = r.Issues.Count(i => i.Severity == CachePackageVerificationSeverity.Error);
        var warnCount = r.Issues.Count(i => i.Severity == CachePackageVerificationSeverity.Warning);
        IssueCountText.Text = r.Issues.Count == 0
            ? "No issues"
            : $"{errCount} error{(errCount == 1 ? "" : "s")}  ·  {warnCount} warning{(warnCount == 1 ? "" : "s")}";

        // Summary
        SumIndexedGames.Text  = r.IndexedGameCount.ToString();
        SumPayloads.Text      = $"{r.PayloadsExpected} / {r.PayloadsFound} / {r.PayloadJsonValid}";
        SumPayloadsMissing.Text = r.PayloadsMissing.ToString();
        SumMedia.Text         = $"{r.IndexedMediaCount} / {r.MediaFilesFound} / {r.MediaFilesMissing}";
        SumZeroByte.Text      = r.ZeroByteMediaFiles.ToString();
        SumSanitization.Text  = $"{r.SanitizationErrors} / {r.SanitizationWarnings}";

        // Issues list
        if (r.Issues.Count == 0)
        {
            NoIssuesMsg.IsVisible = true;
            IssuesList.IsVisible  = false;
        }
        else
        {
            NoIssuesMsg.IsVisible = false;
            IssuesList.IsVisible  = true;

            var vms = r.Issues
                .OrderBy(i => i.Severity)   // Error < Warning < Info (enum order reversed)
                .Select(i => new IssueVm(i))
                .ToList();
            // Re-sort: Errors first, then Warnings, then Info
            vms = r.Issues
                .OrderBy(i => i.Severity switch
                {
                    CachePackageVerificationSeverity.Error   => 0,
                    CachePackageVerificationSeverity.Warning => 1,
                    _                                        => 2,
                })
                .Select(i => new IssueVm(i))
                .ToList();
            IssuesList.ItemsSource = vms;
        }
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ValidationMsg.Text      = "Clipboard not available.";
            ValidationMsg.IsVisible = true;
            return;
        }

        await clipboard.SetTextAsync(_result.ToReport());
        ValidationMsg.Text      = "Report copied to clipboard.";
        ValidationMsg.Foreground = new SolidColorBrush(Color.Parse("#4CAF50"));
        ValidationMsg.IsVisible  = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

// ── Issue view-model ──────────────────────────────────────────────────────────

internal sealed class IssueVm(CachePackageVerificationIssue issue)
{
    public string Area    => issue.Area;
    public string Message => issue.Message;

    public string SeverityLabel => issue.Severity switch
    {
        CachePackageVerificationSeverity.Error   => "ERROR",
        CachePackageVerificationSeverity.Warning => "WARN",
        _                                        => "INFO",
    };

    public IBrush SeverityBackground => issue.Severity switch
    {
        CachePackageVerificationSeverity.Error   => new SolidColorBrush(Color.Parse("#2A1215")),
        CachePackageVerificationSeverity.Warning => new SolidColorBrush(Color.Parse("#1E1A10")),
        _                                        => new SolidColorBrush(Color.Parse("#141428")),
    };

    public IBrush SeverityForeground => issue.Severity switch
    {
        CachePackageVerificationSeverity.Error   => new SolidColorBrush(Color.Parse("#EF7070")),
        CachePackageVerificationSeverity.Warning => new SolidColorBrush(Color.Parse("#E0A040")),
        _                                        => new SolidColorBrush(Color.Parse("#9FA4FF")),
    };

    public bool HasSeverityBadge => true;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class AmpExportReportDialog : Window
{
    private readonly AmpExportPlan           _plan;
    private          AmpPackageVerificationResult? _lastVerifyResult;

    public AmpExportReportDialog() : this(null!) { }

    public AmpExportReportDialog(AmpExportPlan plan)
    {
        InitializeComponent();
        _plan = plan;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Populate();
    }

    // ── Populate ──────────────────────────────────────────────────────────────

    private void Populate()
    {
        var p = _plan;
        int metaPct  = AmpReportHelpers.GetMetadataPercent(p);
        int mediaPct = AmpReportHelpers.GetMediaPercent(p);
        int errors   = AmpReportHelpers.GetErrorCount(p);
        int warnings = AmpReportHelpers.GetWarningCount(p);

        HeaderSubtitle.Text = $"{p.SystemName}  ·  {p.DatLineId}";

        SumSystem.Text     = p.SystemName;
        SumDatLine.Text    = p.DatLineId;
        SumReleases.Text   = p.ReleaseCount.ToString();
        SumMetadata.Text   = $"{p.ReleasesWithMetadata} of {p.ReleaseCount}  ({metaPct}%)";
        SumMedia.Text      = $"{p.ReleasesWithMedia} of {p.ReleaseCount}  ({mediaPct}%)";
        SumFiles.Text      = p.TotalMediaFiles.ToString();
        SumSize.Text       = AmpReportHelpers.FormatBytes(p.TotalBytes);
        SumExclusions.Text = p.ExclusionCount.ToString();
        SumExtraNotes.Text = p.ExtraNotesCount.ToString();

        SumErrors.Text     = errors.ToString();
        SumWarnings.Text   = warnings.ToString();

        // Color the error/warning count
        SumErrors.Foreground   = errors   > 0
            ? new SolidColorBrush(Color.Parse("#EF7070"))
            : new SolidColorBrush(Color.Parse("#CCCCDD"));
        SumWarnings.Foreground = warnings > 0
            ? new SolidColorBrush(Color.Parse("#E0A040"))
            : new SolidColorBrush(Color.Parse("#CCCCDD"));

        PopulateIssues(p);
        PopulateReleases(p);
    }

    private void PopulateIssues(AmpExportPlan p)
    {
        // Collect plan-level issues first, then all release-level issues
        var allIssues = new List<AmpIssueVm>();

        foreach (var issue in p.Issues)
            allIssues.Add(new AmpIssueVm("(plan)", issue));

        foreach (var rel in p.Releases)
        {
            var relName = rel.Title.Length > 0 ? rel.Title : rel.DatName;
            foreach (var issue in rel.Issues)
                allIssues.Add(new AmpIssueVm(relName, issue));
        }

        // Sort: errors first, then warnings, then info
        var sorted = allIssues
            .OrderBy(v => v.SeverityOrder)
            .ToList();

        if (sorted.Count == 0)
        {
            NoIssuesMsg.IsVisible = true;
            IssuesList.IsVisible  = false;
        }
        else
        {
            NoIssuesMsg.IsVisible  = false;
            IssuesList.IsVisible   = true;
            IssuesList.ItemsSource = sorted;
        }
    }

    private void PopulateReleases(AmpExportPlan p)
    {
        if (p.Releases.Count == 0)
        {
            NoReleasesMsg.IsVisible = true;
            ReleasesList.IsVisible  = false;
            return;
        }

        NoReleasesMsg.IsVisible  = false;
        ReleasesList.IsVisible   = true;
        ReleasesList.ItemsSource = p.Releases.Select(r => new AmpReleaseVm(r)).ToList();
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnCreateAmp(object? sender, RoutedEventArgs e)
    {
        if (!CanCreateAmp())
        {
            SetCreateStatus("Cannot create AMP: plan has errors. Resolve issues first.", isError: true);
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save AMP As",
            SuggestedFileName = AmpReportHelpers.SuggestedAmpFileName(_plan),
            DefaultExtension  = "amp",
            FileTypeChoices   = [new FilePickerFileType("Arkadia Media Pack") { Patterns = ["*.amp"] }],
        });

        if (file?.TryGetLocalPath() is not string path)
            return;

        CreateAmpBtn.IsEnabled = false;
        CreateAmpBtn.Content   = "Creating…";
        ClearCreateStatus();

        try
        {
            var writer      = new AmpExportWriterService();
            var writeResult = await Task.Run(() => writer.Write(_plan, path, overwrite: true));

            CreateAmpBtn.Content = "Verifying…";

            var verifier = new AmpPackageVerifierService();
            var result   = await Task.Run(() => verifier.Verify(path));

            _lastVerifyResult = result;
            ShowCreateResult(result, writeResult.Sha256, writeResult.PackageBytes);
        }
        catch (Exception ex)
        {
            SetCreateStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            RestoreCreateButton();
        }
    }

    private void ShowCreateResult(AmpPackageVerificationResult r, string sha256, long bytes)
    {
        ResultStatus.Text = r.Status;
        ResultStatus.Foreground = r.Status switch
        {
            "Valid"   => new SolidColorBrush(Color.Parse("#4CAF50")),
            "Warning" => new SolidColorBrush(Color.Parse("#E0A040")),
            _         => new SolidColorBrush(Color.Parse("#EF5350")),
        };

        ResultPath.Text     = r.AmpFilePath;
        ResultSize.Text     = AmpReportHelpers.FormatBytes(bytes);
        ResultSha256.Text   = sha256.Length > 0 ? sha256 : "—";
        ResultVerifier.Text = r.HasErrors
            ? $"{r.Issues.Count(i => i.Severity == AmpPackageVerificationSeverity.Error)} error(s)"
            : r.HasWarnings
                ? $"{r.Issues.Count(i => i.Severity == AmpPackageVerificationSeverity.Warning)} warning(s)"
                : "All checks passed";

        CreateResultPanel.IsVisible = true;
        CopyReportBtn.IsVisible     = true;
        ClearCreateStatus();
    }

    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (_lastVerifyResult is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_lastVerifyResult.ToReport());
        SetCreateStatus("Report copied to clipboard.");
    }

    // ── Create helpers ────────────────────────────────────────────────────────

    private bool CanCreateAmp() => !AmpReportHelpers.HasErrors(_plan);

    private void RestoreCreateButton()
    {
        CreateAmpBtn.IsEnabled = true;
        CreateAmpBtn.Content   = "Create AMP";
    }

    private void SetCreateStatus(string message, bool isError = false)
    {
        CreateStatusMsg.Text       = message;
        CreateStatusMsg.Foreground = isError
            ? new SolidColorBrush(Color.Parse("#EF7070"))
            : new SolidColorBrush(Color.Parse("#999AAA"));
        CreateStatusMsg.IsVisible  = true;
    }

    private void ClearCreateStatus()
    {
        CreateStatusMsg.IsVisible = false;
        CreateStatusMsg.Text      = "";
    }
}

// ── Issue view-model ──────────────────────────────────────────────────────────

internal sealed class AmpIssueVm(string releaseName, AmpExportPlanIssue issue)
{
    public string ReleaseName => releaseName;
    public string Area        => issue.Area;
    public string Message     => issue.Message;

    public int SeverityOrder => issue.Severity switch
    {
        AmpExportPlanSeverity.Error   => 0,
        AmpExportPlanSeverity.Warning => 1,
        _                             => 2,
    };

    public string SeverityLabel => issue.Severity switch
    {
        AmpExportPlanSeverity.Error   => "ERROR",
        AmpExportPlanSeverity.Warning => "WARN",
        _                             => "INFO",
    };

    public IBrush SeverityBackground => issue.Severity switch
    {
        AmpExportPlanSeverity.Error   => new SolidColorBrush(Color.Parse("#2A1215")),
        AmpExportPlanSeverity.Warning => new SolidColorBrush(Color.Parse("#1E1A10")),
        _                             => new SolidColorBrush(Color.Parse("#141428")),
    };

    public IBrush SeverityForeground => issue.Severity switch
    {
        AmpExportPlanSeverity.Error   => new SolidColorBrush(Color.Parse("#EF7070")),
        AmpExportPlanSeverity.Warning => new SolidColorBrush(Color.Parse("#E0A040")),
        _                             => new SolidColorBrush(Color.Parse("#9FA4FF")),
    };
}

// ── Release view-model ────────────────────────────────────────────────────────

internal sealed class AmpReleaseVm(AmpExportPlanRelease release)
{
    public string DisplayName   => release.Title.Length > 0 ? release.Title : release.DatName;
    public bool   HasMetadata   => release.HasMetadata;
    public string MediaLabel    => $"{release.MediaEntries.Count} media";
    public string IssueLabel    => release.Issues.Count == 0 ? "" : $"{release.Issues.Count} issue{(release.Issues.Count == 1 ? "" : "s")}";

    public IBrush IssueColor => release.Issues.Any(i => i.Severity == AmpExportPlanSeverity.Error)
        ? new SolidColorBrush(Color.Parse("#EF7070"))
        : release.Issues.Any(i => i.Severity == AmpExportPlanSeverity.Warning)
            ? new SolidColorBrush(Color.Parse("#E0A040"))
            : new SolidColorBrush(Color.Parse("#888899"));
}

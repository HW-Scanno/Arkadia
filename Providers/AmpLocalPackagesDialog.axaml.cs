using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class AmpLocalPackagesDialog : Window
{
    private readonly AmpLocalRegistryService          _registry;
    private          IReadOnlyList<AmpLocalPackageInfo> _packages = [];
    private          AmpLocalPackageInfo?               _selected;
    private          bool                               _isLoading;
    private          bool                               _isVerifying;
    private          bool                               _isRegistering;

    public AmpLocalPackagesDialog() : this(null!) { }

    public AmpLocalPackagesDialog(string dataDir)
    {
        InitializeComponent();
        _registry = new AmpLocalRegistryService(dataDir);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _ = RefreshAsync();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        _isLoading = true;
        SetStatus("Loading local AMP packages…");
        UpdateButtons();

        try
        {
            _packages = await Task.Run(() => _registry.ListPackages());
            ClearStatus();
            RebuildList(preserveSelection: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load packages: {ex.Message}", isError: true);
        }
        finally
        {
            _isLoading = false;
            UpdateButtons();
        }
    }

    private void RebuildList(bool preserveSelection)
    {
        var selectedPath = _selected?.FilePath;
        var vms          = _packages.Select(p => new AmpPackageVm(p)).ToList();

        PackageList.ItemsSource    = vms;
        EmptyPackagesMsg.IsVisible = _packages.Count == 0;

        if (preserveSelection && selectedPath is not null)
        {
            var idx = vms.FindIndex(v => v.Info.FilePath == selectedPath);
            if (idx >= 0)
            {
                PackageList.SelectedIndex = idx;
                _selected                 = _packages[idx];
                ShowDetail(_selected);
                return;
            }
        }

        PackageList.SelectedItem = null;
        _selected                = null;
        ShowPlaceholder();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void OnPackageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = (PackageList.SelectedItem as AmpPackageVm)?.Info;
        if (_selected is not null)
            ShowDetail(_selected);
        else
            ShowPlaceholder();
        UpdateButtons();
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private void OnRefresh(object? sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async void OnAddLocalAmp(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Add Arkadia Media Pack",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Arkadia Media Pack") { Patterns = ["*.amp"] }
            ]
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not string sourcePath) return;

        _isRegistering = true;
        SetStatus("Adding local AMP package…");
        UpdateButtons();

        try
        {
            var info = await Task.Run(() =>
                _registry.RegisterLocalPackage(sourcePath, overwrite: false));

            var list = _packages.ToList();
            var idx  = list.FindIndex(p => p.FilePath == info.FilePath);
            if (idx >= 0)
                list[idx] = info;
            else
                list.Add(info);
            _packages = list.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ToList();

            _selected = info;
            RebuildList(preserveSelection: true);

            SetStatus(info.HasWarnings
                ? "Package added with warnings."
                : "Package added successfully.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to add package: {ex.Message}", isError: true);
        }
        finally
        {
            _isRegistering = false;
            UpdateButtons();
        }
    }

    private async void OnVerifySelected(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        _isVerifying              = true;
        VerifySelectedBtn.Content = "Verifying…";
        ClearStatus();
        UpdateButtons();

        try
        {
            var verified = await Task.Run(() => _registry.VerifyPackage(_selected.FilePath));

            var list = _packages.ToList();
            var idx  = list.FindIndex(p => p.FilePath == _selected.FilePath);
            if (idx >= 0) list[idx] = verified;
            _packages = list;
            _selected = verified;

            RebuildList(preserveSelection: true);

            SetStatus(verified.Status switch
            {
                "Valid"   => "Package verified successfully.",
                "Warning" => "Package verified with warnings.",
                _         => "Package verification failed.",
            }, isError: verified.HasErrors);
        }
        catch (Exception ex)
        {
            SetStatus($"Verification error: {ex.Message}", isError: true);
        }
        finally
        {
            _isVerifying              = false;
            VerifySelectedBtn.Content = "Verify Selected";
            UpdateButtons();
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        _registry.EnsureFolder();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = _registry.RegistryFolder,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (_selected?.VerificationResult is not { } result) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(result.ToReport());
        SetStatus("Verification report copied to clipboard.");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── Detail panel ──────────────────────────────────────────────────────────

    private void ShowPlaceholder()
    {
        DetailPlaceholder.IsVisible = true;
        DetailPanel.IsVisible       = false;
    }

    private void ShowDetail(AmpLocalPackageInfo p)
    {
        DetailPlaceholder.IsVisible = false;
        DetailPanel.IsVisible       = true;

        DetailFilePath.Text        = p.FilePath;
        DetailSha256.Text          = p.PackageSha256.Length > 0 ? p.PackageSha256 : "—";
        DetailFormat.Text          = p.FormatName.Length > 0
            ? $"{p.FormatName} v{p.FormatVersion}"
            : "—";
        DetailSystem.Text          = p.SystemName.Length  > 0 ? p.SystemName  : "—";
        DetailDatLine.Text         = p.DatLineId.Length   > 0 ? p.DatLineId   : "—";
        DetailReleases.Text        = p.ReleaseCount.ToString();
        DetailMediaFiles.Text      = p.MediaFileCount.ToString();
        DetailPackageSize.Text     = AmpReportHelpers.FormatBytes(p.PackageBytes);
        DetailTotalMediaBytes.Text = AmpReportHelpers.FormatBytes(p.TotalMediaBytes);
        DetailExclusions.Text      = p.ExclusionCount.ToString();
        DetailExtraNotes.Text      = p.ExtraNotesCount.ToString();
        DetailModified.Text        = p.LastWriteTimeUtc == default
            ? "—"
            : p.LastWriteTimeUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        DetailVerifierStatus.Text = p.Status switch
        {
            "Valid"      => "Verified — no issues found.",
            "Warning"    => "Verified — warnings detected.",
            "Error"      => "Verified — errors detected.",
            "Unreadable" => "Unreadable — manifest could not be parsed.",
            _            => "Not yet verified. Click Verify Selected to run a full check.",
        };
        DetailVerifierStatus.Foreground = new SolidColorBrush(p.Status switch
        {
            "Valid"                 => Color.Parse("#4CAF50"),
            "Warning"               => Color.Parse("#E0A040"),
            "Error" or "Unreadable" => Color.Parse("#EF5350"),
            _                       => Color.Parse("#888899"),
        });
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void UpdateButtons()
    {
        var busy = _isLoading || _isVerifying || _isRegistering;
        AddLocalAmpBtn.IsEnabled    = !busy;
        RefreshBtn.IsEnabled        = !busy;
        VerifySelectedBtn.IsEnabled = _selected is not null && !busy;
        CopyReportBtn.IsVisible     = _selected?.VerificationResult is not null;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusMsg.Text       = message;
        StatusMsg.Foreground = isError
            ? new SolidColorBrush(Color.Parse("#EF5350"))
            : new SolidColorBrush(Color.Parse("#888899"));
        StatusMsg.IsVisible  = true;
    }

    private void ClearStatus() => StatusMsg.IsVisible = false;
}

// ── Package view-model ────────────────────────────────────────────────────────

internal sealed class AmpPackageVm(AmpLocalPackageInfo info)
{
    public AmpLocalPackageInfo Info => info;

    public string FileName           => info.FileName;
    public string StatusLabel        => info.Status;
    public string SystemName         => info.SystemName.Length > 0 ? info.SystemName : "—";
    public string DatLineId          => info.DatLineId.Length  > 0 ? info.DatLineId  : "—";
    public string ReleaseCountText   => info.ReleaseCount.ToString();
    public string MediaFileCountText => info.MediaFileCount.ToString();
    public string SizeFormatted      => AmpReportHelpers.FormatBytes(info.PackageBytes);
    public string ModifiedShort      => info.LastWriteTimeUtc == default
        ? "—"
        : info.LastWriteTimeUtc.LocalDateTime.ToString("yyyy-MM-dd");

    public IBrush StatusBackground => new SolidColorBrush(info.Status switch
    {
        "Valid"                 => Color.Parse("#152415"),
        "Warning"               => Color.Parse("#1E1A10"),
        "Error" or "Unreadable" => Color.Parse("#2A1215"),
        _                       => Color.Parse("#1A1A2C"),
    });

    public IBrush StatusForeground => new SolidColorBrush(info.Status switch
    {
        "Valid"                 => Color.Parse("#4CAF50"),
        "Warning"               => Color.Parse("#E0A040"),
        "Error" or "Unreadable" => Color.Parse("#EF5350"),
        _                       => Color.Parse("#888899"),
    });
}

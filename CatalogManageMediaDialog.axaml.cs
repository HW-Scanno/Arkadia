using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Arkadia.Data;
using Arkadia.Library;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class CatalogManageMediaDialog : Window
{
    private readonly LibraryEntry                  _entry;
    private readonly string                        _dataDir;
    private readonly ReleaseMediaCurationService   _service;

    private List<MediaAssetVm>  _allVms    = [];
    private MediaAssetVm?       _selected;
    private Bitmap?             _previewBitmap;
    private string?             _addMediaSourcePath;

    public CatalogManageMediaDialog() : this(null!, null!) { }

    public CatalogManageMediaDialog(LibraryEntry entry, string dataDir)
    {
        InitializeComponent();
        _entry   = entry;
        _dataDir = dataDir;
        _service = new ReleaseMediaCurationService(dataDir);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        var displayTitle = LibraryTitleResolver.Resolve(_entry.Name, "catalog", _entry.Metadata?.Title);
        HeaderTitle.Text = $"Manage Media — {displayTitle}";
        Title            = $"Manage Media — {displayTitle}";

        PopulateAddMediaTypes();
        RefreshAssets();
    }

    // ── Populate ──────────────────────────────────────────────────────────────

    private void RefreshAssets()
    {
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        PreviewImage.Source = null;

        var assets = _service.LoadAssets(
            _entry.DbPath, _entry.ReleaseId, _entry.Name,
            _entry.HardwareFamilyId, _entry.DatLineId);

        _allVms = assets.Select(a => new MediaAssetVm(a)).ToList();

        var prev = _selected?.Asset.FilePath;
        AssetList.ItemsSource  = _allVms;
        EmptyMsg.IsVisible     = _allVms.Count == 0;

        _selected = null;
        if (prev is not null)
        {
            var match = _allVms.FirstOrDefault(v =>
                string.Equals(v.Asset.FilePath, prev, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                AssetList.SelectedItem = match;
                AssetList.ScrollIntoView(match);
            }
        }

        if (_selected is null)
            ShowDetailEmpty();

        HideValidation();
    }

    private void PopulateAddMediaTypes()
    {
        AddMediaTypePicker.ItemsSource   = ReleaseMediaCurationService.MediaTypeOrder;
        AddMediaTypePicker.SelectedIndex = 0;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void OnAssetSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected = AssetList.SelectedItem as MediaAssetVm;
        UpdateDetailPanel();
    }

    private void UpdateDetailPanel()
    {
        if (_selected is null)
        {
            ShowDetailEmpty();
            return;
        }

        var a = _selected.Asset;
        DetailPanel.IsVisible = true;
        DetailEmpty.IsVisible = false;

        // Info fields
        InfoFilename.Text = a.FileName;
        InfoType.Text     = a.MediaType;
        InfoSize.Text     = a.SizeDisplay;
        InfoStatus.Text   = a.StatusLabel;
        InfoSha256.Text   = a.Sha256 ?? "—";

        // Credits
        CreditsField.Text = a.Credits ?? "";

        // Preview
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        PreviewImage.IsVisible      = false;
        PreviewPlaceholder.IsVisible = true;

        if (a.Exists)
        {
            var ext = Path.GetExtension(a.FilePath).ToLowerInvariant();
            if (MediaStore.ImageExtensions.Contains(ext))
            {
                try
                {
                    _previewBitmap = new Bitmap(a.FilePath);
                    PreviewImage.Source     = _previewBitmap;
                    PreviewImage.IsVisible  = true;
                    PreviewPlaceholder.IsVisible = false;
                }
                catch
                {
                    PreviewPlaceholder.Text = "Preview unavailable";
                }
            }
            else if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".webm")
            {
                PreviewPlaceholder.Text = "▶  Video file";
            }
            else if (ext is ".pdf")
            {
                PreviewPlaceholder.Text = "📄  PDF document";
            }
            else
            {
                PreviewPlaceholder.Text = Path.GetExtension(a.FilePath).TrimStart('.').ToUpperInvariant() + " file";
            }
        }
        else
        {
            PreviewPlaceholder.Text = "File missing from disk";
        }

        // Action button states
        SetPreferredBtn.IsEnabled = a.Exists && !a.IsPreferred && !a.IsExcluded;
        ExcludeBtn.IsEnabled      = !a.IsExcluded;
        RestoreBtn.IsEnabled      = a.IsExcluded;
        OpenFileBtn.IsEnabled     = a.Exists;
        OpenFolderBtn.IsEnabled   = a.Exists;
        DeleteFileBtn.IsEnabled   = a.Exists;
        SaveCreditsBtn.IsEnabled  = true;
    }

    private void ShowDetailEmpty()
    {
        DetailPanel.IsVisible = false;
        DetailEmpty.IsVisible = true;
        _previewBitmap?.Dispose();
        _previewBitmap      = null;
        PreviewImage.Source = null;
    }

    // ── Asset actions ─────────────────────────────────────────────────────────

    private void OnSetPreferred(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var a = _selected.Asset;
        try
        {
            _service.SetPreferred(_entry.DbPath, a.ReleaseId, a.MediaType, a.FilePath);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnExclude(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var a = _selected.Asset;
        try
        {
            _service.Exclude(_entry.DbPath, a.ReleaseId, a.MediaType, a.FilePath, reason: null);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var a = _selected.Asset;
        try
        {
            _service.Restore(_entry.DbPath, a.ReleaseId, a.MediaType, a.FilePath);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnSaveCredits(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var a = _selected.Asset;
        try
        {
            _service.SaveCredits(_entry.DbPath, a.ReleaseId, a.MediaType, a.FilePath,
                CreditsField.Text);
            ShowValidation("Credits saved.", isError: false);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Asset.FilePath is not string path || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { ShowValidation($"Could not open file: {ex.Message}"); }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Asset.FilePath is not string path || !File.Exists(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { ShowValidation($"Could not open folder: {ex.Message}"); }
    }

    private async void OnDeleteFile(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var a    = _selected.Asset;
        var name = Path.GetFileName(a.FilePath);

        var confirmed = await new ConfirmDialog(
            "Delete Media File",
            $"Delete \"{name}\" from disk?\n\n" +
            "The curation record will remain excluded so this asset is not reintroduced later.\n\n" +
            "This cannot be undone.")
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        try
        {
            _service.DeleteMediaFile(_entry.DbPath, a.ReleaseId, a.MediaType, a.FilePath);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Delete failed: {ex.Message}"); }
    }

    // ── Add Media flow ────────────────────────────────────────────────────────

    private async void OnAddMedia(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Select Media File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image Files")   { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] },
                new FilePickerFileType("Video Files")   { Patterns = ["*.mp4", "*.avi", "*.mkv", "*.mov", "*.webm"] },
                new FilePickerFileType("Document Files"){ Patterns = ["*.pdf"] },
                new FilePickerFileType("All Files")     { Patterns = ["*.*"] },
            ],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not string path) return;

        _addMediaSourcePath            = path;
        AddMediaSourceLabel.Text       = path;
        AddMediaPanel.IsVisible        = true;
        AddMediaTypePicker.SelectedIndex = 0;
        HideValidation();
    }

    private void OnAddMediaConfirm(object? sender, RoutedEventArgs e)
    {
        if (_addMediaSourcePath is null) return;
        var mediaType = AddMediaTypePicker.SelectedItem as string;
        if (mediaType is null) return;

        try
        {
            _service.AddMediaFile(
                _entry.DbPath, _entry.ReleaseId, _entry.Name,
                _entry.HardwareFamilyId, _entry.DatLineId,
                _addMediaSourcePath, mediaType);

            AddMediaPanel.IsVisible = false;
            _addMediaSourcePath     = null;
            ShowValidation($"Media added as {mediaType}.", isError: false);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Add failed: {ex.Message}"); }
    }

    private void OnAddMediaCancel(object? sender, RoutedEventArgs e)
    {
        AddMediaPanel.IsVisible = false;
        _addMediaSourcePath     = null;
    }

    // ── Global ────────────────────────────────────────────────────────────────

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshAssets();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void ShowValidation(string message, bool isError = true)
    {
        ValidationMsg.Text       = message;
        ValidationMsg.Foreground = isError
            ? new SolidColorBrush(Color.Parse("#EF5350"))
            : new SolidColorBrush(Color.Parse("#4CAF50"));
        ValidationMsg.IsVisible = true;
    }

    private void HideValidation() => ValidationMsg.IsVisible = false;
}

// ── View model ────────────────────────────────────────────────────────────────

internal sealed class MediaAssetVm(ReleaseMediaAsset asset)
{
    public ReleaseMediaAsset Asset => asset;

    public string FileName      => asset.FileName;
    public string MediaTypeLabel => asset.MediaType.ToUpperInvariant();
    public string SizeDisplay   => asset.SizeDisplay;
    public string StatusLabel   => asset.StatusLabel;

    public IBrush StatusBackground => asset.StatusLabel switch
    {
        "Preferred"    => new SolidColorBrush(Color.Parse("#152415")),
        "Excluded"     => new SolidColorBrush(Color.Parse("#2A1215")),
        "Missing"      => new SolidColorBrush(Color.Parse("#1A1215")),
        _              => new SolidColorBrush(Color.Parse("#141430")),
    };

    public IBrush StatusForeground => asset.StatusLabel switch
    {
        "Preferred" => new SolidColorBrush(Color.Parse("#4CAF50")),
        "Excluded"  => new SolidColorBrush(Color.Parse("#EF5350")),
        "Missing"   => new SolidColorBrush(Color.Parse("#FF8A65")),
        _           => new SolidColorBrush(Color.Parse("#7070AA")),
    };
}

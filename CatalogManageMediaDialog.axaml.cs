using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private readonly IReadOnlyList<LibraryEntry>  _allEntries;
    private int                                   _currentIndex;
    private readonly string                       _baseDir;
    private readonly string                       _dataDir;
    private readonly string                       _incomingDir;
    private readonly ReleaseMediaCurationService  _service;

    // Left pane
    private List<MediaAssetVm>   _allVms   = [];
    private MediaAssetVm?        _selected;
    private Bitmap?              _previewBitmap;
    private string?              _typeFilter;

    // Right pane
    private string               _incomingBrowseDir;
    private List<IncomingFileVm> _incomingFiles = [];
    private IncomingFileVm?      _incomingSelected;
    private Bitmap?              _incomingPreviewBitmap;

    private LibraryEntry? CurrentEntry =>
        _allEntries?.Count > 0 && _currentIndex >= 0 && _currentIndex < _allEntries.Count
            ? _allEntries[_currentIndex] : null;

    public CatalogManageMediaDialog() : this(null!, 0, null!) { }

    public CatalogManageMediaDialog(IReadOnlyList<LibraryEntry> allEntries, int initialIndex, string baseDir)
    {
        InitializeComponent();
        _allEntries        = allEntries;
        _currentIndex      = Math.Max(0, Math.Min(initialIndex, Math.Max((allEntries?.Count ?? 1) - 1, 0)));
        _baseDir           = baseDir ?? "";
        _dataDir           = string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, "data");
        _incomingDir       = string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, ArkadiaFolders.IncomingMedia);
        _incomingBrowseDir = _incomingDir;
        _service           = string.IsNullOrEmpty(_dataDir) ? null! : new ReleaseMediaCurationService(_dataDir);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_allEntries is null || _allEntries.Count == 0) return;

        PopulateTypeFilter();
        PopulateImportTypes();
        EnsureIncomingDir();
        LoadRelease();
        RefreshIncoming();
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private void PopulateTypeFilter()
    {
        var items = new List<string> { "All" };
        items.AddRange(ReleaseMediaCurationService.MediaTypeOrder);
        TypeFilterPicker.ItemsSource   = items;
        TypeFilterPicker.SelectedIndex = 0;
        _typeFilter = null;
    }

    private void PopulateImportTypes()
    {
        ImportTypePicker.ItemsSource   = ReleaseMediaCurationService.MediaTypeOrder;
        ImportTypePicker.SelectedIndex = 0;
    }

    private void EnsureIncomingDir()
    {
        if (!string.IsNullOrEmpty(_incomingDir))
            try { Directory.CreateDirectory(_incomingDir); } catch { }
    }

    // ── Release navigation ────────────────────────────────────────────────────

    private void LoadRelease()
    {
        var entry = CurrentEntry;
        if (entry is null) return;

        var displayTitle = LibraryTitleResolver.Resolve(entry.Name, "catalog", entry.Metadata?.Title);
        HeaderTitle.Text = $"Manage Media — {displayTitle}";
        Title            = $"Manage Media — {displayTitle}";

        var total              = _allEntries?.Count ?? 0;
        NavLabel.Text          = total > 1 ? $"{_currentIndex + 1} of {total}" : "";
        PrevEntryBtn.IsEnabled = _currentIndex > 0;
        NextEntryBtn.IsEnabled = _currentIndex < (total - 1);

        RefreshAssets();
    }

    private void OnPrevEntry(object? sender, RoutedEventArgs e)
    {
        if (_currentIndex <= 0) return;
        _currentIndex--;
        LoadRelease();
    }

    private void OnNextEntry(object? sender, RoutedEventArgs e)
    {
        var total = _allEntries?.Count ?? 0;
        if (_currentIndex >= total - 1) return;
        _currentIndex++;
        LoadRelease();
    }

    // ── Left pane: asset list ─────────────────────────────────────────────────

    private void RefreshAssets()
    {
        var entry = CurrentEntry;
        if (entry is null) return;

        _previewBitmap?.Dispose();
        _previewBitmap      = null;
        PreviewImage.Source = null;

        var assets = _service.LoadAssets(
            entry.DbPath, entry.ReleaseId, entry.Name,
            entry.HardwareFamilyId, entry.DatLineId);

        _allVms = assets.Select(a => new MediaAssetVm(a)).ToList();
        ApplyTypeFilter();
    }

    private void ApplyTypeFilter()
    {
        var filtered = _typeFilter is null
            ? _allVms
            : _allVms.Where(v => v.Asset.MediaType == _typeFilter).ToList();

        IReadOnlyList<object> display = _typeFilter is null
            ? CatalogMediaListHelpers.BuildGroupedDisplay(filtered)
            : filtered.Cast<object>().ToList();

        var prev              = _selected?.Asset.FilePath;
        AssetList.ItemsSource = display;
        EmptyMsg.IsVisible    = _allVms.Count == 0;

        _selected = null;
        if (prev is not null)
        {
            var match = display.OfType<MediaAssetVm>().FirstOrDefault(v =>
                string.Equals(v.Asset.FilePath, prev, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                AssetList.SelectedItem = match;
                AssetList.ScrollIntoView(match);
            }
        }

        if (_selected is null) ShowDetailEmpty();
        HideValidation();
    }

    private void OnTypeFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        var sel = TypeFilterPicker.SelectedItem as string;
        _typeFilter = sel == "All" ? null : sel;
        ApplyTypeFilter();
    }

    private void OnAssetSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AssetList.SelectedItem is MediaGroupHeaderVm)
        {
            AssetList.SelectedItem = null;
            return;
        }
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

        InfoFilename.Text = a.FileName;
        InfoType.Text     = a.MediaType;
        InfoSize.Text     = a.SizeDisplay;
        InfoStatus.Text   = a.StatusLabel;
        var hasSha = a.Sha256 is not null;
        InfoSha256Key.IsVisible = hasSha;
        InfoSha256.IsVisible    = hasSha;
        InfoSha256.Text         = a.Sha256 ?? "";

        CreditsField.Text = a.Credits ?? "";

        _previewBitmap?.Dispose();
        _previewBitmap = null;
        PreviewImage.IsVisible        = false;
        PreviewPlaceholder.IsVisible  = true;

        if (a.Exists)
        {
            var ext = Path.GetExtension(a.FilePath).ToLowerInvariant();
            if (MediaStore.ImageExtensions.Contains(ext))
            {
                try
                {
                    _previewBitmap               = new Bitmap(a.FilePath);
                    PreviewImage.Source          = _previewBitmap;
                    PreviewImage.IsVisible       = true;
                    PreviewPlaceholder.IsVisible = false;
                }
                catch { PreviewPlaceholder.Text = "Preview unavailable"; }
            }
            else if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".webm")
                PreviewPlaceholder.Text = "▶  Video file";
            else if (ext is ".pdf")
                PreviewPlaceholder.Text = "📄  PDF document";
            else
                PreviewPlaceholder.Text = ext.TrimStart('.').ToUpperInvariant() + " file";
        }
        else
        {
            PreviewPlaceholder.Text = "File missing from disk";
        }

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

    // ── Left pane: curation actions ───────────────────────────────────────────

    private void OnSetPreferred(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || CurrentEntry is null) return;
        var a = _selected.Asset;
        try
        {
            _service.SetPreferred(CurrentEntry.DbPath, a.ReleaseId, a.MediaType, a.FilePath);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnExclude(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || CurrentEntry is null) return;
        var a = _selected.Asset;
        try
        {
            _service.Exclude(CurrentEntry.DbPath, a.ReleaseId, a.MediaType, a.FilePath, reason: null);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || CurrentEntry is null) return;
        var a = _selected.Asset;
        try
        {
            _service.Restore(CurrentEntry.DbPath, a.ReleaseId, a.MediaType, a.FilePath);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnSaveCredits(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || CurrentEntry is null) return;
        var a = _selected.Asset;
        try
        {
            _service.SaveCredits(CurrentEntry.DbPath, a.ReleaseId, a.MediaType, a.FilePath, CreditsField.Text);
            ShowValidation("Credits saved.", isError: false);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Error: {ex.Message}"); }
    }

    private void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Asset.FilePath is not string path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { ShowValidation($"Could not open file: {ex.Message}"); }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Asset.FilePath is not string path || !File.Exists(path)) return;
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
        if (_selected is null || CurrentEntry is null) return;
        var a    = _selected.Asset;
        var name = Path.GetFileName(a.FilePath);

        var body = a.IsExcluded
            ? $"This asset is currently excluded.\n\n" +
              $"Deleting \"{name}\" will remove both the file and its exclusion record, " +
              $"so it may be reintroduced by a future scrape or import.\n\n" +
              $"If you want to keep the exclusion, close this dialog and do not delete."
            : $"Delete \"{name}\" from disk and remove its media record from Arkadia?\n\n" +
              $"This will not create an exclusion, so the asset may be reintroduced by a future " +
              $"scrape or import. Use Exclude instead to permanently reject an asset.";

        var confirmed = await new ConfirmDialog("Delete Media File", body)
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        try
        {
            _service.DeleteMediaFile(CurrentEntry.DbPath, a.ReleaseId, a.MediaType, a.FilePath);
            RefreshAssets();
        }
        catch (Exception ex) { ShowValidation($"Delete failed: {ex.Message}"); }
    }

    // ── Right pane: incoming media ────────────────────────────────────────────

    private void RefreshIncoming()
    {
        _incomingFiles.Clear();
        _incomingSelected          = null;
        IncomingList.ItemsSource   = null;
        IncomingFolderLabel.Text   = _incomingBrowseDir;
        ImportBtn.IsEnabled        = false;

        if (!Directory.Exists(_incomingBrowseDir))
        {
            IncomingEmptyMsg.Text      = "Folder not found.";
            IncomingEmptyMsg.IsVisible = true;
            ShowIncomingPreviewEmpty();
            return;
        }

        try
        {
            _incomingFiles = Directory
                .EnumerateFiles(_incomingBrowseDir)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Select(f => new IncomingFileVm(f))
                .ToList();
        }
        catch (Exception ex)
        {
            IncomingEmptyMsg.Text      = $"Error reading folder: {ex.Message}";
            IncomingEmptyMsg.IsVisible = true;
            ShowIncomingPreviewEmpty();
            return;
        }

        IncomingList.ItemsSource   = _incomingFiles;
        IncomingEmptyMsg.IsVisible = _incomingFiles.Count == 0;
        if (_incomingFiles.Count == 0)
            IncomingEmptyMsg.Text = "No files found in this folder.";

        ShowIncomingPreviewEmpty();
    }

    private void OnIncomingSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _incomingSelected   = IncomingList.SelectedItem as IncomingFileVm;
        ImportBtn.IsEnabled = _incomingSelected is not null && CurrentEntry is not null;
        UpdateIncomingPreview();
    }

    private void UpdateIncomingPreview()
    {
        _incomingPreviewBitmap?.Dispose();
        _incomingPreviewBitmap = null;
        IncomingPreview.IsVisible            = false;
        IncomingPreviewPlaceholder.IsVisible = true;

        if (_incomingSelected is null)
        {
            IncomingPreviewPlaceholder.Text = "Select a file to preview.";
            return;
        }

        var f = _incomingSelected;
        if (f.IsImage && File.Exists(f.FilePath))
        {
            try
            {
                _incomingPreviewBitmap               = new Bitmap(f.FilePath);
                IncomingPreview.Source               = _incomingPreviewBitmap;
                IncomingPreview.IsVisible            = true;
                IncomingPreviewPlaceholder.IsVisible = false;
                return;
            }
            catch { }
        }

        var ext = Path.GetExtension(f.FilePath).ToLowerInvariant();
        IncomingPreviewPlaceholder.Text = ext switch
        {
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".webm" => "▶  Video file",
            ".pdf"                                           => "📄  PDF document",
            _                                               => ext.TrimStart('.').ToUpperInvariant() + " file",
        };
    }

    private void ShowIncomingPreviewEmpty()
    {
        _incomingPreviewBitmap?.Dispose();
        _incomingPreviewBitmap               = null;
        IncomingPreview.IsVisible            = false;
        IncomingPreviewPlaceholder.IsVisible = true;
        IncomingPreviewPlaceholder.Text      = "Select a file to preview.";
        if (IncomingPreview.Source is not null)
            IncomingPreview.Source = null;
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Select Incoming Media Folder",
            AllowMultiple = false,
        });

        if (result.Count == 0) return;
        var picked = result[0].TryGetLocalPath();
        if (picked is null) return;

        _incomingBrowseDir = picked;
        RefreshIncoming();
    }

    private void OnOpenIncomingFolder(object? sender, RoutedEventArgs e)
    {
        var dir = Directory.Exists(_incomingBrowseDir) ? _incomingBrowseDir :
                  Directory.Exists(_incomingDir)       ? _incomingDir : null;
        if (dir is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        var entry = CurrentEntry;
        if (entry is null || _incomingSelected is null) return;

        var mediaType = ImportTypePicker.SelectedItem as string;
        if (mediaType is null) return;

        var deleteAfter = DeleteSourceCheck.IsChecked == true;

        try
        {
            var result = _service.ImportFromIncoming(
                entry.DbPath, entry.ReleaseId, entry.Name,
                entry.HardwareFamilyId, entry.DatLineId,
                _incomingSelected.FilePath, mediaType, deleteAfter);

            if (!result.Success)
            {
                ShowValidation($"Import failed: {result.ErrorMessage}");
                return;
            }

            ShowValidation($"Imported as {mediaType}.", isError: false);
            RefreshAssets();
            RefreshIncoming();
        }
        catch (Exception ex)
        {
            ShowValidation($"Import error: {ex.Message}");
        }
    }

    // ── Global ────────────────────────────────────────────────────────────────

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        RefreshAssets();
        RefreshIncoming();
    }

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

// ── Left-pane view model ──────────────────────────────────────────────────────

internal sealed class MediaAssetVm(ReleaseMediaAsset asset)
{
    public ReleaseMediaAsset Asset        => asset;
    public string            FileName     => asset.FileName;
    public string            MediaTypeLabel => asset.MediaType.ToUpperInvariant();
    public string            SizeDisplay  => asset.SizeDisplay;
    public string            StatusLabel  => asset.StatusLabel;

    public IBrush StatusBackground => asset.StatusLabel switch
    {
        "Preferred" => new SolidColorBrush(Color.Parse("#152415")),
        "Excluded"  => new SolidColorBrush(Color.Parse("#2A1215")),
        "Missing"   => new SolidColorBrush(Color.Parse("#1A1215")),
        _           => new SolidColorBrush(Color.Parse("#141430")),
    };

    public IBrush StatusForeground => asset.StatusLabel switch
    {
        "Preferred" => new SolidColorBrush(Color.Parse("#4CAF50")),
        "Excluded"  => new SolidColorBrush(Color.Parse("#EF5350")),
        "Missing"   => new SolidColorBrush(Color.Parse("#FF8A65")),
        _           => new SolidColorBrush(Color.Parse("#7070AA")),
    };
}

// ── Right-pane view model ─────────────────────────────────────────────────────

internal sealed class IncomingFileVm
{
    public string FilePath    { get; }
    public string FileName    { get; }
    public string Extension   { get; }
    public string SizeDisplay { get; }
    public bool   IsImage     { get; }

    public IncomingFileVm(string filePath)
    {
        FilePath    = filePath;
        FileName    = Path.GetFileName(filePath);
        var ext     = Path.GetExtension(filePath).ToLowerInvariant();
        Extension   = ext.TrimStart('.').ToUpperInvariant();
        IsImage     = MediaStore.ImageExtensions.Contains(ext);
        SizeDisplay = TryGetSize(filePath);
    }

    private static string TryGetSize(string path)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            return bytes switch
            {
                < 1024               => $"{bytes} B",
                < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
                _                    => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
            };
        }
        catch { return "?"; }
    }
}

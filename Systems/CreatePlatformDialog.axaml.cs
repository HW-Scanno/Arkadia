using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class CreatePlatformDialog : Window
{
    private readonly HashSet<string> _existingIds;
    private readonly bool            _isEditMode;
    private readonly string          _imageDir;
    private readonly CatalogService  _catalog;

    private string? _logoPath;      // null = no new file chosen (keep existing or none)
    private string? _detailsPath;

    // Pending delete flags — set when user confirms deletion, applied on save
    private bool _deleteLogoOnSave;
    private bool _deleteDetailsOnSave;

    public HardwareFamilyRecord? CreatedPlatform { get; private set; }
    public string? LogoImagePath           { get; private set; }
    public string? DetailsImagePath        { get; private set; }
    public bool DeleteLogoImage            { get; private set; }
    public bool DeleteDetailsImage         { get; private set; }

    // Parameterless ctor required by Avalonia XAML compiler
    public CreatePlatformDialog() : this([], null, string.Empty, [], null!) { }

    /// <param name="existingIds">IDs already in the catalog — used for uniqueness check.</param>
    /// <param name="prefill">Non-null → edit mode; pre-fills all fields, locks ID.</param>
    /// <param name="imageDir">Path to data/systemimages — used to check existing images.</param>
    /// <param name="hardwareTypes">Hardware type list loaded from the DB — drives the ComboBox.</param>
    /// <param name="catalog">Catalog service — used to refresh platform types after manager closes.</param>
    public CreatePlatformDialog(
        IEnumerable<string>                 existingIds,
        HardwareFamilyRecord?               prefill,
        string                              imageDir,
        IReadOnlyList<HardwareTypeRecord>   hardwareTypes,
        CatalogService                      catalog)
    {
        InitializeComponent();

        _existingIds = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        _imageDir    = imageDir;
        _isEditMode  = prefill is not null;
        _catalog     = catalog;

        // Build a display list with a blank leading entry
        var items = new List<HardwareTypeRecord>(hardwareTypes.Count + 1)
        {
            new() { Id = "", Name = "", SortOrder = 0 }
        };
        items.AddRange(hardwareTypes);
        HardwareTypeInput.ItemsSource   = items;
        HardwareTypeInput.SelectedItem  = items.Find(h => h.Id == (prefill?.HardwareTypeId ?? ""))
                                          ?? items[0];

        if (_isEditMode && prefill is not null)
        {
            // Lock the ID — read-only in edit mode
            IdInput.Text = prefill.Id;
            IdInput.Classes.Add("id-readonly");
            _existingIds.Remove(prefill.Id); // allow saving back same id without "already exists" error

            // Pre-fill all other fields
            ScrapeAsInput.Text = prefill.ScrapeSystemId;
            ScrapeAsInput.Classes.Add("id-readonly");

            NameInput.Text         = prefill.Name;
            ManufacturerInput.Text = prefill.Manufacturer;
            YearInput.Text         = prefill.YearOfRelease;
            MediaInput.Text        = prefill.Media;
            NotesInput.Text        = prefill.Notes;
            CpuInput.Text          = prefill.Cpu;
            MemoryInput.Text       = prefill.Memory;
            GraphicsInput.Text     = prefill.Graphics;
            SoundInput.Text        = prefill.Sound;
            ResolutionInput.Text   = prefill.DisplayResolution;
            AspectRatioInput.Text  = prefill.AspectRatio;

            CreateButton.Content = "Save Changes";

            // Load existing images into preview areas
            LoadExistingPreview(prefill.Id, isLogo: true);
            LoadExistingPreview(prefill.Id, isLogo: false);

            // Show delete buttons if images exist on disk
            RefreshDeleteButtons(prefill.Id);
        }

        // Click-to-browse
        LogoDropArea.PointerPressed    += async (_, _) => await BrowseImage(isLogo: true);
        DetailsDropArea.PointerPressed += async (_, _) => await BrowseImage(isLogo: false);

        ValidateForm();
    }

    private async void OnManagePlatformTypes(object? sender, RoutedEventArgs e)
    {
        var prevId = (HardwareTypeInput.SelectedItem as HardwareTypeRecord)?.Id;
        var dialog = new PlatformTypeManagerDialog(_catalog);
        await dialog.ShowDialog<bool>(this);

        var refreshed = _catalog.LoadHardwareTypes();
        var items = new List<HardwareTypeRecord>(refreshed.Count + 1)
        {
            new() { Id = "", Name = "", SortOrder = 0 }
        };
        items.AddRange(refreshed);
        HardwareTypeInput.ItemsSource  = items;
        HardwareTypeInput.SelectedItem = items.Find(h => h.Id == (prevId ?? "")) ?? items[0];
    }

    private void RefreshDeleteButtons(string platformId)
    {
        var logoPath    = Path.Combine(_imageDir, $"{platformId}-logo.png");
        var detailsPath = Path.Combine(_imageDir, $"{platformId}-details.png");

        // Show delete button only if the file currently exists AND not already queued for deletion
        LogoDeleteButton.IsVisible    = File.Exists(logoPath)    && !_deleteLogoOnSave;
        DetailsDeleteButton.IsVisible = File.Exists(detailsPath) && !_deleteDetailsOnSave;
    }

    // ── Image delete handlers ────────────────────────────────────────────────

    private async void OnDeleteLogoImage(object? sender, RoutedEventArgs e)
    {
        var id       = IdInput.Text?.Trim() ?? "";
        var fileName = $"{id}-logo.png";
        var dialog   = new DeleteImageConfirmDialog(fileName);
        var ok       = await dialog.ShowDialog<bool>(this);
        if (!ok) return;

        _deleteLogoOnSave          = true;
        LogoPreviewImage.Source    = null;
        LogoPreviewImage.IsVisible = false;
        LogoDropText.IsVisible     = true;
        LogoDeleteButton.IsVisible = false;

        // Clear any newly-chosen replacement too
        _logoPath = null;
    }

    private async void OnDeleteDetailsImage(object? sender, RoutedEventArgs e)
    {
        var id       = IdInput.Text?.Trim() ?? "";
        var fileName = $"{id}-details.png";
        var dialog   = new DeleteImageConfirmDialog(fileName);
        var ok       = await dialog.ShowDialog<bool>(this);
        if (!ok) return;

        _deleteDetailsOnSave          = true;
        DetailsPreviewImage.Source    = null;
        DetailsPreviewImage.IsVisible = false;
        DetailsDropText.IsVisible     = true;
        DetailsDeleteButton.IsVisible = false;

        _detailsPath = null;
    }

    private void LoadExistingPreview(string platformId, bool isLogo)
    {
        var suffix = isLogo ? "logo" : "details";
        var path   = Path.Combine(_imageDir, $"{platformId}-{suffix}.png");
        if (!File.Exists(path)) return;
        try
        {
            var bmp = new Bitmap(path);
            if (isLogo)
            {
                LogoPreviewImage.Source    = bmp;
                LogoPreviewImage.IsVisible = true;
                LogoDropText.IsVisible     = false;
            }
            else
            {
                DetailsPreviewImage.Source    = bmp;
                DetailsPreviewImage.IsVisible = true;
                DetailsDropText.IsVisible     = false;
            }
        }
        catch { }
    }

    // ── Browse ───────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task BrowseImage(bool isLogo)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = isLogo ? "Select Logo Image" : "Select Details Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PNG Image") { Patterns = ["*.png"] },
            ],
        });
        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is string path)
            SetImagePath(path, isLogo);
    }

    // ── Image preview ────────────────────────────────────────────────────────

    private void SetImagePath(string path, bool isLogo)
    {
        try
        {
            var bmp = new Bitmap(path);
            if (isLogo)
            {
                _logoPath                  = path;
                _deleteLogoOnSave          = false; // uploading cancels pending delete
                LogoPreviewImage.Source    = bmp;
                LogoPreviewImage.IsVisible = true;
                LogoDropText.IsVisible     = false;
                LogoDeleteButton.IsVisible = false; // new file replaces; no existing to delete
            }
            else
            {
                _detailsPath                  = path;
                _deleteDetailsOnSave          = false;
                DetailsPreviewImage.Source    = bmp;
                DetailsPreviewImage.IsVisible = true;
                DetailsDropText.IsVisible     = false;
                DetailsDeleteButton.IsVisible = false;
            }
        }
        catch { /* ignore unreadable files */ }
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private void OnIdChanged(object? sender, TextChangedEventArgs e)           => ValidateForm();

    private void OnScrapeAsChanged(object? sender, TextChangedEventArgs e)    { }

    private void OnScrapeAsGotFocus(object? sender, Avalonia.Input.GotFocusEventArgs e)
    {
        if (!_isEditMode && string.IsNullOrEmpty(ScrapeAsInput.Text))
            ScrapeAsInput.Text = IdInput.Text?.Trim() ?? "";
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)         => ValidateForm();
    private void OnManufacturerChanged(object? sender, TextChangedEventArgs e) => ValidateForm();

    private static readonly Regex SafeId =
        new(@"^[a-z0-9][a-z0-9\-]*$", RegexOptions.Compiled);

    private void ValidateForm()
    {
        var id   = IdInput.Text?.Trim()           ?? "";
        var name = NameInput.Text?.Trim()         ?? "";
        var mfr  = ManufacturerInput.Text?.Trim() ?? "";

        string? error = _isEditMode ? null  // ID is locked in edit mode — skip validation
            : id.Length == 0                ? null
            : !SafeId.IsMatch(id)           ? "ID must be lowercase alphanumeric with hyphens (e.g. nes, ps2)."
            : _existingIds.Contains(id)     ? "A platform with this ID already exists."
            : null;

        IdErrorText.Text      = error ?? "";
        IdErrorText.IsVisible = error is not null;

        CreateButton.IsEnabled =
            id.Length > 0 && error is null &&
            name.Length > 0 &&
            mfr.Length > 0;
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────────────

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var idVal    = IdInput.Text!.Trim();
        var scrapeAs = ScrapeAsInput.Text?.Trim() ?? "";
        CreatedPlatform = new HardwareFamilyRecord
        {
            Id                = idVal,
            Name              = NameInput.Text!.Trim(),
            Manufacturer      = ManufacturerInput.Text!.Trim(),
            HardwareTypeId    = (HardwareTypeInput.SelectedItem as HardwareTypeRecord)?.Id ?? "",
            YearOfRelease     = YearInput.Text?.Trim()       ?? "",
            Media             = MediaInput.Text?.Trim()       ?? "",
            Notes             = NotesInput.Text?.Trim()       ?? "",
            Cpu               = CpuInput.Text?.Trim()         ?? "",
            Memory            = MemoryInput.Text?.Trim()      ?? "",
            Graphics          = GraphicsInput.Text?.Trim()    ?? "",
            Sound             = SoundInput.Text?.Trim()       ?? "",
            DisplayResolution = ResolutionInput.Text?.Trim()  ?? "",
            AspectRatio       = AspectRatioInput.Text?.Trim() ?? "",
            ScrapeSystemId    = scrapeAs == idVal ? "" : scrapeAs,
        };
        LogoImagePath      = _logoPath;
        DetailsImagePath   = _detailsPath;
        DeleteLogoImage    = _deleteLogoOnSave;
        DeleteDetailsImage = _deleteDetailsOnSave;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

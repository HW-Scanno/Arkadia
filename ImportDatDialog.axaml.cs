using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class ImportDatDialog : Window
{
    private string?              _filePath;
    private DatParser.Result?    _parseResult;
    private readonly HashSet<string> _existingDatLineIds;
    private readonly CatalogService  _catalog;
    private readonly string          _dataDir;

    public string? SelectedFilePath   { get; private set; }
    public string? PlatformId         { get; private set; }
    public string? Authority          { get; private set; }
    public AuthorityRecord? SelectedAuthority { get; private set; }
    public string? DatCategory        { get; private set; }
    public string? ParsedDate         { get; private set; }
    public string? Version            { get; private set; }
    public string? DatLineId          { get; private set; }
    public int     ReleaseCount       { get; private set; }

    // Parsed games, passed back to MainWindow for persistence.
    public IReadOnlyList<DatParser.ParsedGame> ParsedGames =>
        _parseResult?.Games ?? [];

    // Parameterless ctor required by Avalonia XAML compiler
    public ImportDatDialog() : this([], [], null!, string.Empty) { }

    public ImportDatDialog(
        IReadOnlyList<PlatformRecord>  platforms,
        IReadOnlyList<DatLineRecord>   existingDatLines,
        CatalogService                 catalog,
        string                         dataDir)
    {
        InitializeComponent();

        _existingDatLineIds = existingDatLines.Select(d => d.Id).ToHashSet();
        _catalog = catalog;
        _dataDir = dataDir;

        RefreshAuthorityList(null);

        var platformItems = new List<PlatformRecord>(platforms.Count + 1)
        {
            new() { Id = "", Name = "" }
        };
        platformItems.AddRange(platforms);
        PlatformInput.ItemsSource   = platformItems;
        PlatformInput.SelectedIndex = 0;

        CategoryInput.ItemsSource = new[] { "Media", "Firmware", "BIOS", "eShop", "Other" };

        ValidateForm();
    }

    // ── Browse ───────────────────────────────────────────────────────────────

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var startLocation = await TryGetIncomingDatsFolder();

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title                  = "Select DAT File",
            AllowMultiple          = false,
            SuggestedStartLocation = startLocation,
            FileTypeFilter         =
            [
                new FilePickerFileType("DAT File") { Patterns = ["*.dat"] },
            ],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not string path) return;

        _filePath          = path;
        FilePathInput.Text = path;

        ApplyParseResult(DatParser.Parse(path));
        UpdateDatLineId();
        ValidateForm();
    }

    private async System.Threading.Tasks.Task<IStorageFolder?> TryGetIncomingDatsFolder()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "incoming-dats");
            return await StorageProvider.TryGetFolderFromPathAsync(dir);
        }
        catch { return null; }
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    private void ApplyParseResult(DatParser.Result result)
    {
        _parseResult = result;

        if (result.Success)
        {
            ParsedDateInput.Text   = result.Date;
            VersionInput.Text      = result.Version;
            ReleaseCountText.Text  = result.Games.Count.ToString();
            ParseStatusText.Text   = "Parsed ✓";
            ParseStatusText.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
        }
        else
        {
            ParsedDateInput.Text   = "";
            VersionInput.Text      = "";
            ReleaseCountText.Text  = "0";
            ParseStatusText.Text   = $"Parsing failed — {result.ErrorMessage}";
            ParseStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(0xEF, 0x53, 0x50));
        }
    }

    // ── ID generation ────────────────────────────────────────────────────────

    private void UpdateDatLineId()
    {
        var platformId = (PlatformInput.SelectedItem as PlatformRecord)?.Id ?? "";
        var authority  = (AuthorityInput.SelectedItem as AuthorityRecord)?.Id ?? "";
        var category   = CategoryInput.Text?.Trim() ?? "";
        var slug       = CategorySlug(category);

        DatLineIdInput.Text = (platformId.Length > 0 && authority.Length > 0 && slug.Length > 0)
            ? $"{platformId}-{authority}-{slug}"
            : "";
    }

    /// <summary>Strips non-alphanumeric chars and lowercases to produce a stable slug.</summary>
    private static string CategorySlug(string category)
        => new string(category.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnConfigChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateDatLineId();
        ValidateForm();
    }

    private void OnParsedDateChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateDatLineId();
        ValidateForm();
    }

    private void OnCategoryChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateDatLineId();
        ValidateForm();
    }

    // ── Authority list refresh ────────────────────────────────────────────────

    private void RefreshAuthorityList(string? preserveId)
    {
        var authorities = _catalog?.LoadAuthorities() ?? [];
        AuthorityInput.ItemsSource = authorities;
        var sel = preserveId is not null
            ? authorities.FirstOrDefault(a => a.Id == preserveId)
            : authorities.FirstOrDefault();
        AuthorityInput.SelectedItem = sel;
    }

    private async void OnManageAuthorities(object? sender, RoutedEventArgs e)
    {
        var prevId = (AuthorityInput.SelectedItem as AuthorityRecord)?.Id;
        var dialog = new AuthorityManagerDialog(_catalog, _dataDir);
        await dialog.ShowDialog<bool>(this);
        RefreshAuthorityList(prevId);
        UpdateDatLineId();
        ValidateForm();
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private void ValidateForm()
    {
        var hasFile     = _filePath is not null;
        var hasPlatform = (PlatformInput.SelectedItem as PlatformRecord)?.Id.Length > 0;
        var hasAuth     = (AuthorityInput.SelectedItem as AuthorityRecord)?.Id.Length > 0;
        var hasCategory = CategorySlug(CategoryInput.Text?.Trim() ?? "").Length > 0;
        var hasDate     = ParsedDateInput.Text?.Trim().Length > 0;
        var parseOk     = _parseResult?.Success == true;

        var datLineId   = DatLineIdInput.Text?.Trim() ?? "";
        var isDuplicate = datLineId.Length > 0 && _existingDatLineIds.Contains(datLineId);

        DatLineConflictText.IsVisible = isDuplicate;
        ImportButton.IsEnabled = hasFile && hasPlatform && hasAuth && hasCategory
                              && hasDate && parseOk && !isDuplicate;
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────────────

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        SelectedFilePath  = _filePath;
        SelectedAuthority = AuthorityInput.SelectedItem as AuthorityRecord;
        PlatformId        = (PlatformInput.SelectedItem as PlatformRecord)?.Id ?? "";
        Authority         = SelectedAuthority?.Id ?? "";
        DatCategory       = CategoryInput.Text?.Trim() ?? "";
        ParsedDate        = ParsedDateInput.Text?.Trim() ?? "";
        Version           = VersionInput.Text?.Trim() ?? "";
        DatLineId    = DatLineIdInput.Text?.Trim() ?? "";
        ReleaseCount = _parseResult?.Games.Count ?? 0;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

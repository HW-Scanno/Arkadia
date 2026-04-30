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
    private readonly CatalogService? _catalog;
    private readonly string          _dataDir;
    private List<MediaTypeRecord>    _mediaTypes = [];

    public string? SelectedFilePath   { get; private set; }
    public string? HardwareFamilyId   { get; private set; }
    public string? Authority          { get; private set; }
    public AuthorityRecord? SelectedAuthority { get; private set; }
    public string? MediaTypeId        { get; private set; }
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
        IReadOnlyList<HardwareFamilyRecord> platforms,
        IReadOnlyList<DatLineRecord>        existingDatLines,
        CatalogService                      catalog,
        string                              dataDir,
        string?                             preselectedHardwareFamilyId = null)
    {
        InitializeComponent();

        _existingDatLineIds = existingDatLines.Select(d => d.Id).ToHashSet();
        _catalog = catalog;
        _dataDir = dataDir;

        RefreshAuthorityList(null);

        var platformItems = new List<HardwareFamilyRecord>(platforms.Count + 1)
        {
            new() { Id = "", Name = "" }
        };
        platformItems.AddRange(platforms);
        HardwareFamilyInput.ItemsSource   = platformItems;
        HardwareFamilyInput.SelectedIndex = 0;

        if (preselectedHardwareFamilyId is { Length: > 0 })
        {
            var preselected = platformItems.FirstOrDefault(p => p.Id == preselectedHardwareFamilyId);
            if (preselected is not null)
            {
                HardwareFamilyInput.SelectedItem = preselected;
                HardwareFamilyInput.IsEnabled    = false;
            }
        }

        _mediaTypes = _catalog?.GetMediaTypes() ?? [];
        MediaTypeInput.ItemsSource = _mediaTypes;
        if (_mediaTypes.Count > 0)
            MediaTypeInput.SelectedIndex = 0;

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
        var platformId  = (HardwareFamilyInput.SelectedItem as HardwareFamilyRecord)?.Id ?? "";
        var authority   = (AuthorityInput.SelectedItem as AuthorityRecord)?.Id ?? "";
        var mediaTypeId = (MediaTypeInput.SelectedItem as MediaTypeRecord)?.Id ?? "";

        DatLineIdInput.Text = (platformId.Length > 0 && authority.Length > 0 && mediaTypeId.Length > 0)
            ? $"{platformId}-{authority}-{mediaTypeId}"
            : "";
    }

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

    private void OnMediaTypeChanged(object? sender, SelectionChangedEventArgs e)
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
        if (_catalog is null) return;
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
        var hasFile             = _filePath is not null;
        var hasHardwareFamily   = (HardwareFamilyInput.SelectedItem as HardwareFamilyRecord)?.Id.Length > 0;
        var hasAuth      = (AuthorityInput.SelectedItem as AuthorityRecord)?.Id.Length > 0;
        var hasMediaType = (MediaTypeInput.SelectedItem as MediaTypeRecord)?.Id.Length > 0;
        var hasDate      = ParsedDateInput.Text?.Trim().Length > 0;
        var parseOk      = _parseResult?.Success == true;

        var datLineId   = DatLineIdInput.Text?.Trim() ?? "";
        var isDuplicate = datLineId.Length > 0 && _existingDatLineIds.Contains(datLineId);

        DatLineConflictText.IsVisible = isDuplicate;
        ImportButton.IsEnabled = hasFile && hasHardwareFamily && hasAuth && hasMediaType
                              && hasDate && parseOk && !isDuplicate;
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────────────

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        SelectedFilePath  = _filePath;
        SelectedAuthority = AuthorityInput.SelectedItem as AuthorityRecord;
        HardwareFamilyId  = (HardwareFamilyInput.SelectedItem as HardwareFamilyRecord)?.Id ?? "";
        Authority         = SelectedAuthority?.Id ?? "";
        MediaTypeId       = (MediaTypeInput.SelectedItem as MediaTypeRecord)?.Id ?? "";
        ParsedDate        = ParsedDateInput.Text?.Trim() ?? "";
        Version           = VersionInput.Text?.Trim() ?? "";
        DatLineId    = DatLineIdInput.Text?.Trim() ?? "";
        ReleaseCount = _parseResult?.Games.Count ?? 0;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

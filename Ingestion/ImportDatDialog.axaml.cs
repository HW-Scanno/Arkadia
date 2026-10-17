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
    private readonly Ingestion.SingleDatImportForm _form;
    private readonly Ingestion.SingleDatImportIdentityBinding _identity;
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
    public string? Variant            { get; private set; }
    public string? DisplayName        { get; private set; }
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

        // Every existing DAT id (Single DATs and Group leaves): the shared builder compares case-insensitively.
        _form = new Ingestion.SingleDatImportForm(existingDatLines.Select(d => d.Id));
        _identity = new Ingestion.SingleDatImportIdentityBinding(_form, VariantInput, DatLineIdInput, DisplayNameInput);
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

        UpdateDatLineId();   // initial id preview + auto-suggested Display Name from the default selections
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

    // ── Identity (all rules live in SingleDatImportForm / SingleDatIdBuilder) ──

    /// <summary>Pushes the CURRENT combo selections into the form, then renders its outputs.</summary>
    private void UpdateDatLineId()
    {
        var family    = HardwareFamilyInput.SelectedItem as HardwareFamilyRecord;
        var authority = AuthorityInput.SelectedItem as AuthorityRecord;
        var media     = MediaTypeInput.SelectedItem as MediaTypeRecord;

        _identity.OnSelectionChanged(family?.Id, authority?.Id, authority?.Name, media?.Id);
    }

    // TextChanged is delivered DEFERRED by Avalonia (also for our own programmatic writes), so these handlers
    // must not rely on a "currently syncing" flag; SingleDatImportIdentityBinding decides user-edit vs echo.
    private void OnVariantChanged(object? sender, TextChangedEventArgs e)
    {
        if (_identity is null) return;   // XAML-initial notification can arrive before construction finishes
        _identity.OnVariantTextChanged();   // verbatim: never trimmed or lowercased
        ValidateForm();
    }

    private void OnDisplayNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (_identity is null) return;
        _identity.OnDisplayNameTextChanged();
        ValidateForm();
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
        if (_form is null) return;

        var hasFile = _filePath is not null;
        var hasDate = ParsedDateInput.Text?.Trim().Length > 0;
        var parseOk = _parseResult?.Success == true;

        // Show identity problems only once there is something to report; the button stays disabled either way.
        var error = _form.FirstError;
        DatLineConflictText.Text      = error ?? "";
        DatLineConflictText.IsVisible = error is not null && _form.SelectionComplete;

        ImportButton.IsEnabled = hasFile && hasDate && parseOk && _form.IsValid;
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────────────

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        if (!_form.IsValid) return;   // defensive: the button is already disabled
        SelectedFilePath  = _filePath;
        SelectedAuthority = AuthorityInput.SelectedItem as AuthorityRecord;
        HardwareFamilyId  = (HardwareFamilyInput.SelectedItem as HardwareFamilyRecord)?.Id ?? "";
        Authority         = SelectedAuthority?.Id ?? "";
        MediaTypeId       = (MediaTypeInput.SelectedItem as MediaTypeRecord)?.Id ?? "";
        ParsedDate        = ParsedDateInput.Text?.Trim() ?? "";
        Version           = VersionInput.Text?.Trim() ?? "";
        // The id is the validated one from the shared builder (never re-read from the preview text box).
        DatLineId    = _form.FinalId ?? "";
        Variant      = _form.Variant;
        DisplayName  = _form.DisplayName.Trim();
        ReleaseCount = _parseResult?.Games.Count ?? 0;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

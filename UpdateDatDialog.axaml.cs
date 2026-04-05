using System;
using System.IO;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Arkadia;

public partial class UpdateDatDialog : Window
{
    private string?           _filePath;
    private DatParser.Result? _parseResult;

    public DatParser.Result? ParseResult => _parseResult;
    public string?           Version     => VersionInput.Text?.Trim();

    // Parameterless ctor required by Avalonia XAML compiler
    public UpdateDatDialog() : this(new DatLineRecord(), "", "") { }

    public UpdateDatDialog(
        DatLineRecord datLine,
        string        platformName,
        string        storageStrategyName)
    {
        InitializeComponent();

        InfoId.Text       = datLine.Id;
        InfoPlatform.Text = platformName;
        InfoAuthority.Text = AuthorityLabel(datLine.Authority);
        InfoCategory.Text  = datLine.DatCategory;
        InfoStorage.Text   = storageStrategyName.Length > 0 ? storageStrategyName : "—";
    }

    private static string AuthorityLabel(string authority) => authority switch
    {
        "redump"   => "Redump",
        "no-intro" => "No-Intro",
        "tosec"    => "TOSEC",
        "custom"   => "Custom",
        _ => authority.Length > 0
                 ? char.ToUpperInvariant(authority[0]) + authority[1..]
                 : authority,
    };

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
            ParsedDateInput.Text  = result.Date;
            VersionInput.Text     = result.Version;
            ReleaseCountText.Text = result.Games.Count.ToString();
            ParseStatusText.Text  = "Parsed ✓";
            ParseStatusText.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
        }
        else
        {
            ParsedDateInput.Text  = "";
            VersionInput.Text     = "";
            ReleaseCountText.Text = "0";
            ParseStatusText.Text  = $"Parsing failed — {result.ErrorMessage}";
            ParseStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(0xEF, 0x53, 0x50));
        }
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private void OnParsedDateChanged(object? sender, TextChangedEventArgs e) => ValidateForm();

    private void ValidateForm()
    {
        UpdateButton.IsEnabled =
            _filePath is not null &&
            _parseResult?.Success == true &&
            ParsedDateInput.Text?.Trim().Length > 0;
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────────────

    private void OnUpdate(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

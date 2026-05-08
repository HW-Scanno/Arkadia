using System.IO;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class ResizeVolumeDialog : Window
{
    private readonly long           _actualSizeBytes;
    private readonly string         _datLineId;
    private readonly string         _dbPath;
    private readonly CatalogService _catalog;

    public long ResultBytes { get; private set; }

    public ResizeVolumeDialog() : this("", 0, 0, "", "", null!) { }

    public ResizeVolumeDialog(
        string         volumeLabel,
        long           currentPlannedBytes,
        long           actualSizeBytes,
        string         datLineId,
        string         dbPath,
        CatalogService catalog)
    {
        InitializeComponent();

        _actualSizeBytes = actualSizeBytes;
        _datLineId       = datLineId;
        _dbPath          = dbPath;
        _catalog         = catalog;

        Title           = "Resize Volume";
        TitleLabel.Text = $"Resize Volume \u2014 {volumeLabel}";
        var currentGb   = currentPlannedBytes / (1024.0 * 1024 * 1024);
        SizeInput.Text  = currentGb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Size suggestion ───────────────────────────────────────────────────────

    private void OnSuggestSize(object? sender, RoutedEventArgs e)
    {
        if (_dbPath.Length == 0 || !File.Exists(_dbPath)) return;

        var assignedIds     = _catalog.GetAssignedDerivedIdsByDatLine(_datLineId);
        var (_, unassigned) = new DatLineStore(_dbPath).GetUnassignedPresentStats(assignedIds);

        var suggestedBytes = (long)((_actualSizeBytes + unassigned) * 1.10);
        var suggestedGb    = suggestedBytes / (1024.0 * 1024 * 1024);
        SizeInput.Text = suggestedGb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private void OnSizeChanged(object? sender, TextChangedEventArgs e)
    {
        var text = SizeInput.Text?.Trim() ?? "";
        bool ok  = double.TryParse(text,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var gb) && gb > 0;

        ErrorText.Text      = text.Length > 0 && !ok ? "Must be a positive number (e.g. 4.7)." : "";
        ErrorText.IsVisible = text.Length > 0 && !ok;
        ConfirmBtn.IsEnabled = ok;
    }

    // ── Confirm / Cancel ──────────────────────────────────────────────────────

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (double.TryParse(SizeInput.Text?.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var gb) && gb > 0)
        {
            ResultBytes = (long)(gb * 1024L * 1024 * 1024);
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

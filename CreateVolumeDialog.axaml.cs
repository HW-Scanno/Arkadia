using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class CreateVolumeDialog : Window
{
    private readonly List<DatLineRecord> _datLines;

    public VolumeRecord? Result { get; private set; }

    // Parameterless ctor required by Avalonia XAML compiler
    public CreateVolumeDialog() : this([], []) { }

    public CreateVolumeDialog(List<PlatformRecord> platforms, List<DatLineRecord> datLines)
    {
        InitializeComponent();

        _datLines = datLines;

        var platformItems = platforms.Select(p => p.Name).ToList();
        PlatformCombo.ItemsSource  = platformItems;
        PlatformCombo.SelectedIndex = platformItems.Count > 0 ? 0 : -1;

        PlatformCombo.SelectionChanged += OnPlatformChanged;
        RefreshDatLines();
    }

    private void OnPlatformChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshDatLines();
        Validate();
    }

    private void RefreshDatLines()
    {
        // Filter dat lines by selected platform name
        var platforms = PlatformCombo.ItemsSource as List<string> ?? [];
        var idx       = PlatformCombo.SelectedIndex;
        if (idx < 0 || idx >= platforms.Count)
        {
            DatLineCombo.ItemsSource   = new List<string>();
            DatLineCombo.SelectedIndex = -1;
            return;
        }

        // We stored platform names; map back to IDs via the dat lines list
        // Build a reverse: platform name → dat lines (we need platform records for this)
        // Since we only have names here, filter by matching dat_line names that match the platform
        // Actually, we need platforms list with IDs — rethink: store PlatformRecord directly
        // The combo shows names but we need to match via PlatformRecord.
        // We'll re-expose the platform list through a field.
        var filtered = _datLinesByPlatformIdx.TryGetValue(idx, out var dl) ? dl : [];
        DatLineCombo.ItemsSource   = filtered.Select(d => d.Name).ToList();
        DatLineCombo.SelectedIndex = filtered.Count > 0 ? 0 : -1;
        Validate();
    }

    // Indexed by platform combo index → dat lines for that platform
    private Dictionary<int, List<DatLineRecord>> _datLinesByPlatformIdx = [];

    // Called after construction to finish wiring (avoids chicken-and-egg in ctor)
    public void FinishInit(List<PlatformRecord> platforms)
    {
        _datLinesByPlatformIdx = [];
        for (int i = 0; i < platforms.Count; i++)
        {
            var pid  = platforms[i].Id;
            var list = _datLines.Where(d => d.PlatformId == pid).ToList();
            _datLinesByPlatformIdx[i] = list;
        }
        RefreshDatLines();
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e)   => Validate();
    private void OnFieldChanged(object? sender, SelectionChangedEventArgs e) => Validate();

    private void Validate()
    {
        var label    = LabelInput.Text?.Trim()        ?? "";
        var sizeText = PlannedSizeInput.Text?.Trim()  ?? "";
        bool sizeOk  = double.TryParse(sizeText, System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out var size) && size > 0;
        bool datOk   = DatLineCombo.SelectedIndex >= 0;

        string? err = sizeText.Length > 0 && !sizeOk ? "Planned size must be a positive number." : null;

        ErrorText.Text      = err ?? "";
        ErrorText.IsVisible = err is not null;
        ConfirmButton.IsEnabled = label.Length > 0 && sizeOk && datOk && err is null;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var sizeGb  = double.Parse(PlannedSizeInput.Text!.Trim(),
                          System.Globalization.CultureInfo.InvariantCulture);
        var platIdx = PlatformCombo.SelectedIndex;
        var dlIdx   = DatLineCombo.SelectedIndex;

        if (!_datLinesByPlatformIdx.TryGetValue(platIdx, out var dls) || dlIdx < 0 || dlIdx >= dls.Count)
            return;

        var datLine = dls[dlIdx];

        Result = new VolumeRecord
        {
            Id               = Guid.NewGuid().ToString("N"),
            Label            = LabelInput.Text!.Trim(),
            PlatformId       = datLine.PlatformId,
            DatLineId        = datLine.Id,
            Status           = "init",
            PlannedSizeBytes = (long)(sizeGb * 1024 * 1024 * 1024),
            ActualSizeBytes  = 0,
            CreatedAt        = DateTime.UtcNow,
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

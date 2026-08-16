using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Volumes;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Unified Create Volume dialog. One action, one dialog — the user picks a top-level COLLECTION owner
/// (a standalone Single DAT or a Group) for the selected platform; Group leaf DATs are never listed. Owner
/// identity is the typed <see cref="VolumeOwnerSelection"/> ((Kind, OwnerId)), never the display text. Label
/// numbering, Suggest Size, and SaveVolume dispatch on the owner kind. Header-only for both (no filesystem).
/// </summary>
public partial class CreateVolumeDialog : Window
{
    private readonly List<HardwareFamilyRecord>        _platforms;
    private readonly List<DatLineRecord>               _datLines;
    private readonly List<DatGroupRecord>              _groups;
    private readonly HashSet<string>                   _groupLeafIds;
    private readonly IReadOnlyDictionary<string,string> _authorityNames;
    private readonly IReadOnlyList<string>             _existingLabels;
    private readonly CatalogService                    _catalog;
    private readonly string                            _dataDir;

    private Dictionary<int, List<VolumeOwnerSelection>> _ownersByPlatformIdx = [];

    public VolumeRecord? Result { get; private set; }

    // Parameterless ctor required by the Avalonia XAML compiler.
    public CreateVolumeDialog() : this([], [], [], new HashSet<string>(), new Dictionary<string,string>(), [], null!, string.Empty) { }

    public CreateVolumeDialog(
        List<HardwareFamilyRecord>        platforms,
        List<DatLineRecord>               datLines,
        List<DatGroupRecord>              groups,
        ISet<string>                      groupLeafIds,
        IReadOnlyDictionary<string,string> authorityNames,
        IReadOnlyList<string>             existingLabels,
        CatalogService                    catalog,
        string                            dataDir)
    {
        InitializeComponent();

        _platforms      = [.. platforms.OrderBy(p => p.Manufacturer, StringComparer.OrdinalIgnoreCase)
                                       .ThenBy(p => p.Name,         StringComparer.OrdinalIgnoreCase)];
        _datLines       = datLines;
        _groups         = groups;
        _groupLeafIds   = new HashSet<string>(groupLeafIds, StringComparer.Ordinal);
        _authorityNames = authorityNames;
        _existingLabels = existingLabels;
        _catalog        = catalog;
        _dataDir        = dataDir;

        PlatformCombo.ItemsSource   = _platforms.Select(PlatformDisplay).ToList();
        PlatformCombo.SelectedIndex = _platforms.Count > 0 ? 0 : -1;

        PlatformCombo.SelectionChanged += OnPlatformChanged;
        RefreshCollections();
    }

    private static string PlatformDisplay(HardwareFamilyRecord p) =>
        p.Manufacturer.Length > 0 ? $"{p.Manufacturer} — {p.Name}" : p.Name;

    // Called after construction to finish wiring (avoids chicken-and-egg in ctor).
    public void FinishInit()
    {
        _ownersByPlatformIdx = [];
        for (int i = 0; i < _platforms.Count; i++)
            _ownersByPlatformIdx[i] = VolumeOwnerSelectionBuilder.Build(
                _platforms[i].Id, _datLines, _groups, _groupLeafIds, _authorityNames);
        RefreshCollections();
        RegenerateLabel();   // initial best-effort proposal for the default platform+collection
    }

    // ── Platform / collection selection ───────────────────────────────────────

    // Platform change = new owner context → rebuild collections and always propose a fresh label.
    private void OnPlatformChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshCollections();
        RegenerateLabel();
        Validate();
    }

    private void RefreshCollections()
    {
        // Detach so the programmatic ItemsSource/SelectedIndex swap doesn't fire a stale OnCollectionChanged.
        CollectionCombo.SelectionChanged -= OnCollectionChanged;
        var owners = CurrentOwners();
        CollectionCombo.ItemsSource   = owners;
        CollectionCombo.SelectedIndex = owners.Count > 0 ? 0 : -1;
        CollectionCombo.SelectionChanged += OnCollectionChanged;
        Validate();
    }

    private List<VolumeOwnerSelection> CurrentOwners()
        => _ownersByPlatformIdx.TryGetValue(PlatformCombo.SelectedIndex, out var o) ? o : [];

    private VolumeOwnerSelection? CurrentOwner() => CollectionCombo.SelectedItem as VolumeOwnerSelection;

    private string CurrentPlatformId()
    {
        var i = PlatformCombo.SelectedIndex;
        return i >= 0 && i < _platforms.Count ? _platforms[i].Id : "";
    }

    // Collection change = the volume OWNER changed → always propose a fresh label (replaces a manual one too).
    private void OnCollectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RegenerateLabel();
        Validate();
    }

    // ── Best-effort label proposal ────────────────────────────────────────────

    // A manual edit is preserved across incidental changes (size, Suggest Size) because only Platform/Collection
    // changes call RegenerateLabel. A user edit is only overwritten when the owner context itself changes.
    private void OnLabelChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void RegenerateLabel()
        => LabelInput.Text = VolumeLabelSuggestion.Suggest(_existingLabels, CurrentOwner(), CurrentPlatformId());

    // ── Size suggestion (Single: one leaf; Group: sum of leaf stats) — read-only, 1.10 rule ──

    private void OnSuggestSize(object? sender, RoutedEventArgs e)
    {
        if (CurrentOwner() is not { } owner) return;

        long unassigned = owner.Kind switch
        {
            VolumeOwnerKind.SingleDat => _datLines.FirstOrDefault(d => d.Id == owner.OwnerId) is { } dl
                                             ? LeafUnassignedBytes(dl.Id, dl.DataStorePath) : 0,
            VolumeOwnerKind.Group     => _catalog.GetLeavesForGroup(owner.OwnerId)
                                             .Sum(l => LeafUnassignedBytes(l.DatLine.Id, l.DatLine.DataStorePath)),
            _                         => 0,
        };

        var suggestedGb = (long)(unassigned * 1.10) / (1024.0 * 1024 * 1024);
        PlannedSizeInput.Text = suggestedGb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Unassigned-present bytes for one leaf/dat line (existing read-only stats path). 0 when its DB is absent.
    private long LeafUnassignedBytes(string datLineId, string dataStorePath)
    {
        if (string.IsNullOrEmpty(dataStorePath)) return 0;
        var dbPath = Path.Combine(_dataDir, dataStorePath);
        if (!File.Exists(dbPath)) return 0;

        var assigned        = _catalog.GetAssignedDerivedIdsByDatLine(datLineId);
        var (_, unassigned) = new DatLineStore(dbPath).GetUnassignedPresentStats(assigned);
        return unassigned;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private void Validate()
    {
        var label    = LabelInput.Text?.Trim()       ?? "";
        var sizeText = PlannedSizeInput.Text?.Trim() ?? "";
        bool sizeOk  = double.TryParse(sizeText, System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out var size) && size > 0;
        bool ownerOk = CurrentOwner() is not null;

        string? err = sizeText.Length > 0 && !sizeOk ? "Planned size must be a positive number." : null;

        ErrorText.Text          = err ?? "";
        ErrorText.IsVisible     = err is not null;
        ConfirmButton.IsEnabled = label.Length > 0 && sizeOk && ownerOk && err is null;
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e)      => Validate();
    private void OnFieldChanged(object? sender, SelectionChangedEventArgs e) => Validate();

    // ── Confirm / Cancel ──────────────────────────────────────────────────────

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (CurrentOwner() is not { } owner) return;
        var sizeGb = double.Parse(PlannedSizeInput.Text!.Trim(),
                         System.Globalization.CultureInfo.InvariantCulture);

        // Ownership dispatch (pure) — SaveVolume enforces the XOR/FK invariant.
        Result = owner.ToVolumeRecord(LabelInput.Text!.Trim(), (long)(sizeGb * 1024 * 1024 * 1024));
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

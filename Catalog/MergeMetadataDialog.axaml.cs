using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Arkadia.Data;
using Arkadia.Library;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

/// <summary>Result returned by MergeMetadataDialog when the user applies proposals.</summary>
public sealed class MergeMetadataResult
{
    public required ReleaseMetadataRecord Metadata      { get; init; }
    public required HashSet<string>       AppliedFields { get; init; }
}

/// <summary>View model for a single proposal comparison row in MergeMetadataDialog.</summary>
public sealed class ProposalRowVm : INotifyPropertyChanged
{
    public string  FieldKey      { get; init; } = "";
    public string  DisplayName   { get; init; } = "";
    public string  CurrentValue  { get; init; } = "";
    public string  ProviderValue { get; init; } = "";
    public string  Provider      { get; init; } = "";
    public bool    CanSelect     { get; init; }
    public bool    CanOverride   { get; init; }
    public string  StatusLabel   { get; init; } = "";
    public IBrush  StatusBrush   { get; init; } = Brushes.Gray;
    public string  CurrentDisplay => CurrentValue.Length > 0 ? CurrentValue : "—";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private bool _isOverridden;
    public bool IsOverridden
    {
        get => _isOverridden;
        set
        {
            if (_isOverridden == value) return;
            _isOverridden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOverridden)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelectEffective)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveStatusLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveStatusBrush)));
        }
    }

    public bool   CanSelectEffective   => CanSelect || IsOverridden;
    public string EffectiveStatusLabel => IsOverridden ? "OVERRIDE" : StatusLabel;
    public IBrush EffectiveStatusBrush => IsOverridden
        ? new SolidColorBrush(Color.Parse("#FF7043"))
        : StatusBrush;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MergeMetadataDialog : Window
{
    private readonly LibraryEntry        _entry;
    private readonly List<ProposalRowVm> _rows;
    private readonly string              _provider;

    public MergeMetadataDialog()
    {
        InitializeComponent();
        _entry    = null!;
        _rows     = [];
        _provider = "screenscraper";
    }

    public MergeMetadataDialog(LibraryEntry entry, string provider = "screenscraper")
    {
        InitializeComponent();
        _entry    = entry;
        _provider = provider;
        SubtitleLabel.Text = entry.Name;

        var store    = new DatLineStore(entry.DbPath);
        var current  = entry.Metadata ?? new ReleaseMetadataRecord { ReleaseId = entry.ReleaseId };
        var states   = store.LoadMetadataFieldStates(entry.ReleaseId)
                            .ToDictionary(s => s.Field, StringComparer.Ordinal);
        var proposals = store.LoadMetadataProposals(entry.ReleaseId, _provider);

        _rows = BuildRows(current, states, proposals);

        EmptyLabel.IsVisible    = _rows.Count == 0;
        ProposalRows.IsVisible  = _rows.Count > 0;
        ProposalRows.ItemsSource = _rows;
    }

    internal static List<ProposalRowVm> BuildRows(
        ReleaseMetadataRecord current,
        Dictionary<string, MetadataFieldStateRecord> states,
        List<MetadataProposalRecord> proposals)
    {
        var rows = new List<ProposalRowVm>();

        foreach (var proposal in proposals.Where(p => p.Value.Length > 0))
        {
            var canonical = GetCanonicalValue(current, proposal.Field);
            states.TryGetValue(proposal.Field, out var state);
            var isLocked = state?.Locked ?? false;
            var isManual = string.Equals(state?.Source, "manual", StringComparison.Ordinal);
            var isSame   = string.Equals(canonical, proposal.Value, StringComparison.Ordinal);

            string statusLabel;
            IBrush statusBrush;
            bool   canSelect;
            bool   canOverride;

            if (isSame)
            {
                statusLabel = "SAME";    statusBrush = new SolidColorBrush(Color.Parse("#4CAF50")); canSelect = false; canOverride = false;
            }
            else if (isManual)
            {
                statusLabel = "MANUAL";  statusBrush = new SolidColorBrush(Color.Parse("#FFD54F")); canSelect = false; canOverride = true;
            }
            else if (isLocked)
            {
                statusLabel = "LOCKED";  statusBrush = new SolidColorBrush(Color.Parse("#FFA726")); canSelect = false; canOverride = true;
            }
            else if (canonical.Length == 0)
            {
                statusLabel = "NEW";     statusBrush = new SolidColorBrush(Color.Parse("#9FA4FF")); canSelect = true;  canOverride = false;
            }
            else
            {
                statusLabel = "DIFFERS"; statusBrush = new SolidColorBrush(Color.Parse("#88AACC")); canSelect = true;  canOverride = false;
            }

            rows.Add(new ProposalRowVm
            {
                FieldKey      = proposal.Field,
                DisplayName   = FieldDisplayName(proposal.Field),
                CurrentValue  = canonical,
                ProviderValue = proposal.Value,
                Provider      = proposal.Provider,
                CanSelect     = canSelect,
                CanOverride   = canOverride,
                IsSelected    = canSelect && canonical.Length == 0,
                StatusLabel   = statusLabel,
                StatusBrush   = statusBrush,
            });
        }

        return [.. rows.OrderBy(r => FieldOrder(r.FieldKey))];
    }

    private static string GetCanonicalValue(ReleaseMetadataRecord r, string field) => field switch
    {
        "title"            => r.Title,
        "original_title"   => r.OriginalTitle,
        "sort_title"       => r.SortTitle,
        "developer"        => r.Developer,
        "publisher"        => r.Publisher,
        "year"             => r.Year,
        "languages"        => r.Languages,
        "alternate_titles" => r.AlternateTitles,
        "description"      => r.Description,
        "genre"            => r.Genre,
        "subgenre"         => r.Subgenre,
        "players"          => r.Players,
        "release_type"     => r.ReleaseType,
        "rating"           => r.Rating,
        "notes"            => r.Notes,
        _                  => "",
    };

    private static string FieldDisplayName(string field) => field switch
    {
        "title"            => "Title",
        "original_title"   => "Original Title",
        "sort_title"       => "Sort Title",
        "developer"        => "Developer",
        "publisher"        => "Publisher",
        "year"             => "Year",
        "languages"        => "Languages",
        "alternate_titles" => "Alternate Titles",
        "description"      => "Description",
        "genre"            => "Genre",
        "subgenre"         => "Subgenre",
        "players"          => "Players",
        "release_type"     => "Release Type",
        "rating"           => "Rating",
        "notes"            => "Notes",
        _                  => field,
    };

    private static int FieldOrder(string field) => field switch
    {
        "title"            => 0,
        "original_title"   => 1,
        "sort_title"       => 2,
        "developer"        => 3,
        "publisher"        => 4,
        "year"             => 5,
        "languages"        => 6,
        "genre"            => 7,
        "subgenre"         => 8,
        "players"          => 9,
        "release_type"     => 10,
        "rating"           => 11,
        "alternate_titles" => 12,
        "description"      => 13,
        "notes"            => 14,
        _                  => 99,
    };

    private void OnSelectAllSafe(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(r => r.CanSelect))
            row.IsSelected = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => (r.CanSelect || r.IsOverridden) && r.IsSelected).ToList();
        if (selected.Count == 0) { Close(null); return; }

        var current = _entry.Metadata ?? new ReleaseMetadataRecord { ReleaseId = _entry.ReleaseId };
        var store   = new DatLineStore(_entry.DbPath);

        var selections = selected
            .Select(r => (r.FieldKey, r.ProviderValue))
            .ToList();

        var merged = store.ApplyMergeSelections(_entry.ReleaseId, _provider, selections, current);

        Close(new MergeMetadataResult
        {
            Metadata      = merged,
            AppliedFields = selected.Select(r => r.FieldKey).ToHashSet(StringComparer.Ordinal),
        });
    }
}

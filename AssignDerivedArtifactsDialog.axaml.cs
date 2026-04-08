using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class AssignDerivedArtifactsDialog : Window
{
    public List<DerivedArtifactRecord> SelectedArtifacts { get; private set; } = [];
    private readonly List<DerivedArtifactRecord> _allArtifacts;

    // Parameterless ctor required by Avalonia XAML compiler
    public AssignDerivedArtifactsDialog() : this("", [], []) { }

    public AssignDerivedArtifactsDialog(
        string volumeLabel,
        List<DerivedArtifactRecord> artifacts,
        HashSet<string> alreadyAssignedIds)
    {
        InitializeComponent();

        _allArtifacts      = artifacts;
        SubtitleLabel.Text = $"Volume: {volumeLabel}";

        var rows = artifacts
            .Where(a => !alreadyAssignedIds.Contains(a.Id))
            .Select(a => new ArtifactRow
            {
                Id                 = a.Id,
                FileName           = a.FileName,
                Size               = a.DerivedSizeBytes,
                ContentIdentityKey = a.ContentIdentityKey,
            })
            .ToList();

        ArtifactList.ItemsSource = rows;
        ArtifactList.SelectionChanged += OnListSelectionChanged;
        UpdateFooter();
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateFooter();

    private void UpdateFooter()
    {
        var count = ArtifactList.SelectedItems?.Count ?? 0;
        SelectionCountLabel.Text = count == 0 ? "No artifacts selected" : $"{count} selected";
        ConfirmButton.IsEnabled  = count > 0;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var selectedIds = ArtifactList.SelectedItems?
            .OfType<ArtifactRow>()
            .Select(r => r.Id)
            .ToHashSet(System.StringComparer.Ordinal)
            ?? new HashSet<string>();

        SelectedArtifacts = _allArtifacts.Where(a => selectedIds.Contains(a.Id)).ToList();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

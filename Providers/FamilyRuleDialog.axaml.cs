using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class FamilyRuleDialog : Window
{
    private readonly List<string>    _allSourcefiles;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSelection;

    public FamilyRule? Result { get; private set; }

    // Public parameterless constructor — satisfies Avalonia runtime loader (AVLN3001).
    public FamilyRuleDialog()
    {
        _allSourcefiles = [];
        InitializeComponent();
    }

    public FamilyRuleDialog(IReadOnlyList<string> sourcefiles)
    {
        _allSourcefiles = [.. sourcefiles];
        InitializeComponent();
        PopulateList("");
    }

    public FamilyRuleDialog(FamilyRule existing, IReadOnlyList<string> sourcefiles)
        : this(sourcefiles)
    {
        NameBox.Text = existing.DisplayName;
        foreach (var v in existing.RuleValues)
            _selected.Add(v);
        PopulateList("");
        UpdateOkButton();
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e) =>
        UpdateOkButton();

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) =>
        PopulateList(SearchBox.Text ?? "");

    private void OnSourcefileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        foreach (var s in e.AddedItems.OfType<string>())
            _selected.Add(s);
        foreach (var s in e.RemovedItems.OfType<string>())
            _selected.Remove(s);
        SyncLabel();
        UpdateOkButton();
    }

    private void PopulateList(string filter)
    {
        _suppressSelection = true;
        try
        {
            var items = string.IsNullOrEmpty(filter)
                ? _allSourcefiles
                : _allSourcefiles
                    .Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            SourcefileList.ItemsSource = items;
            SourcefileList.SelectedItems?.Clear();

            foreach (var item in items)
                if (_selected.Contains(item))
                    SourcefileList.SelectedItems?.Add(item);
        }
        finally
        {
            _suppressSelection = false;
        }
        SyncLabel();
        UpdateOkButton();
    }

    private void SyncLabel() =>
        SelectedValueLabel.Text = _selected.Count == 0
            ? "(none)"
            : $"{_selected.Count} selected";

    private void UpdateOkButton() =>
        OkButton.IsEnabled =
            !string.IsNullOrWhiteSpace(NameBox.Text) && _selected.Count > 0;

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0 || _selected.Count == 0) return;
        Result = new FamilyRule(
            name,
            FamilyRule.SourcefileIn,
            [.. _selected.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)]);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

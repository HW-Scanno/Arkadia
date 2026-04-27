using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class PlaylistListDialog : Window
{
    private readonly List<string> _names;
    public string? Result { get; private set; }

    public PlaylistListDialog()
    {
        _names = [];
        InitializeComponent();
    }

    public PlaylistListDialog(List<string> names)
    {
        _names = [.. names];
        InitializeComponent();
        RefreshList();
    }

    private void RefreshList()
    {
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = _names;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        bool has = PlaylistList.SelectedItem is string;
        LoadButton.IsEnabled   = has;
        DeleteButton.IsEnabled = has;
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not string name) return;
        PlaylistStore.Delete(name);
        _names.Remove(name);
        RefreshList();
        LoadButton.IsEnabled   = false;
        DeleteButton.IsEnabled = false;
        if (_names.Count == 0) Close();
    }

    private void OnLoad(object? sender, RoutedEventArgs e)
    {
        Result = PlaylistList.SelectedItem as string;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

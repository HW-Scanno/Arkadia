using System.Collections.Generic;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class PickDriveDialog : Window
{
    public DiscoveredDisk? SelectedDrive { get; private set; }

    public PickDriveDialog() { InitializeComponent(); }

    public PickDriveDialog(List<DiscoveredDiskRow> drives)
    {
        InitializeComponent();
        DriveList.ItemsSource = drives;
    }

    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SelectedDrive        = (DriveList.SelectedItem as DiscoveredDiskRow)?.Source;
        SelectButton.IsEnabled = SelectedDrive is not null;
    }

    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null) return;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

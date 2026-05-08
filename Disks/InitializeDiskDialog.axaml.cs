using System.Collections.Generic;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class InitializeDiskDialog : Window
{
    public DiscoveredDisk? SelectedDrive { get; private set; }

    public InitializeDiskDialog() { InitializeComponent(); }

    public InitializeDiskDialog(string diskLabel, List<DiscoveredDiskRow> drives)
    {
        InitializeComponent();
        DialogTitle.Text   = $"Initialize Disk — {diskLabel}";
        DriveList.ItemsSource = drives;
    }

    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SelectedDrive      = (DriveList.SelectedItem as DiscoveredDiskRow)?.Source;
        InitButton.IsEnabled = SelectedDrive is not null;
    }

    private void OnInitialize(object? sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null) return;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

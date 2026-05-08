using System.Collections.Generic;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class AssignVolumeToDiskDialog : Window
{
    private readonly List<DiskRecord> _disks;

    public DiskRecord? SelectedDisk { get; private set; }

    // Parameterless ctor required by Avalonia XAML compiler
    public AssignVolumeToDiskDialog() : this("", []) { }

    public AssignVolumeToDiskDialog(string volumeLabel, List<DiskRecord> disks)
    {
        InitializeComponent();

        _disks              = disks;
        VolumeNameLabel.Text = volumeLabel;

        DiskCombo.ItemsSource   = disks.ConvertAll(d => $"{d.Label}  ({FormatBytes(d.DeclaredCapacityBytes)})");
        DiskCombo.SelectedIndex = disks.Count > 0 ? 0 : -1;
        UpdateDiskInfo();
    }

    private void OnDiskChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateDiskInfo();
        ConfirmButton.IsEnabled = DiskCombo.SelectedIndex >= 0;
    }

    private void UpdateDiskInfo()
    {
        var idx = DiskCombo.SelectedIndex;
        if (idx < 0 || idx >= _disks.Count)
        {
            DiskInfoText.IsVisible = false;
            ConfirmButton.IsEnabled = false;
            return;
        }
        var d = _disks[idx];
        DiskInfoText.Text      = $"Status: {d.Status}  |  {(string.IsNullOrEmpty(d.Model) ? "" : d.Model)}";
        DiskInfoText.IsVisible = true;
        ConfirmButton.IsEnabled = true;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var idx = DiskCombo.SelectedIndex;
        if (idx < 0 || idx >= _disks.Count) return;
        SelectedDisk = _disks[idx];
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string FormatBytes(long b)
    {
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F0} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}

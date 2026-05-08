using System;
using System.Collections.Generic;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class CreateDiskDialog : Window
{
    public DiskRecord?    Result       { get; private set; }
    public DiscoveredDisk? SelectedDrive { get; private set; }

    public CreateDiskDialog()
    {
        InitializeComponent();
        GeneratedLabelText.Text = "Auto-generated on confirmation";
    }

    private async void OnSelectDrive(object? sender, RoutedEventArgs e)
    {
        var drives = DiskDiscoveryService.DiscoverAll();
        var rows   = drives.ConvertAll(d => new DiscoveredDiskRow { Source = d });

        var picker = new PickDriveDialog(rows);
        var ok     = await picker.ShowDialog<bool>(this);
        if (!ok || picker.SelectedDrive is null) return;

        SelectedDrive = picker.SelectedDrive;

        // Build compact summary line
        var total = FormatBytes(SelectedDrive.TotalCapacityBytes);
        var free  = FormatBytes(SelectedDrive.FreeSpaceBytes);
        var fs    = SelectedDrive.DriveFormat.Length > 0 ? SelectedDrive.DriveFormat : "?";
        DriveInfoText.Text     = $"{SelectedDrive.Mountpoint}  |  {total}  |  {fs}  |  {free} free";
        DriveInfoBorder.IsVisible = true;

        // Best-effort hardware autofill (Windows-only, never blocks confirm)
        DiskHardwareInfo hw = System.OperatingSystem.IsWindows()
            ? DiskHardwareEnricher.TryGetInfo(SelectedDrive.Mountpoint)
            : default;
        if (hw.Manufacturer.Length > 0) BrandInput.Text  = hw.Manufacturer;
        if (hw.Model.Length > 0)        ModelInput.Text  = hw.Model;
        if (hw.SerialNumber.Length > 0) SerialInput.Text = hw.SerialNumber;

        ConfirmButton.IsEnabled = true;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null) return;
        var now    = DateTime.UtcNow;
        var family = (FamilyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "core";

        Result = new DiskRecord
        {
            Id                    = Guid.NewGuid().ToString("N"),
            Label                 = "",               // filled in by OnAddDisk after sequence commit
            Status                = "available",
            Family                = family,
            DeclaredCapacityBytes = SelectedDrive.TotalCapacityBytes,
            Filesystem            = SelectedDrive.DriveFormat,
            Brand                 = BrandInput.Text?.Trim()  ?? "",
            Model                 = ModelInput.Text?.Trim()  ?? "",
            Serial                = SerialInput.Text?.Trim() ?? "",
            CreatedAt             = now,
            UpdatedAt             = now,
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string FormatBytes(long b)
    {
        if (b >= 1L << 40) return $"{b / (double)(1L << 40):F1} TB";
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):F1} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):F1} MB";
        return $"{b / (double)(1L << 10):F0} KB";
    }
}

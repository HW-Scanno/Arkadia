using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class AmpPickerDialog : Window
{
    private AmpLocalPackageInfo? _selected;

    public AmpPickerDialog() : this([]) { }

    public AmpPickerDialog(IReadOnlyList<AmpLocalPackageInfo> packages)
    {
        InitializeComponent();
        var vms = packages.Select(p => new AmpPickerPackageVm(p)).ToList();
        PackageList.ItemsSource = vms;
        if (vms.Count == 1)
            PackageList.SelectedIndex = 0;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selected           = (PackageList.SelectedItem as AmpPickerPackageVm)?.Info;
        AcceptBtn.IsEnabled = _selected is not null;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnAccept(object? sender, RoutedEventArgs e) => Close(_selected);
}

internal sealed class AmpPickerPackageVm(AmpLocalPackageInfo info)
{
    public AmpLocalPackageInfo Info             => info;
    public string              FileName         => info.FileName;
    public string              StatusLabel      => info.Status;
    public string              SystemName       => info.SystemName.Length > 0 ? info.SystemName : "—";
    public string              DatLineId        => info.DatLineId.Length  > 0 ? info.DatLineId  : "—";
    public string              ReleaseCountText => info.ReleaseCount.ToString();
    public string MediaFileCountText            => info.MediaFileCount.ToString();
    public string SizeFormatted                 => AmpReportHelpers.FormatBytes(info.PackageBytes);
    public string ModifiedShort                 => info.LastWriteTimeUtc == default
        ? "—" : info.LastWriteTimeUtc.LocalDateTime.ToString("yyyy-MM-dd");

    public IBrush StatusBackground => new SolidColorBrush(info.Status switch
    {
        "Valid"                 => Color.Parse("#152415"),
        "Warning"               => Color.Parse("#1E1A10"),
        "Error" or "Unreadable" => Color.Parse("#2A1215"),
        _                       => Color.Parse("#1A1A2C"),
    });

    public IBrush StatusForeground => new SolidColorBrush(info.Status switch
    {
        "Valid"                 => Color.Parse("#4CAF50"),
        "Warning"               => Color.Parse("#E0A040"),
        "Error" or "Unreadable" => Color.Parse("#EF5350"),
        _                       => Color.Parse("#888899"),
    });
}

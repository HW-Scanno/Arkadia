using System.Collections.Generic;
using Arkadia.Volumes;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class MoveVolumeDialog : Window
{
    public VolumeDestination? SelectedDestination { get; private set; }

    public MoveVolumeDialog() { InitializeComponent(); }

    public MoveVolumeDialog(string volumeLabel, long requiredBytes,
                             List<VolumeDestination> destinations)
    {
        InitializeComponent();
        DialogTitle.Text  = $"Move Volume — {volumeLabel}";
        SubtitleText.Text = $"Required: {FormatBytes(requiredBytes)}  |  Select a destination below.";
        DestList.ItemsSource = destinations;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var dest = DestList.SelectedItem as VolumeDestination;
        // Only allow selecting READY destinations.
        if (dest is not null && !dest.IsSelectable)
        {
            DestList.SelectedItem = null;
            ConfirmButton.IsEnabled = false;
            return;
        }
        SelectedDestination     = dest;
        ConfirmButton.IsEnabled = dest is not null;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (SelectedDestination is null) return;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string FormatBytes(long b)
    {
        if (b <= 0)           return "0 B";
        if (b >= 1L << 40)    return $"{b / (double)(1L << 40):F1} TB";
        if (b >= 1L << 30)    return $"{b / (double)(1L << 30):F1} GB";
        if (b >= 1L << 20)    return $"{b / (double)(1L << 20):F1} MB";
        return $"{b / (double)(1L << 10):F0} KB";
    }
}

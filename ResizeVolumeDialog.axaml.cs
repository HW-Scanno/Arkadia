using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class ResizeVolumeDialog : Window
{
    public long ResultBytes { get; private set; }

    public ResizeVolumeDialog() : this("", 0) { }

    public ResizeVolumeDialog(string volumeLabel, long currentPlannedBytes)
    {
        InitializeComponent();
        Title           = "Resize Volume";
        TitleLabel.Text = $"Resize Volume — {volumeLabel}";
        var currentGb   = currentPlannedBytes / (1024.0 * 1024 * 1024);
        SizeInput.Text  = currentGb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnSizeChanged(object? sender, TextChangedEventArgs e)
    {
        var text = SizeInput.Text?.Trim() ?? "";
        bool ok  = double.TryParse(text,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var gb) && gb > 0;

        ErrorText.Text      = text.Length > 0 && !ok ? "Must be a positive number (e.g. 4.7)." : "";
        ErrorText.IsVisible = text.Length > 0 && !ok;
        ConfirmBtn.IsEnabled = ok;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (double.TryParse(SizeInput.Text?.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var gb) && gb > 0)
        {
            ResultBytes = (long)(gb * 1024L * 1024 * 1024);
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

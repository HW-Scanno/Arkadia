using System;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class CreateDiskDialog : Window
{
    public DiskRecord? Result { get; private set; }

    // Parameterless ctor required by Avalonia XAML compiler
    public CreateDiskDialog() { InitializeComponent(); }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var label    = LabelInput.Text?.Trim()    ?? "";
        var capText  = CapacityInput.Text?.Trim() ?? "";
        bool capOk   = double.TryParse(capText, System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out var cap) && cap > 0;

        string? err = label.Length == 0 ? null
            : capText.Length > 0 && !capOk ? "Capacity must be a positive number."
            : null;

        ErrorText.Text      = err ?? "";
        ErrorText.IsVisible = err is not null;
        ConfirmButton.IsEnabled = label.Length > 0 && capOk;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var capGb = double.Parse(CapacityInput.Text!.Trim(),
                       System.Globalization.CultureInfo.InvariantCulture);
        var now   = DateTime.UtcNow;

        Result = new DiskRecord
        {
            Id                    = Guid.NewGuid().ToString("N"),
            Label                 = LabelInput.Text!.Trim(),
            Status                = "available",
            DeclaredCapacityBytes = (long)(capGb * 1024 * 1024 * 1024),
            Filesystem            = FilesystemInput.Text?.Trim() ?? "",
            Brand                 = BrandInput.Text?.Trim()      ?? "",
            Model                 = ModelInput.Text?.Trim()       ?? "",
            Serial                = SerialInput.Text?.Trim()      ?? "",
            CreatedAt             = now,
            UpdatedAt             = now,
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class PlaylistNameDialog : Window
{
    public string? Result { get; private set; }

    public PlaylistNameDialog() => InitializeComponent();

    private void OnNameChanged(object? sender, TextChangedEventArgs e) =>
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        Result = name;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class DeleteImageConfirmDialog : Window
{
    public DeleteImageConfirmDialog() : this(string.Empty) { }

    public DeleteImageConfirmDialog(string fileName)
    {
        InitializeComponent();
        DlgFileName.Text = fileName;
    }

    private void OnConfirmInputChanged(object? sender, TextChangedEventArgs e)
        => DeleteButton.IsEnabled = ConfirmInput.Text == "OK";

    private void OnDelete(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

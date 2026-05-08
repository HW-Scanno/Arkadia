using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("", "") { }

    public ConfirmDialog(string title, string message)
    {
        InitializeComponent();
        Title             = title;
        TitleLabel.Text   = title;
        MessageLabel.Text = message;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender,  RoutedEventArgs e) => Close(false);
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class InfoDialog : Window
{
    public InfoDialog() : this("", "") { }

    public InfoDialog(string title, string message)
    {
        InitializeComponent();
        Title         = title;
        TitleLabel.Text   = title;
        MessageLabel.Text = message;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class CategoryIniPromptDialog : Window
{
    public bool ShouldDownload { get; private set; }

    public CategoryIniPromptDialog() => InitializeComponent();

    private void OnDownload(object? sender, RoutedEventArgs e)
    {
        ShouldDownload = true;
        Close();
    }

    private void OnSkip(object? sender, RoutedEventArgs e) => Close();
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class DeleteDatLineDialog : Window
{
    private readonly string _requiredName;

    // Parameterless ctor required by Avalonia XAML compiler
    public DeleteDatLineDialog() : this(string.Empty, string.Empty, string.Empty, 0) { }

    public DeleteDatLineDialog(
        string platformName,
        string datLineName,
        string authority,
        int    releaseCount)
    {
        InitializeComponent();

        _requiredName = datLineName;

        DlgPlatform.Text  = platformName;
        DlgName.Text      = datLineName;
        DlgAuthority.Text = authority;
        DlgReleases.Text  = $"{releaseCount:N0}";
    }

    private void OnConfirmInputChanged(object? sender, TextChangedEventArgs e)
        => DeleteButton.IsEnabled = ConfirmInput.Text == _requiredName;

    private void OnDelete(object? sender, RoutedEventArgs e)
        => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(false);
}

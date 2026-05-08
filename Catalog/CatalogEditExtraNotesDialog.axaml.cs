using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class CatalogEditExtraNotesDialog : Window
{
    public CatalogEditExtraNotesDialog() : this(null!, null) { }

    public CatalogEditExtraNotesDialog(string releaseTitle, string? currentNotes)
    {
        InitializeComponent();
        Title           = $"Edit Extra Notes — {releaseTitle}";
        HeaderTitle.Text = $"Edit Extra Notes — {releaseTitle}";
        NotesField.Text  = currentNotes ?? "";
    }

    private void OnSave(object? sender, RoutedEventArgs e)
        => Close(NotesField.Text);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(null);
}

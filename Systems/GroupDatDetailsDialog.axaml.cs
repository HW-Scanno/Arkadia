using System.Collections.Generic;
using Arkadia.Systems;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Read-only Group DAT Details window: a compact, scrollable list of the group's leaves (Leaf DAT / source
/// path / media type / releases). The window opens immediately and stays responsive while its rows are
/// loaded off the UI thread — a spinner and a live count are shown until loading completes. Selecting a leaf
/// enables "Go to Library", which closes the dialog and reports the chosen leaf's authoritative
/// <c>DatLineId</c> (never a group id, path, or display label) so the caller can open the existing
/// Single-DAT Library scoped to that one dat_line. Pure display: no catalog/filesystem access, no mutation.
/// </summary>
public partial class GroupDatDetailsDialog : Window
{
    private bool _rowsLoaded;

    /// <summary>The authoritative dat_line id chosen via "Go to Library"; null if the dialog was just closed.</summary>
    public string? SelectedLeafId { get; private set; }

    // Parameterless ctor required by the Avalonia XAML compiler.
    public GroupDatDetailsDialog() : this("Group DAT", "") { }

    public GroupDatDetailsDialog(GroupDatCardInfo card) : this(card.DisplayName, card.Subtitle) { }

    public GroupDatDetailsDialog(string headerName, string headerSub)
    {
        InitializeComponent();
        HeaderName.Text = headerName;
        HeaderSub.Text  = headerSub;
        // Opens in the loading state: spinner visible, list empty, "Go to Library" disabled.
    }

    /// <summary>Live progress while rows are being mapped off the UI thread ("173 / 410").</summary>
    public void SetProgress(int loaded, int total)
        => LoadStatus.Text = $"Loading leaf details…  {loaded} / {total}";

    /// <summary>Final population: show the rows, stop the spinner, and report the loaded count.</summary>
    public void SetRows(IReadOnlyList<GroupDatDetailsRow> rows)
    {
        LeavesList.ItemsSource = rows;
        LoadSpinner.IsVisible  = false;
        LoadStatus.Text        = $"{rows.Count} / {rows.Count} loaded";
        _rowsLoaded            = true;
        UpdateGoToLibraryState();
    }

    private GroupDatDetailsRow? SelectedRow => LeavesList.SelectedItem as GroupDatDetailsRow;

    // "Go to Library" is available only once the rows have loaded AND a leaf is selected.
    private void UpdateGoToLibraryState()
        => GoToLibraryButton.IsEnabled = GroupDatDetailsGate.CanGoToLibrary(_rowsLoaded, SelectedRow is not null);

    private void OnLeafSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateGoToLibraryState();

    private void OnLeafDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_rowsLoaded && SelectedRow is not null) GoToLibrary();
    }

    private void OnGoToLibrary(object? sender, RoutedEventArgs e) => GoToLibrary();

    private void GoToLibrary()
    {
        // LeafId is the dat_line id (GroupDatDetails.BuildRows sets it from DatLine.Id) — the source of truth.
        if (SelectedRow is not { } row) return;
        SelectedLeafId = row.LeafId;
        Close(true);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}

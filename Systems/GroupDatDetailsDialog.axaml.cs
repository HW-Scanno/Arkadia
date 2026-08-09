using System.Collections.Generic;
using Arkadia.Systems;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Read-only Group DAT Details window: a compact, scrollable list of the group's leaves (Leaf DAT / source
/// path / media type / releases). The window opens immediately and stays responsive while its rows are
/// loaded off the UI thread — a spinner and a live count are shown until loading completes. Pure display:
/// it performs no catalog/filesystem access itself and no mutation (the caller supplies the rows).
/// </summary>
public partial class GroupDatDetailsDialog : Window
{
    // Parameterless ctor required by the Avalonia XAML compiler.
    public GroupDatDetailsDialog() : this("Group DAT", "") { }

    public GroupDatDetailsDialog(GroupDatCardInfo card) : this(card.DisplayName, card.Subtitle) { }

    public GroupDatDetailsDialog(string headerName, string headerSub)
    {
        InitializeComponent();
        HeaderName.Text = headerName;
        HeaderSub.Text  = headerSub;
        // Opens in the loading state: spinner visible, list empty.
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
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}

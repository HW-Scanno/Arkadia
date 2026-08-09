using System.Collections.Generic;
using Arkadia.Systems;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Read-only Group DAT Details window: a compact, scrollable list of the group's leaves (Leaf DAT / source
/// path / media type / releases). Pure display — it receives already-built rows and performs no catalog or
/// filesystem access and no mutation.
/// </summary>
public partial class GroupDatDetailsDialog : Window
{
    // Parameterless ctor required by the Avalonia XAML compiler.
    public GroupDatDetailsDialog() : this("Group DAT", "", new List<GroupDatDetailsRow>()) { }

    public GroupDatDetailsDialog(GroupDatCardInfo card, IReadOnlyList<GroupDatDetailsRow> rows)
        : this(card.DisplayName, card.Subtitle, rows) { }

    public GroupDatDetailsDialog(string headerName, string headerSub, IReadOnlyList<GroupDatDetailsRow> rows)
    {
        InitializeComponent();
        HeaderName.Text  = headerName;
        HeaderSub.Text   = headerSub;
        LeavesList.ItemsSource = rows;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}

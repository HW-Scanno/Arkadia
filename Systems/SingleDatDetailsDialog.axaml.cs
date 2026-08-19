using System.Collections.Generic;
using Arkadia.Systems;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Read-only Single DAT Details window: a compact table of the volumes that store this DAT's artifacts (Volume /
/// Artifacts / Size / Disk). Informational only — no Move / Verify / Repair / Reabsorb controls. The Disk column
/// is resolved from persisted catalog location alone (no live disk discovery). Rows are supplied fully built by
/// the caller (few volumes, on-demand), so the dialog itself does no catalog or filesystem access.
/// </summary>
public partial class SingleDatDetailsDialog : Window
{
    // Parameterless ctor required by the Avalonia XAML compiler.
    public SingleDatDetailsDialog() : this("DAT Details", "", System.Array.Empty<SingleDatVolumeRow>()) { }

    public SingleDatDetailsDialog(string headerName, string headerSub, IReadOnlyList<SingleDatVolumeRow> rows)
    {
        InitializeComponent();
        HeaderName.Text        = headerName;
        HeaderSub.Text         = headerSub;
        VolumesList.ItemsSource = rows;
        EmptyState.IsVisible   = rows.Count == 0;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

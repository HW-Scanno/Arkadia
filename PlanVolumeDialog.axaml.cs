using System.Linq;
using Arkadia.Data;
using Arkadia.Volumes;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class PlanVolumeDialog : Window
{
    // Parameterless ctor required by Avalonia XAML compiler
    public PlanVolumeDialog() : this(null!) { }

    public PlanVolumeDialog(PlanningResult result)
    {
        InitializeComponent();
        if (result is null) return;
        Populate(result);
    }

    private void Populate(PlanningResult r)
    {
        StatCapacity.Text       = FormatBytes(r.VolumeCapacityBytes);
        StatUsed.Text           = FormatBytes(r.VolumeActualSizeBytes);
        StatRemainingBefore.Text = FormatBytes(r.RemainingBytesBeforePlanning);
        StatPlanned.Text        = FormatBytes(r.PlannedBytes);
        StatRemainingAfter.Text = FormatBytes(r.RemainingBytesAfterPlanning);
        StatCandidates.Text     = r.Items.Count.ToString("N0");
        StatIncluded.Text       = r.Items.Count(i => i.Decision == "include").ToString("N0");
        StatDeferred.Text       = r.Items.Count(i => i.Decision == "defer").ToString("N0");

        // Replace raw byte values in the Size column with formatted strings
        // by projecting into display rows.
        DecisionList.ItemsSource = r.Items
            .Select(d => new PlanningDisplayRow
            {
                Decision      = d.Decision,
                ReleaseName   = d.ReleaseName,
                DerivedCount  = d.DerivedCount,
                SizeLabel     = FormatBytes(d.TotalSizeBytes),
                Reason        = d.Reason,
            })
            .ToList();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "0 B";
        if (b < 1024L)                return $"{b} B";
        if (b < 1024L * 1024)         return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)  return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}

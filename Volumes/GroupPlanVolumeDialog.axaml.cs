using System.Linq;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

/// <summary>One collapsed per-leaf summary row in the Group volume plan preview.</summary>
public sealed class GroupLeafDisplayRow
{
    public required string DatLineId   { get; init; }
    public required string DisplayName { get; init; }
    public required string CountsLabel { get; init; }   // "1,428 candidates · 1,402 included"
    public required string SizeLabel   { get; init; }   // included bytes
}

/// <summary>
/// Read-only Group volume plan preview (V-G2). Opens with a spinner while the plan is computed off the UI
/// thread, then shows the capacity header, a readiness banner, and a compact per-leaf summary list. There is
/// NO Proceed/Apply — Close only. It renders a frozen <see cref="GroupVolumePlanningResult"/> and never mutates
/// anything.
/// </summary>
public partial class GroupPlanVolumeDialog : Window
{
    public GroupPlanVolumeDialog() : this("Group Volume") { }

    public GroupPlanVolumeDialog(string volumeLabel)
    {
        InitializeComponent();
        DialogTitle.Text = $"Group Volume Plan — {volumeLabel}";
    }

    /// <summary>Live per-leaf planning progress (marshalled to the UI thread by <see cref="System.Progress{T}"/>).</summary>
    public void UpdateProgress(GroupVolumePlanProgress p) => LoadStatus.Text = p.PhaseText;

    /// <summary>Show the finished, frozen plan.</summary>
    public void SetResult(GroupVolumePlanningResult r)
    {
        LoadingRow.IsVisible  = false;
        StatsPanel.IsVisible  = true;

        StatCapacity.Text        = FormatBytes(r.CapacityBytes);
        StatUsed.Text            = FormatBytes(r.ActualSizeBytes);
        StatRemainingBefore.Text = FormatBytes(r.RemainingBeforeBytes);
        StatPlanned.Text         = FormatBytes(r.PlannedBytes);
        StatRemainingAfter.Text  = FormatBytes(r.RemainingAfterBytes);
        StatLeaves.Text          = r.Leaves.Length.ToString("N0");
        StatIncluded.Text        = r.IncludeCount.ToString("N0");
        StatSkipDefer.Text       = $"{r.SkipCount:N0} / {r.DeferCount:N0}";

        (string text, string bg, string fg) = r.Readiness switch
        {
            GroupVolumeReadiness.Ready       => ($"Ready — {r.IncludeCount:N0} release(s) would be planned across {r.Leaves.Length:N0} leaf DAT(s).", "#12281A", "#7BE0A0"),
            GroupVolumeReadiness.NothingToDo => ("Nothing to plan — no unassigned, archive-complete releases fit the remaining capacity.", "#1A1A24", "#AAAACC"),
            _                                => ($"NOT READY — {r.Diagnostics.Length} leaf DAT(s) could not be read:\n{string.Join("\n", r.Diagnostics)}", "#281212", "#EF8A8A"),
        };
        ReadinessBanner.IsVisible = true;
        ReadinessBanner.Background = new SolidColorBrush(Color.Parse(bg));
        ReadinessText.Foreground   = new SolidColorBrush(Color.Parse(fg));
        ReadinessText.Text         = text;

        LeafList.ItemsSource = r.Leaves.Select(l => new GroupLeafDisplayRow
        {
            DatLineId   = l.DatLineId,
            DisplayName = l.DisplayName,
            CountsLabel = $"{l.Items.Count:N0} candidates · {l.IncludeCount:N0} included",
            SizeLabel   = FormatBytes(l.PlannedBytes),
        }).ToList();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "0 B";
        if (b < 1024L)                return $"{b} B";
        if (b < 1024L * 1024)         return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)  return $"{b / (1024.0 * 1024):F1} MB";
        if (b < 1024L * 1024 * 1024 * 1024) return $"{b / (1024.0 * 1024 * 1024):F2} GB";
        return $"{b / (1024.0 * 1024 * 1024 * 1024):F2} TB";
    }
}

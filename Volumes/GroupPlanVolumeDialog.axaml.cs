using System;
using System.Linq;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Volumes;
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
/// Group volume plan preview + Apply &amp; Build (V-G2 preview, V-G3 execution). Opens with a spinner while the
/// plan is computed off the UI thread, then shows the capacity header, a readiness banner, and a compact per-leaf
/// summary list. When the plan is <see cref="GroupVolumeReadiness.Ready"/> the single affirmative "Apply &amp;
/// Build" action is enabled; clicking it invokes <see cref="ExecuteHandler"/> (stale-plan guard → Apply → local
/// Build, off the UI thread) and reports the outcome in-place. Cancel/Close never mutate anything.
/// </summary>
public partial class GroupPlanVolumeDialog : Window
{
    /// <summary>
    /// Runs the confirmed Apply + local Build off the UI thread. Returns the execution result, or throws with a
    /// user-facing message (e.g. the stale-plan guard tripped). Set by the caller before <see cref="ShowDialog"/>.
    /// </summary>
    public Func<IProgress<GroupVolumeBuildProgress>, Task<GroupVolumeExecutionResult>>? ExecuteHandler { get; set; }

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

        // The one affirmative action is offered only for a Ready plan and only when an executor is wired.
        ApplyButton.IsVisible = r.Readiness == GroupVolumeReadiness.Ready && ExecuteHandler is not null;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);

    // ── Apply & Build (V-G3) — confirmed, in-place, non-blocking ──
    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (ExecuteHandler is null) return;

        ApplyButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        LoadingRow.IsVisible  = true;
        LoadSpinner.IsVisible = true;
        LoadStatus.Text       = "Applying plan…";

        var progress = new Progress<GroupVolumeBuildProgress>(p =>
            LoadStatus.Text = p.Total > 0 ? $"{p.PhaseText} — {p.Index:N0}/{p.Total:N0}" : p.PhaseText);

        try
        {
            var result = await ExecuteHandler(progress);
            LoadSpinner.IsVisible = false;
            LoadingRow.IsVisible  = false;

            var (text, bg, fg) = result.Status == GroupBuildStatus.NothingToBuild
                ? ("Nothing to build — no assigned artifacts to materialize.", "#1A1A24", "#AAAACC")
                : ($"Built — {result.MovedCount:N0} artifact(s) materialized" +
                   (result.AlreadyBuiltCount > 0 ? $", {result.AlreadyBuiltCount:N0} already present" : "") +
                   $" across {result.LeafCount:N0} leaf DAT(s).", "#12281A", "#7BE0A0");

            ReadinessBanner.Background = new SolidColorBrush(Color.Parse(bg));
            ReadinessText.Foreground   = new SolidColorBrush(Color.Parse(fg));
            ReadinessText.Text         = text;

            CloseButton.Content   = "Done";
            CloseButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LoadSpinner.IsVisible = false;
            LoadingRow.IsVisible  = false;
            ReadinessBanner.Background = new SolidColorBrush(Color.Parse("#281212"));
            ReadinessText.Foreground   = new SolidColorBrush(Color.Parse("#EF8A8A"));
            ReadinessText.Text         = ex.Message;
            CloseButton.IsEnabled = true;   // stays "Cancel" — nothing committed on guard/abort paths
        }
    }

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

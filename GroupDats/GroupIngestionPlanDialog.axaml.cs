using System.Linq;
using Arkadia.Ingestion;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

/// <summary>
/// Read-only Group ingestion planning/readiness preview (M9C1). Opens immediately with a spinner while the
/// planner runs off the UI thread, then shows scope counts, File Handling, incoming inspection, readiness,
/// and any blocking diagnostics. No leaf selection, no Execute button — planning only.
/// </summary>
public partial class GroupIngestionPlanDialog : Window
{
    /// <summary>True when the user explicitly pressed Execute (only possible for a Ready plan).</summary>
    public bool ExecuteRequested { get; private set; }

    public GroupIngestionPlanDialog() : this("Group") { }

    public GroupIngestionPlanDialog(string displayName)
    {
        InitializeComponent();
        HeaderName.Text = $"Group Ingestion — {displayName}";
    }

    /// <summary>Live planning progress — marshalled to the UI thread by <see cref="System.Progress{T}"/>.</summary>
    public void Update(GroupIngestionPlanProgress p) => LoadStatus.Text = p.PhaseText;

    /// <summary>Show the finished, frozen plan.</summary>
    public void SetPlan(GroupIngestionPlan plan)
    {
        LoadingRow.IsVisible   = false;
        ResultsScroll.IsVisible = true;

        // Execute is shown ONLY for a Ready plan (Blocked / NothingToDo → Close only).
        ExecuteButton.IsVisible = GroupExecuteGate.CanExecute(plan.Readiness);

        (string text, string bg, string fg) = plan.Readiness switch
        {
            GroupIngestionReadiness.Ready       => ($"Ready — {plan.InScopeLeafCount} leaf DAT(s) require work.", "#12281A", "#7BE0A0"),
            GroupIngestionReadiness.NothingToDo => ("Nothing to do — every leaf is already satisfied. No ingestion work is currently required.", "#1A1A24", "#AAAACC"),
            _                                   => ($"Blocked — {plan.BlockedLeafCount} leaf DAT(s) require attention. No ingestion may run.", "#281212", "#EF8A8A"),
        };
        ReadinessBanner.Background = new SolidColorBrush(Color.Parse(bg));
        ReadinessText.Foreground   = new SolidColorBrush(Color.Parse(fg));
        ReadinessText.Text         = text;

        AddCount("Group leaves", plan.TotalLeaves.ToString("N0"));
        AddCount("Leaves requiring work", plan.InScopeLeafCount.ToString("N0"));
        AddCount("Already satisfied", plan.SatisfiedLeafCount.ToString("N0"));
        AddCount("Missing targets", plan.MissingTargetCount.ToString("N0"));
        AddCount("Resumable releases", plan.ResumableCount.ToString("N0"));
        AddCount("File Handling", plan.FileHandling switch
        {
            "archives_pre_extraction" => "Archives Pre-Extraction",
            "all_files"               => "All Files",
            null                      => plan.InScopeLeafCount == 0 ? "—" : "Mixed",
            var v                     => v,
        });
        AddCount("Incoming — loose files", plan.IncomingInspection.LooseFiles.ToString("N0"));
        AddCount("Incoming — archive containers", plan.IncomingInspection.ArchiveContainers.ToString("N0"));

        if (plan.Diagnostics.Length > 0)
        {
            DiagnosticsTitle.IsVisible = true;
            DiagnosticsBox.IsVisible   = true;
            DiagnosticsBox.Text = string.Join("\n", plan.Diagnostics.Select(d =>
                d.DatLineId.Length > 0 ? $"{d.DatLineId}:  {d.Reason}" : d.Reason));
        }
    }

    private void AddCount(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#888899")) });
        var val = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.Parse("#E8E8F0")) };
        Grid.SetColumn(val, 1);
        grid.Children.Add(val);
        CountsPanel.Children.Add(grid);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);

    private void OnExecute(object? sender, RoutedEventArgs e)
    {
        ExecuteRequested = true;
        Close(true);
    }
}

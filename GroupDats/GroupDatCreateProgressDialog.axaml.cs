using System;
using Arkadia.GroupDats;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>
/// Minimal progress + result window for a Group-DAT Create execution. Shows live phase progress while the
/// executor runs, then a single terminal result (success / error / manual-cleanup) with an OK button. No
/// cancel button — cancellation UX is out of scope for M4 (the executor stays cancellation-capable). The
/// window cannot be closed while the work is running.
/// </summary>
public partial class GroupDatCreateProgressDialog : Window
{
    private bool _running = true;

    // Parameterless ctor required by the Avalonia XAML compiler.
    public GroupDatCreateProgressDialog() : this("Create Group DAT") { }

    public GroupDatCreateProgressDialog(string title)
    {
        InitializeComponent();
        OpTitle.Text = title;
    }

    /// <summary>Live progress tick — marshalled to the UI thread by <see cref="System.Progress{T}"/>.</summary>
    public void Update(GroupDatExecutionProgress p)
    {
        PhaseText.Text = p.Text;

        // Per-leaf phases have a real N/total; the catalog/cleanup phases are indeterminate.
        bool determinate = p.Total > 0 && p.Phase is
            GroupDatExecutionPhase.Revalidating or
            GroupDatExecutionPhase.Preparing or
            GroupDatExecutionPhase.Publishing;

        OpProgress.IsIndeterminate = !determinate;
        OpProgress.Maximum         = p.Total > 0 ? p.Total : 100;
        OpProgress.Value           = p.Index;
    }

    /// <summary>Transition to the terminal result state and enable OK.</summary>
    public void SetResult(GroupDatCreatePresentation presentation)
    {
        _running = false;

        RunningPanel.IsVisible = false;
        ResultPanel.IsVisible  = true;

        ResultTitle.Text   = presentation.Title.ToUpperInvariant();
        ResultMessage.Text = presentation.Message;

        ResultTitle.Foreground = presentation.Kind switch
        {
            GroupDatCreatePresentationKind.Success => Avalonia.Media.Brushes.MediumSeaGreen,
            GroupDatCreatePresentationKind.Warning => Avalonia.Media.Brushes.Orange,
            _                                      => Avalonia.Media.Brushes.IndianRed,
        };

        if (presentation.CleanupPaths.Count > 0)
        {
            PathsBox.IsVisible = true;
            PathsBox.Text      = string.Join(Environment.NewLine, presentation.CleanupPaths);
        }

        OkButton.IsEnabled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_running) e.Cancel = true;   // cannot close mid-execution
        base.OnClosing(e);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
}

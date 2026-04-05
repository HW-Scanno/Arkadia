using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class UpdateDatSummaryDialog : Window
{
    // Parameterless ctor required by Avalonia XAML compiler
    public UpdateDatSummaryDialog() : this("", "", new ReconciliationResult()) { }

    public UpdateDatSummaryDialog(
        string                platformName,
        string                datLineId,
        ReconciliationResult  result)
    {
        InitializeComponent();

        SumPlatform.Text  = platformName;
        SumId.Text        = datLineId;
        SumKept.Text      = result.Kept.ToString("N0");
        SumOutdated.Text  = result.Outdated.ToString("N0");
        SumPending.Text   = result.Pending.ToString("N0");
        SumMissing.Text   = result.Missing.ToString("N0");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

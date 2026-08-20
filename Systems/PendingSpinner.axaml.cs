using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Arkadia.Systems;

/// <summary>
/// A compact clockwise-rotating circular-arrow indicator meaning "Arkadia is actively calculating this row"
/// (never "unknown forever"). Reused wherever a System/Group aggregate is pending. The rotation is a pure
/// Avalonia looping animation defined in XAML — no extra dependency, no animation framework.
/// </summary>
public partial class PendingSpinner : UserControl
{
    public PendingSpinner() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

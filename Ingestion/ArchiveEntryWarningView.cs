using Arkadia.Ingestion;
using Avalonia.Controls;
using Avalonia.Media;

namespace Arkadia;

/// <summary>
/// Shared renderer for the Configure DAT / Configure Group archive-entry notice, so both dialogs present it
/// identically: amber for a <see cref="DatArchiveWarningSeverity.Warning"/>, dimmed for
/// <see cref="DatArchiveWarningSeverity.Info"/>, hidden for <see cref="DatArchiveWarningSeverity.None"/>. Purely visual —
/// it never touches validation, Save/Apply, or configuration values.
/// </summary>
internal static class ArchiveEntryWarningView
{
    private static readonly IBrush WarnTitle = new SolidColorBrush(Color.Parse("#E8A000"));
    private static readonly IBrush WarnBody  = new SolidColorBrush(Color.Parse("#CCCCDD"));
    private static readonly IBrush InfoTitle = new SolidColorBrush(Color.Parse("#888899"));
    private static readonly IBrush InfoBody  = new SolidColorBrush(Color.Parse("#77778A"));
    private static readonly IBrush Dim       = new SolidColorBrush(Color.Parse("#555566"));

    public static void Render(StackPanel panel, DatArchiveWarning warning)
    {
        panel.Children.Clear();
        panel.IsVisible = warning.Severity != DatArchiveWarningSeverity.None;
        if (!panel.IsVisible) return;

        bool warn  = warning.Severity == DatArchiveWarningSeverity.Warning;
        var  title = warn ? WarnTitle : InfoTitle;
        var  body  = warn ? WarnBody  : InfoBody;

        panel.Children.Add(new TextBlock
        {
            Text = warning.Title, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = title,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var line in warning.Message)
            panel.Children.Add(new TextBlock
            {
                Text = line, FontSize = 11, Foreground = body, TextWrapping = TextWrapping.Wrap,
            });

        foreach (var example in warning.Examples)
            panel.Children.Add(new TextBlock
            {
                Text = "  " + example, FontSize = 11, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Consolas, monospace"),
            });

        if (warning.MoreLine is { } more)
            panel.Children.Add(new TextBlock { Text = "  " + more, FontSize = 11, Foreground = Dim });

        if (warning.IncompleteNote is { } note)
            panel.Children.Add(new TextBlock
            {
                Text = note, FontSize = 10, Foreground = Dim, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap,
            });
    }

    /// <summary>A subtle one-line status (e.g. while the Group scan runs).</summary>
    public static void RenderStatus(StackPanel panel, string text)
    {
        panel.Children.Clear();
        panel.IsVisible = true;
        panel.Children.Add(new TextBlock { Text = text, FontSize = 10, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
    }
}

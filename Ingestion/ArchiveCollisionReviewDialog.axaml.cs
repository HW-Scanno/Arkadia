using System.Collections.Generic;
using Arkadia.Archive;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

/// <summary>
/// First usable archive-output collision review dialog. Thin shell over
/// <see cref="ArchiveCollisionReviewSession"/> (all decision logic lives there).
/// Shows the current colliding pair (A | B) in a shared vertical ScrollViewer.
/// Exclude A/B marks the release unwanted (existing curation — no files deleted),
/// re-validates, and advances; when the group/plan is resolved the dialog closes
/// with <c>true</c>. Abort closes with <c>false</c> and applies nothing further.
/// </summary>
public partial class ArchiveCollisionReviewDialog : Window
{
    private readonly ArchiveCollisionReviewSession _session = null!;
    private readonly string _datLineName = "";

    public ArchiveCollisionReviewDialog() { InitializeComponent(); }

    public ArchiveCollisionReviewDialog(ArchiveCollisionReviewSession session, string datLineName)
    {
        _session     = session;
        _datLineName = datLineName;
        InitializeComponent();
        RenderCurrent();
    }

    private void RenderCurrent()
    {
        var pair = _session.CurrentPair();
        if (pair is null)
        {
            // No collisions remain — resolved.
            Close(true);
            return;
        }

        HeaderText.Text = $"Archive Output Collision — {_datLineName}";
        SubHeaderText.Text =
            $"Output form: {_session.Form}    ·    Colliding name: \"{pair.ArchiveEntryName}\"    ·    " +
            $"Releases in group: {pair.GroupSize}\n" +
            "These releases would produce the same archive artifact name. " +
            "Exclude one (marks it unwanted — no files are deleted) or abort.";

        Populate(ColumnA, "A", pair.A);
        Populate(ColumnB, "B", pair.B);
    }

    private static void Populate(StackPanel col, string label, ArchiveOutputCandidate c)
    {
        col.Children.Clear();
        Head(col, $"Release {label}");
        Field(col, "Title",             c.ReleaseName);
        Field(col, "Safe release name", c.SafeReleaseName);
        Field(col, "Status",            c.Status);
        Field(col, "Output form",       c.Form.ToString());
        Field(col, "Planned filename",  c.PlannedFilename.Length > 0 ? c.PlannedFilename : "(folder)");
        Field(col, "Planned path",      c.PlannedRelativePath);
        Field(col, "Main/source input", c.MainInputFile.Length > 0 ? c.MainInputFile : "—");
        Field(col, "Content identity",  c.ContentIdentityKey ?? "—");
        Field(col, "Total source size", c.TotalSourceBytes > 0 ? FormatBytes(c.TotalSourceBytes) : "—");
        Field(col, "Collision reason",  "Same archive artifact name as the other release.");

        Head(col, "Source files");
        if (c.SourceFiles.Count == 0)
            Field(col, "", "(none)");
        foreach (var f in c.SourceFiles)
        {
            var size  = f.SizeBytes is { } b ? FormatBytes(b) : "?";
            var hash  = f.Sha1.Length > 0 ? $"sha1 {Short(f.Sha1)}"
                      : f.Md5.Length  > 0 ? $"md5 {Short(f.Md5)}"
                      : f.Crc.Length  > 0 ? $"crc {f.Crc}"
                      : "no hash";
            Field(col, f.RomName, $"{size}  ·  {hash}");
        }
    }

    // ── Actions ────────────────────────────────────────────────────────────────

    private async void OnExcludeA(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmExclude()) return;
        _session.ExcludeA();
        RenderCurrent();
    }

    private async void OnExcludeB(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmExclude()) return;
        _session.ExcludeB();
        RenderCurrent();
    }

    private void OnAbort(object? sender, RoutedEventArgs e)
    {
        _session.Abort();
        Close(false);
    }

    private System.Threading.Tasks.Task<bool> ConfirmExclude() =>
        new ConfirmDialog(
            "Exclude Release",
            "Mark this release as unwanted and exclude it from this DAT line?\n\nNo files will be deleted.")
            .ShowDialog<bool>(this);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void Head(StackPanel col, string text) =>
        col.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 1,
            Foreground = new SolidColorBrush(Color.Parse("#7B68EE")),
            Margin = new Avalonia.Thickness(0, 10, 0, 2),
        });

    private static void Field(StackPanel col, string label, string value)
    {
        var sp = new StackPanel { Spacing = 0, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        if (label.Length > 0)
            sp.Children.Add(new TextBlock
            {
                Text = label, FontSize = 9, Foreground = new SolidColorBrush(Color.Parse("#555566")),
            });
        sp.Children.Add(new TextBlock
        {
            Text = value, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
            TextWrapping = TextWrapping.Wrap,
        });
        col.Children.Add(sp);
    }

    private static string Short(string h) => h.Length >= 8 ? h[..8] + "…" : h;

    private static string FormatBytes(long b)
    {
        if (b < 1024L)               return $"{b} B";
        if (b < 1024L * 1024)        return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}

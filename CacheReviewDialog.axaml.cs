using System.IO;
using System.Linq;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Arkadia;

public partial class CacheReviewDialog : Window
{
    private readonly ScreenScraperCacheSearchService _search;
    private readonly string? _systemId;
    private ScreenScraperCacheCandidate? _selected;

    public CacheReviewDialog() : this(null!, null, null) { }

    public CacheReviewDialog(
        ScreenScraperCacheSearchService search,
        string? initialQuery,
        string? systemId)
    {
        InitializeComponent();
        _search   = search;
        _systemId = systemId;

        if (initialQuery is { Length: > 0 })
        {
            SearchBox.Text = initialQuery;
            RunSearch(initialQuery);
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnSearch(object? sender, RoutedEventArgs e)
        => RunSearch(SearchBox.Text?.Trim() ?? "");

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
            RunSearch(SearchBox.Text?.Trim() ?? "");
    }

    private void RunSearch(string query)
    {
        CandidateList.ItemsSource = null;
        _selected = null;
        AcceptBtn.IsEnabled = false;
        HidePreview();

        if (query.Length == 0)
        {
            StatusText.Text       = "";
            NoResultsText.IsVisible = false;
            return;
        }

        var results = _search.Search(query, _systemId);

        if (results.Count == 0)
        {
            NoResultsText.IsVisible = true;
            StatusText.Text         = "No results found.";
        }
        else
        {
            NoResultsText.IsVisible   = false;
            StatusText.Text           = $"{results.Count} result{(results.Count == 1 ? "" : "s")} found.";
            CandidateList.ItemsSource = results;

            if (ShouldAutoSelect(results.Count))
                CandidateList.SelectedIndex = 0;
        }
    }

    internal static bool ShouldAutoSelect(int candidateCount) => candidateCount == 1;

    // ── Selection ─────────────────────────────────────────────────────────────

    private void OnCandidateSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is not ScreenScraperCacheCandidate c)
        {
            _selected = null;
            AcceptBtn.IsEnabled = false;
            HidePreview();
            return;
        }

        _selected = c;
        AcceptBtn.IsEnabled = true;
        ShowPreview(c);
    }

    private void ShowPreview(ScreenScraperCacheCandidate c)
    {
        PreviewTitle.Text  = c.Title;
        PreviewSystem.Text = c.SystemName;
        PreviewGameId.Text = c.ProviderGameId;
        PreviewPackage.Text = ShortenPath(c.PackagePath);
        PreviewMediaBadge.IsVisible = c.HasMedia;

        PreviewPanel.IsVisible      = true;
        PreviewPlaceholder.IsVisible = false;
    }

    private void HidePreview()
    {
        PreviewPanel.IsVisible       = false;
        PreviewPlaceholder.IsVisible = true;
    }

    private static string ShortenPath(string path)
    {
        if (path.Length <= 60) return path;
        var dir  = Path.GetDirectoryName(path) ?? "";
        var file = Path.GetFileName(path);
        var trimmed = dir.Length > 30 ? "…" + dir[^27..] : dir;
        return Path.Combine(trimmed, file);
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnAccept(object? sender, RoutedEventArgs e) => Close(_selected);
}

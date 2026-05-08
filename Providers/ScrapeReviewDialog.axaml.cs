using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Arkadia;

/// <summary>
/// Returned by <see cref="ScrapeReviewDialog"/> when the user accepts a candidate.
/// Exactly one of <see cref="Candidate"/> or <see cref="DirectResult"/> is non-null.
/// </summary>
public sealed class ScrapeReviewResult
{
    /// <summary>
    /// Set when the user accepted a normal search candidate.
    /// Caller should call <c>FetchDetailsByGameIdAsync</c> to retrieve the full record.
    /// </summary>
    public ScraperCandidate? Candidate { get; init; }

    /// <summary>
    /// Set when the user accepted a direct ROM/DAT fallback result.
    /// Caller should use this result directly without a second API call.
    /// </summary>
    public ScreenScraperResult? DirectResult { get; init; }

    public bool IsDirectResult => DirectResult is not null;
}

public partial class ScrapeReviewDialog : Window
{
    // ── Row visual constants ──────────────────────────────────────────────────

    private static readonly IBrush RowBgDefault   = new SolidColorBrush(Color.Parse("#181826"));
    private static readonly IBrush RowBgSelected  = new SolidColorBrush(Color.Parse("#1C1C36"));
    private static readonly IBrush BorderDefault   = new SolidColorBrush(Color.Parse("#2A2A44"));
    private static readonly IBrush BorderSelected  = new SolidColorBrush(Color.Parse("#7B68EE"));
    private static readonly IBrush DirectBadgeFg   = new SolidColorBrush(Color.Parse("#9FA4FF"));

    // Sentinel ProviderGameId used for the synthetic direct-fallback row.
    // ScreenScraper game IDs are always numeric, so this string is safe as a key.
    private const string DirectFallbackKey = "__direct__";

    // ── Instance state ────────────────────────────────────────────────────────

    private readonly string _devId           = "";
    private readonly string _devPassword     = "";
    private readonly string _username        = "";
    private readonly string _password        = "";
    private readonly string _arkadiaSystemId = "";
    private readonly string _releaseName     = "";
    private readonly bool   _isMame          = false;
    private readonly string _softName        = ScreenScraperClient.DefaultSoftName;

    private ScraperCandidate?                    _selected;
    private readonly Dictionary<string, Border>  _candidateRows  = new(StringComparer.Ordinal);
    // Maps the sentinel key → the full ScreenScraperResult for direct-fallback rows.
    private readonly Dictionary<string, ScreenScraperResult> _directResults =
        new(StringComparer.Ordinal);
    private CancellationTokenSource _searchCts = new();

    // ── Constructors ──────────────────────────────────────────────────────────

    public ScrapeReviewDialog()
    {
        InitializeComponent();
    }

    public ScrapeReviewDialog(
        string devId, string devPassword,
        string username, string password,
        string arkadiaSystemId,
        string platformName,
        string initialQuery,
        string releaseName,
        bool   isMame,
        string softName  = ScreenScraperClient.DefaultSoftName)
    {
        InitializeComponent();

        _devId           = devId;
        _devPassword     = devPassword;
        _username        = username;
        _password        = password;
        _arkadiaSystemId = arkadiaSystemId;
        _releaseName     = releaseName;
        _isMame          = isMame;
        _softName        = softName;

        PlatformLabel.Text = platformName.Length > 0 ? platformName : arkadiaSystemId;
        SearchBox.Text     = initialQuery;

        SearchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await SearchAsync();
        };

        Opened += async (_, _) => await SearchAsync();
    }

    // ── Static helpers (internal for unit tests) ──────────────────────────────

    /// <summary>
    /// Returns the initial search query: the catalog display title when non-empty,
    /// falling back to the raw DAT release name.
    /// </summary>
    internal static string BuildInitialQuery(string catalogTitle, string rawName)
        => catalogTitle.Length > 0 ? catalogTitle : rawName;

    /// <summary>
    /// Returns true when the candidate list is empty or every candidate has no usable title,
    /// indicating that the ROM/DAT-name fallback via <c>QueryAsync</c> should be attempted.
    /// </summary>
    internal static bool ShouldAttemptRomFallback(IReadOnlyList<ScraperCandidate> candidates)
        => candidates.Count == 0 || candidates.All(c => c.Title.Length == 0);

    /// <summary>
    /// Synthesises a <see cref="ScraperCandidate"/> from a <see cref="ScreenScraperResult"/>
    /// returned by the ROM/DAT-name fallback lookup.
    /// </summary>
    internal static ScraperCandidate BuildSyntheticCandidate(ScreenScraperResult result)
        => new()
        {
            ProviderId     = "screenscraper-direct",
            ProviderGameId = DirectFallbackKey,
            Title          = result.Title.Length > 0 ? result.Title : "(Exact ROM match)",
            Year           = result.Year,
            Developer      = result.Developer,
            Publisher      = result.Publisher,
            Description    = result.Description,
        };

    // ── Search ────────────────────────────────────────────────────────────────

    private async Task SearchAsync()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        if (query.Length == 0)
        {
            SetStatus("Enter a search query.");
            return;
        }

        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        SetStatus("Searching…");
        RetryBtn.IsEnabled = false;
        CandidatePanel.Children.Clear();
        _candidateRows.Clear();
        _directResults.Clear();
        ClearPreview();

        try
        {
            // ── Phase 1: text search via jeuRecherche ─────────────────────────
            var candidates = await ScreenScraperClient.SearchCandidatesAsync(
                _devId, _devPassword, _username, _password,
                _arkadiaSystemId, query, ct, _softName);

            if (ct.IsCancellationRequested) return;

            foreach (var c in candidates)
            {
                var row = BuildCandidateRow(c, isDirect: false);
                _candidateRows[c.ProviderGameId] = row;
                CandidatePanel.Children.Add(row);
            }

            // ── Phase 2: ROM/DAT name fallback via jeuInfos romnom ────────────
            if (ShouldAttemptRomFallback(candidates) && _releaseName.Length > 0)
            {
                var fallbackStatus = candidates.Count == 0
                    ? "No text results — trying exact ROM lookup…"
                    : "Weak results — trying exact ROM lookup…";
                SetStatus(fallbackStatus);

                var fallback = await ScreenScraperClient.QueryAsync(
                    _devId, _devPassword, _username, _password,
                    _arkadiaSystemId, _releaseName, _isMame, ct, _softName);

                if (ct.IsCancellationRequested) return;

                if (fallback is not null)
                {
                    var synthetic = BuildSyntheticCandidate(fallback);
                    var row       = BuildCandidateRow(synthetic, isDirect: true);
                    _candidateRows[DirectFallbackKey] = row;
                    _directResults[DirectFallbackKey] = fallback;
                    CandidatePanel.Children.Add(row);
                }
            }

            SetStatus(CandidatePanel.Children.Count == 0 ? "No results." : "");
            AutoSelectSingleCandidate();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search; no status update needed.
        }
        catch (ScreenScraperRateLimitException)
        {
            SetStatus("Rate limited. Please wait before retrying.");
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                RetryBtn.IsEnabled = true;
        }
    }

    // ── Candidate row building ────────────────────────────────────────────────

    private Border BuildCandidateRow(ScraperCandidate candidate, bool isDirect)
    {
        var titleText = new TextBlock
        {
            Text         = candidate.Title.Length > 0 ? candidate.Title : "(Untitled)",
            FontSize     = 12,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = new SolidColorBrush(Color.Parse("#D0D0E8")),
            TextWrapping = TextWrapping.Wrap,
        };

        TextBlock metaText;
        if (isDirect)
        {
            metaText = new TextBlock
            {
                Text       = "Exact ROM match",
                FontSize   = 10,
                Foreground = DirectBadgeFg,
            };
        }
        else
        {
            var parts = new List<string>();
            if (candidate.PlatformName.Length > 0) parts.Add(candidate.PlatformName);
            if (candidate.Year.Length        > 0) parts.Add(candidate.Year);
            if (candidate.Developer.Length   > 0) parts.Add(candidate.Developer);

            metaText = new TextBlock
            {
                Text       = string.Join("  ·  ", parts),
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
            };
        }

        var stack = new StackPanel
        {
            Spacing  = 2,
            Children = { titleText, metaText },
        };

        var border = new Border
        {
            Background      = RowBgDefault,
            BorderBrush     = BorderDefault,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(12, 8),
            Cursor          = new Cursor(StandardCursorType.Hand),
            Tag             = candidate,
            Child           = stack,
        };

        border.PointerPressed += OnCandidatePointerPressed;
        return border;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void OnCandidatePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: ScraperCandidate candidate }) return;
        SelectCandidate(candidate);
    }

    private void SelectCandidate(ScraperCandidate candidate)
    {
        if (_selected is not null && _candidateRows.TryGetValue(_selected.ProviderGameId, out var prev))
        {
            prev.Background  = RowBgDefault;
            prev.BorderBrush = BorderDefault;
        }

        _selected = candidate;

        if (_candidateRows.TryGetValue(candidate.ProviderGameId, out var cur))
        {
            cur.Background  = RowBgSelected;
            cur.BorderBrush = BorderSelected;
        }

        AcceptBtn.IsEnabled = true;
        ShowPreview(candidate);
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    private void ShowPreview(ScraperCandidate candidate)
    {
        PreviewTitle.Text       = candidate.Title.Length       > 0 ? candidate.Title       : "—";
        PreviewPlatform.Text    = candidate.PlatformName.Length > 0 ? candidate.PlatformName : "";
        PreviewYear.Text        = candidate.Year.Length        > 0 ? candidate.Year         : "";
        PreviewDeveloper.Text   = candidate.Developer.Length   > 0 ? candidate.Developer    : "—";
        PreviewPublisher.Text   = candidate.Publisher.Length   > 0 ? candidate.Publisher    : "—";
        PreviewRegion.Text      = candidate.Region.Length      > 0 ? candidate.Region       : "—";
        PreviewDescription.Text = candidate.Description.Length > 0 ? candidate.Description  : "—";

        PreviewPlaceholder.IsVisible = false;
        PreviewScroll.IsVisible      = true;
    }

    private void ClearPreview()
    {
        _selected                    = null;
        AcceptBtn.IsEnabled          = false;
        PreviewPlaceholder.IsVisible = true;
        PreviewScroll.IsVisible      = false;
    }

    // ── Auto-select ───────────────────────────────────────────────────────────

    private void AutoSelectSingleCandidate()
    {
        if (!ShouldAutoSelect(CandidatePanel.Children.Count)) return;
        if (CandidatePanel.Children[0] is Border { Tag: ScraperCandidate candidate })
            SelectCandidate(candidate);
    }

    internal static bool ShouldAutoSelect(int rowCount) => rowCount == 1;

    // ── Status ────────────────────────────────────────────────────────────────

    private void SetStatus(string message)
    {
        StatusLabel.Text      = message;
        StatusLabel.IsVisible = message.Length > 0;
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private async void OnRetry(object? sender, RoutedEventArgs e) => await SearchAsync();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        if (_directResults.TryGetValue(_selected.ProviderGameId, out var directResult))
            Close(new ScrapeReviewResult { DirectResult = directResult });
        else
            Close(new ScrapeReviewResult { Candidate = _selected });
    }
}

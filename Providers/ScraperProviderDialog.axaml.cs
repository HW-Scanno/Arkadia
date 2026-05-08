using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Arkadia;

/// <summary>
/// Describes a single scraper provider and its availability status.
/// </summary>
public sealed record ScraperProviderInfo(
    string ProviderId,
    string DisplayName,
    bool IsAvailable,
    string UnavailableText = "Not configured")
{
    public string StatusText => IsAvailable ? "Available" : UnavailableText;

    public IBrush StatusBrush => IsAvailable
        ? new SolidColorBrush(Color.Parse("#4CAF50"))
        : new SolidColorBrush(Color.Parse("#888899"));

    /// <summary>
    /// Returns true when all four ScreenScraper credentials are non-empty.
    /// Used both by the dialog to determine availability and in unit tests.
    /// </summary>
    public static bool IsScreenScraperConfigured(
        string username, string password, string devId, string devPassword)
        => username.Length > 0 && password.Length > 0 &&
           devId.Length   > 0 && devPassword.Length > 0;
}

public partial class ScraperProviderDialog : Window
{
    private static readonly IBrush RowBgDefault  = new SolidColorBrush(Color.Parse("#181826"));
    private static readonly IBrush RowBgSelected = new SolidColorBrush(Color.Parse("#1C1C36"));
    private static readonly IBrush BorderDefault  = new SolidColorBrush(Color.Parse("#2A2A44"));
    private static readonly IBrush BorderSelected = new SolidColorBrush(Color.Parse("#7B68EE"));

    private readonly Dictionary<string, Border> _rowBorders = new();
    private ScraperProviderInfo? _selected;

    public ScraperProviderDialog()
    {
        InitializeComponent();
    }

    public ScraperProviderDialog(IReadOnlyList<ScraperProviderInfo> providers)
    {
        InitializeComponent();
        foreach (var info in providers)
        {
            var row = BuildRow(info);
            _rowBorders[info.ProviderId] = row;
            ProviderPanel.Children.Add(row);
        }
    }

    private Border BuildRow(ScraperProviderInfo info)
    {
        var nameText = new TextBlock
        {
            Text              = info.DisplayName,
            FontSize          = 13,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = new SolidColorBrush(Color.Parse(info.IsAvailable ? "#D0D0E8" : "#555566")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var statusText = new TextBlock
        {
            Text       = info.StatusText,
            FontSize   = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = info.StatusBrush,
        };

        var statusBadge = new Border
        {
            Background        = new SolidColorBrush(Color.Parse("#0D0D12")),
            CornerRadius      = new CornerRadius(3),
            Padding           = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child             = statusText,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(nameText,    0);
        Grid.SetColumn(statusBadge, 1);
        grid.Children.Add(nameText);
        grid.Children.Add(statusBadge);

        var border = new Border
        {
            Background      = RowBgDefault,
            BorderBrush     = BorderDefault,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(16, 12),
            Cursor          = info.IsAvailable ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Tag             = info,
            Child           = grid,
        };

        if (info.IsAvailable)
            border.PointerPressed += OnRowPointerPressed;

        return border;
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: ScraperProviderInfo info }) return;
        SelectProvider(info);
    }

    private void SelectProvider(ScraperProviderInfo info)
    {
        if (_selected is not null && _rowBorders.TryGetValue(_selected.ProviderId, out var prev))
        {
            prev.Background  = RowBgDefault;
            prev.BorderBrush = BorderDefault;
        }

        _selected = info;

        if (_rowBorders.TryGetValue(info.ProviderId, out var cur))
        {
            cur.Background  = RowBgSelected;
            cur.BorderBrush = BorderSelected;
        }

        ContinueBtn.IsEnabled = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnContinue(object? sender, RoutedEventArgs e) => Close(_selected?.ProviderId);
}

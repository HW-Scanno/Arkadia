using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;

namespace Arkadia;

public partial class VolumeArtifactsDialog : Window
{
    private sealed class GroupRow
    {
        public required string       ReleaseName { get; init; }
        public required List<string> FileNames   { get; init; }
        public required Panel        Container   { get; init; }
        public required Panel        Body        { get; init; }
        public required TextBlock    Arrow       { get; init; }
    }

    private readonly List<GroupRow> _groups = [];

    public VolumeArtifactsDialog() { InitializeComponent(); }

    public VolumeArtifactsDialog(string volumeLabel, List<ArtifactBuildInfo> artifacts)
    {
        InitializeComponent();

        HeaderVolume.Text = $"Volume: {volumeLabel}";
        HeaderCount.Text  = $"Total Artifacts: {artifacts.Count:N0}";

        var grouped = artifacts
            .GroupBy(a => a.ReleaseName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var files     = group.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            var fileNames = files.Select(f => f.FileName).ToList();

            // Arrow indicator
            var arrow = new TextBlock
            {
                Text       = "▶",
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Avalonia.Thickness(0, 0, 7, 0),
            };

            // Header row
            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 0,
                Children    =
                {
                    arrow,
                    new TextBlock
                    {
                        Text       = group.Key,
                        FontSize   = 12,
                        FontWeight = FontWeight.Medium,
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text       = $"  ({files.Count})",
                        FontSize   = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#555566")),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };

            var headerButton = new Button
            {
                Content          = headerRow,
                Background       = Brushes.Transparent,
                BorderThickness  = new Avalonia.Thickness(0),
                Padding          = new Avalonia.Thickness(16, 9, 16, 9),
                HorizontalAlignment       = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor           = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };

            // Body with file rows
            var body = new StackPanel
            {
                Spacing   = 0,
                IsVisible = false,
            };

            foreach (var a in files)
            {
                var sizeLabel = FormatBytes(a.SizeBytes);
                var fileRow   = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Margin            = new Avalonia.Thickness(38, 0, 16, 0),
                };
                fileRow.Children.Add(new TextBlock
                {
                    [Grid.ColumnProperty] = 0,
                    Text         = a.FileName,
                    FontSize     = 11,
                    Foreground   = new SolidColorBrush(Color.Parse("#AAAACC")),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Padding      = new Avalonia.Thickness(0, 3, 8, 3),
                });
                fileRow.Children.Add(new TextBlock
                {
                    [Grid.ColumnProperty] = 1,
                    Text       = sizeLabel,
                    FontSize   = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#555566")),
                    Padding    = new Avalonia.Thickness(0, 3, 0, 3),
                });
                body.Children.Add(fileRow);
            }

            // Separator below group
            var separator = new Border
            {
                Height      = 1,
                Background  = new SolidColorBrush(Color.Parse("#181826")),
            };

            var container = new StackPanel { Spacing = 0 };
            container.Children.Add(headerButton);
            container.Children.Add(body);
            container.Children.Add(separator);

            // Capture for lambda
            var capturedArrow = arrow;
            var capturedBody  = body;
            headerButton.Click += (_, _) => ToggleGroup(capturedArrow, capturedBody);

            var row = new GroupRow
            {
                ReleaseName = group.Key,
                FileNames   = fileNames,
                Container   = container,
                Body        = body,
                Arrow       = arrow,
            };
            _groups.Add(row);
            ArtifactGroupsPanel.Children.Add(container);
        }
    }

    private static void ToggleGroup(TextBlock arrow, Panel body)
    {
        body.IsVisible = !body.IsVisible;
        arrow.Text     = body.IsVisible ? "▼" : "▶";
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var g in _groups)
        {
            if (!g.Container.IsVisible) continue;
            g.Body.IsVisible = expanded;
            g.Arrow.Text     = expanded ? "▼" : "▶";
        }
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e)  => SetAllExpanded(true);
    private void OnCollapseAll(object? sender, RoutedEventArgs e) => SetAllExpanded(false);

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        if (query.Length == 0)
        {
            // Restore all groups; leave expanded state as-is but ensure groups are visible
            foreach (var g in _groups)
                g.Container.IsVisible = true;
            return;
        }

        var comp = StringComparison.OrdinalIgnoreCase;
        foreach (var g in _groups)
        {
            bool matches = g.ReleaseName.Contains(query, comp)
                        || g.FileNames.Any(f => f.Contains(query, comp));
            g.Container.IsVisible = matches;
            if (matches && !g.Body.IsVisible)
            {
                g.Body.IsVisible = true;
                g.Arrow.Text     = "▼";
            }
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long b)
    {
        if (b <= 0)                        return "0 B";
        if (b < 1024L)                     return $"{b} B";
        if (b < 1024L * 1024)              return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)       return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}

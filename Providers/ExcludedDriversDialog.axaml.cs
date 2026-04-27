using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Arkadia;

public partial class ExcludedDriversDialog : Window
{
    private readonly List<(string Sf, int Count)> _items;

    public List<string> ToRestore { get; } = [];

    public ExcludedDriversDialog()
    {
        _items = [];
        InitializeComponent();
        Rebuild();
    }

    public ExcludedDriversDialog(List<(string Sf, int Count)> excluded)
    {
        _items = [.. excluded];
        InitializeComponent();
        Rebuild();
    }

    private void Rebuild()
    {
        ExcludedPanel.Children.Clear();

        foreach (var (sf, count) in _items)
        {
            var sfLabel = new TextBlock
            {
                Text = sf,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            var countLabel = new TextBlock
            {
                Text = $"{count:N0}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(12, 0, 16, 0),
            };
            var restoreBtn = new Button
            {
                Content = "Restore",
                FontSize = 11,
                Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
                Foreground = new SolidColorBrush(Color.Parse("#4CAF50")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2A2A3C")),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(3),
                Padding = new Avalonia.Thickness(10, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var captured = sf;
            restoreBtn.Click += (_, _) =>
            {
                ToRestore.Add(captured);
                _items.RemoveAll(x => x.Sf == captured);
                Rebuild();
                if (_items.Count == 0) Close();
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            row.Children.Add(sfLabel);
            Grid.SetColumn(countLabel,  1);
            row.Children.Add(countLabel);
            Grid.SetColumn(restoreBtn,  2);
            row.Children.Add(restoreBtn);

            ExcludedPanel.Children.Add(new Border
            {
                Child           = row,
                Padding         = new Avalonia.Thickness(14, 7, 14, 7),
                BorderBrush     = new SolidColorBrush(Color.Parse("#181826")),
                BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            });
        }

        CountLabel.Text          = $"{_items.Count} driver{(_items.Count == 1 ? "" : "s")} excluded";
        RestoreAllButton.IsEnabled = _items.Count > 0;
    }

    private void OnRestoreAll(object? sender, RoutedEventArgs e)
    {
        ToRestore.AddRange(_items.Select(x => x.Sf));
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

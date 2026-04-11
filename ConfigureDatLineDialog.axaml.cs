using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.Systems;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Arkadia;

public partial class ConfigureDatLineDialog : Window
{
    private readonly DatLineInfo    _d;
    private readonly CatalogService _catalog;
    private readonly string         _dataDir;

    // Controls populated by PopulateContent — read back in OnSave
    private ComboBox                         _stratBox      = null!;
    private ComboBox                         _folderBox     = null!;
    private Dictionary<string, ComboBox>     _extActionBoxes = new(StringComparer.OrdinalIgnoreCase);
    private List<TransformRecord>            _fileXforms    = [];
    private List<TransformRecord>            _folderXforms  = [];
    private readonly string[]                _stratValues   = ["none", "file_extension", "release_folder"];

    public ConfigureDatLineDialog() : this(
        new DatLineInfo("", 0, ""), new CatalogService(""), "") { }

    public ConfigureDatLineDialog(DatLineInfo d, CatalogService catalog, string dataDir)
    {
        InitializeComponent();
        _d       = d;
        _catalog = catalog;
        _dataDir = dataDir;

        Title              = $"Configure: {d.Name}";
        DialogTitle.Text   = d.Name;
        DialogSubtitle.Text = $"{d.Releases:N0} releases";

        PopulateContent();
    }

    // ── Content builder ───────────────────────────────────────────────────────

    private void PopulateContent()
    {
        var allTransforms = _catalog.LoadTransforms();
        _fileXforms   = allTransforms.Where(t => t.IsFileStrategy).ToList();
        _folderXforms = allTransforms.Where(t => t.IsFolderStrategy).ToList();

        var existingMappings = _d.CatalogId is not null
            ? _catalog.LoadExtensionMappings(_d.CatalogId)
            : new List<ExtensionTransformMapping>();
        var mappingLookup = existingMappings.ToDictionary(m => m.FileExtension, StringComparer.OrdinalIgnoreCase);

        // Compute extension counts from the data store
        var extCounts = new List<(string Ext, int Count)>();
        if (_d.DataStorePath.Length > 0)
        {
            var absPath = Path.Combine(_dataDir, _d.DataStorePath);
            if (File.Exists(absPath))
            {
                var extDict  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var allFiles = new DatLineStore(absPath).LoadAllReleaseFiles();
                foreach (var files in allFiles.Values)
                    foreach (var f in files)
                    {
                        var ext = Path.GetExtension(f.RomName).ToLowerInvariant();
                        if (ext.Length == 0) ext = "(no ext)";
                        extDict[ext] = extDict.GetValueOrDefault(ext) + 1;
                    }
                extCounts = extDict.OrderByDescending(kv => kv.Value)
                                   .ThenBy(kv => kv.Key)
                                   .Select(kv => (kv.Key, kv.Value))
                                   .ToList();
            }
        }

        var dim     = new SolidColorBrush(Color.Parse("#555566"));
        var text    = new SolidColorBrush(Color.Parse("#CCCCDD"));
        var warn    = new SolidColorBrush(Color.Parse("#E8A000"));

        // ── Section 1: Strategy type ──────────────────────────────────────────
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = "STRATEGY TYPE",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });

        var stratLabels = new[] { "None", "Per file extension", "Per release folder" };
        _stratBox = new ComboBox
        {
            ItemsSource     = stratLabels,
            SelectedIndex   = Math.Max(0, Array.IndexOf(_stratValues, _d.TransformStrategyType)),
            Background      = new SolidColorBrush(Color.Parse("#0D0D1A")),
            Foreground      = text,
            BorderBrush     = new SolidColorBrush(Color.Parse("#2A2A3C")),
            BorderThickness = new Avalonia.Thickness(1),
            FontSize        = 12,
            Margin          = new Avalonia.Thickness(0, 0, 0, 16),
        };
        ContentPanel.Children.Add(_stratBox);

        // ── Section 2: File extension mapping ─────────────────────────────────
        var fileExtPanel = new StackPanel
        {
            Spacing   = 0,
            IsVisible = _d.TransformStrategyType == "file_extension",
        };
        ContentPanel.Children.Add(fileExtPanel);

        var actionLabels = new List<string> { "Discard" };
        actionLabels.AddRange(_fileXforms.Select(t => t.Name));

        void BuildExtensionTable()
        {
            fileExtPanel.Children.Clear();
            _extActionBoxes.Clear();

            fileExtPanel.Children.Add(new TextBlock
            {
                Text       = "EXTENSION MAPPING",
                FontSize   = 9,
                FontWeight = FontWeight.SemiBold,
                Foreground = dim,
                Margin     = new Avalonia.Thickness(0, 0, 0, 6),
            });

            if (extCounts.Count == 0)
            {
                fileExtPanel.Children.Add(new TextBlock
                {
                    Text         = "No file extensions detected. Import the DAT first.",
                    FontSize     = 11,
                    Foreground   = dim,
                    Margin       = new Avalonia.Thickness(0, 0, 0, 8),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                });
                return;
            }

            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,52,200"),
                Margin            = new Avalonia.Thickness(0, 0, 0, 4),
            };
            var hExt   = new TextBlock { Text = "Extension", FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = dim };
            var hCount = new TextBlock { Text = "Count",     FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = dim, TextAlignment = Avalonia.Media.TextAlignment.Right };
            var hAct   = new TextBlock { Text = "Action",    FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = dim, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
            Grid.SetColumn(hExt,   0);
            Grid.SetColumn(hCount, 1);
            Grid.SetColumn(hAct,   2);
            header.Children.Add(hExt);
            header.Children.Add(hCount);
            header.Children.Add(hAct);
            fileExtPanel.Children.Add(header);

            foreach (var (ext, count) in extCounts)
            {
                var actionBox = new ComboBox
                {
                    ItemsSource     = actionLabels,
                    SelectedIndex   = 0,
                    Background      = new SolidColorBrush(Color.Parse("#0D0D1A")),
                    Foreground      = text,
                    BorderBrush     = new SolidColorBrush(Color.Parse("#2A2A3C")),
                    BorderThickness = new Avalonia.Thickness(1),
                    FontSize        = 11,
                    Margin          = new Avalonia.Thickness(8, 0, 0, 0),
                };

                if (mappingLookup.TryGetValue(ext, out var mapping))
                {
                    if (!mapping.IsDiscard && mapping.TransformId.Length > 0)
                    {
                        var xIdx = _fileXforms.FindIndex(t => t.Id == mapping.TransformId);
                        if (xIdx >= 0) actionBox.SelectedIndex = xIdx + 1;
                    }
                }

                _extActionBoxes[ext] = actionBox;

                var extRow = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,52,200"),
                    Margin            = new Avalonia.Thickness(0, 2, 0, 0),
                };
                var extLabel   = new TextBlock { Text = ext,                  FontSize = 11, Foreground = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                var countLabel = new TextBlock { Text = count.ToString("N0"), FontSize = 11, Foreground = dim,  TextAlignment = Avalonia.Media.TextAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Grid.SetColumn(extLabel,   0);
                Grid.SetColumn(countLabel, 1);
                Grid.SetColumn(actionBox,  2);
                extRow.Children.Add(extLabel);
                extRow.Children.Add(countLabel);
                extRow.Children.Add(actionBox);
                fileExtPanel.Children.Add(extRow);
            }
        }

        // Section 3: Smart suggestion for >10 extensions
        if (extCounts.Count > 10)
        {
            var guidancePanel = new StackPanel { Spacing = 6 };
            guidancePanel.Children.Add(new TextBlock
            {
                Text         = "Many different file extensions were detected for this DAT line.",
                FontSize     = 11,
                Foreground   = warn,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            guidancePanel.Children.Add(new TextBlock
            {
                Text         = "This usually indicates that 'Per release folder' is more appropriate.",
                FontSize     = 11,
                Foreground   = text,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            guidancePanel.Children.Add(new TextBlock
            {
                Text       = $"Total unique extensions: {extCounts.Count}",
                FontSize   = 11,
                Foreground = dim,
                Margin     = new Avalonia.Thickness(0, 4, 0, 0),
            });
            guidancePanel.Children.Add(new TextBlock
            {
                Text       = "Top 5 by count:",
                FontSize   = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = dim,
            });
            foreach (var (ext, cnt) in extCounts.Take(5))
                guidancePanel.Children.Add(new TextBlock
                {
                    Text       = $"  {ext}  ·  {cnt:N0}",
                    FontSize   = 11,
                    Foreground = text,
                    FontFamily = new Avalonia.Media.FontFamily("Consolas, monospace"),
                });

            var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Avalonia.Thickness(0, 8, 0, 0) };

            var useFolderBtn  = new Button { Content = "Use Per release folder",        FontSize = 11 };
            var tableWrapper  = new StackPanel { IsVisible = false, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
            var showAnywayBtn = new Button { Content = "Show extension mapping anyway", FontSize = 11 };

            useFolderBtn.Click  += (_, _) => _stratBox.SelectedIndex = 2;
            showAnywayBtn.Click += (_, _) =>
            {
                guidancePanel.IsVisible   = false;
                showAnywayBtn.IsVisible   = false;
                BuildExtensionTable();
                tableWrapper.Children.Add(fileExtPanel);
                tableWrapper.IsVisible    = true;
            };

            btnRow.Children.Add(useFolderBtn);
            btnRow.Children.Add(showAnywayBtn);
            guidancePanel.Children.Add(btnRow);

            fileExtPanel.Children.Add(guidancePanel);
            fileExtPanel.Children.Add(tableWrapper);
        }
        else
        {
            BuildExtensionTable();
        }

        // ── Release folder sub-panel ───────────────────────────────────────────
        var folderPanel = new StackPanel
        {
            Spacing   = 0,
            IsVisible = _d.TransformStrategyType == "release_folder",
            Margin    = new Avalonia.Thickness(0, 0, 0, 0),
        };
        ContentPanel.Children.Add(folderPanel);

        folderPanel.Children.Add(new TextBlock
        {
            Text       = "RELEASE FOLDER TRANSFORM",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });

        var folderLabels = _folderXforms.Select(t => t.Name).ToList();
        _folderBox = new ComboBox
        {
            ItemsSource     = folderLabels.Count > 0
                                ? (System.Collections.IEnumerable)folderLabels
                                : new[] { "(no folder transforms defined)" },
            Background      = new SolidColorBrush(Color.Parse("#0D0D1A")),
            Foreground      = text,
            BorderBrush     = new SolidColorBrush(Color.Parse("#2A2A3C")),
            BorderThickness = new Avalonia.Thickness(1),
            FontSize        = 12,
        };
        if (folderLabels.Count > 0)
        {
            var selIdx = _folderXforms.FindIndex(t => t.Id == _d.FolderTransformId);
            _folderBox.SelectedIndex = selIdx >= 0 ? selIdx : 0;
        }
        folderPanel.Children.Add(_folderBox);

        // Strategy change → show/hide sub-panels
        _stratBox.SelectionChanged += (_, _) =>
        {
            var idx = _stratBox.SelectedIndex;
            var val = idx >= 0 && idx < _stratValues.Length ? _stratValues[idx] : "none";
            fileExtPanel.IsVisible = val == "file_extension";
            folderPanel.IsVisible  = val == "release_folder";
        };
    }

    // ── Footer handlers ───────────────────────────────────────────────────────

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_d.CatalogId is null) { Close(false); return; }

        var idx      = _stratBox.SelectedIndex;
        var stratVal = idx >= 0 && idx < _stratValues.Length ? _stratValues[idx] : "none";

        string? folderTransformId = null;
        if (stratVal == "release_folder" && _folderXforms.Count > 0 && _folderBox.SelectedIndex >= 0)
            folderTransformId = _folderXforms[_folderBox.SelectedIndex].Id;

        _catalog.SaveDatLineTransformStrategy(_d.CatalogId, stratVal, folderTransformId);

        if (stratVal == "file_extension" && _extActionBoxes.Count > 0)
        {
            var mappings = _extActionBoxes.Select(kv =>
            {
                var selIdx    = kv.Value.SelectedIndex;
                var isDiscard = selIdx <= 0;
                var xformId   = isDiscard ? "" : _fileXforms[selIdx - 1].Id;
                return new ExtensionTransformMapping
                {
                    DatLineId     = _d.CatalogId,
                    FileExtension = kv.Key,
                    IsDiscard     = isDiscard,
                    TransformId   = xformId,
                };
            }).ToList();
            _catalog.SaveExtensionMappings(_d.CatalogId, mappings);
        }

        Close(true);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}

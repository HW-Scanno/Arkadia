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
    private ComboBox                         _fhBox         = null!;
    private ComboBox                         _stratBox      = null!;
    private ComboBox                         _folderBox     = null!;
    private Dictionary<string, ComboBox>     _extActionBoxes = new(StringComparer.OrdinalIgnoreCase);
    private List<TransformRecord>            _fileXforms    = [];
    private List<TransformRecord>            _folderXforms  = [];
    private readonly string[]                _fileHandlingValues = ["archives_pre_extraction", "all_files"];
    private readonly string[]                _stratValues   = ["none", "file_extension", "release_folder", "release_shape"];

    // Live-feedback panels (built in PopulateContent, updated by RefreshLivePanels)
    private StackPanel _modelInfoPanel  = null!;
    private StackPanel _happenPanel     = null!;
    private StackPanel _validationPanel = null!;
    private bool       _isConfigInvalid = false;

    // "single" | "multi" | "mixed" | "unknown"
    private string                      _releaseShape   = "unknown";
    private ReleaseShapeAnalysisResult? _shapeAnalysis;

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

                if (allFiles.Count > 0)
                {
                    int singles = allFiles.Values.Count(f => f.Count == 1);
                    int multis  = allFiles.Values.Count(f => f.Count > 1);
                    _releaseShape = (singles, multis) switch
                    {
                        ( > 0, 0)   => "single",
                        (0,   > 0)  => "multi",
                        ( > 0, > 0) => "mixed",
                        _           => "unknown",
                    };
                    _shapeAnalysis = ReleaseShapeTransformPlanner.AnalyzeDat(allFiles);
                }
            }
        }

        var dim     = new SolidColorBrush(Color.Parse("#555566"));
        var text    = new SolidColorBrush(Color.Parse("#CCCCDD"));
        var warn    = new SolidColorBrush(Color.Parse("#E8A000"));

        // ── Section 0: File Handling ──────────────────────────────────────────
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = "FILE HANDLING",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });

        var fhLabels = new[] { "Archives Pre-Extraction", "All Files" };
        _fhBox = new ComboBox
        {
            ItemsSource     = fhLabels,
            SelectedIndex   = _d.FileHandling == "all_files" ? 1 : 0,
            Background      = new SolidColorBrush(Color.Parse("#0D0D1A")),
            Foreground      = text,
            BorderBrush     = new SolidColorBrush(Color.Parse("#2A2A3C")),
            BorderThickness = new Avalonia.Thickness(1),
            FontSize        = 12,
            Margin          = new Avalonia.Thickness(0, 0, 0, 6),
        };
        ContentPanel.Children.Add(_fhBox);

        var fhNote = new TextBlock
        {
            FontSize     = 11,
            Foreground   = dim,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 0, 0, 4),
        };

        void UpdateFhNote()
        {
            fhNote.Text = _fhBox.SelectedIndex == 1
                ? "All files in the incoming folder are matched directly — no archive extraction is performed."
                : "Archives (.zip, .7z, .rar) are extracted before matching; the originals are deleted on success.";
        }

        UpdateFhNote();
        _fhBox.SelectionChanged += (_, _) => UpdateFhNote();
        ContentPanel.Children.Add(fhNote);

        ContentPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1A1A2C")),
            Margin     = new Avalonia.Thickness(0, 10, 0, 16),
        });

        // ── Section 1: Strategy type ──────────────────────────────────────────
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = "STRATEGY TYPE",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });

        var stratLabels = new[] { "None", "Per file extension", "Per release folder", "Per release shape" };
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

        // Strategy change → show/hide sub-panels + refresh live sections
        _stratBox.SelectionChanged += (_, _) =>
        {
            var idx = _stratBox.SelectedIndex;
            var val = idx >= 0 && idx < _stratValues.Length ? _stratValues[idx] : "none";
            fileExtPanel.IsVisible = val == "file_extension";
            folderPanel.IsVisible  = val == "release_folder";
            RefreshLivePanels();
        };


        _folderBox.SelectionChanged += (_, _) => RefreshLivePanels();

        // ── Section 4: Release Shape (static) ────────────────────────────────
        ContentPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1A1A2C")),
            Margin     = new Avalonia.Thickness(0, 16, 0, 16),
        });
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = "RELEASE SHAPE",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });
        var shapeText = _releaseShape switch
        {
            "single"  => "Single-file \u2014 all releases contain exactly one file",
            "multi"   => "Multi-file \u2014 all releases contain multiple files",
            "mixed"   => "Mixed \u2014 some releases have one file, others have multiple",
            _         => "Unknown \u2014 no data store found for this DAT line",
        };
        ContentPanel.Children.Add(new TextBlock
        {
            Text         = shapeText,
            FontSize     = 11,
            Foreground   = text,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 0, 0, 4),
        });
        string? recText = _releaseShape switch
        {
            "single" when _shapeAnalysis is { IsValid: true }
                     => "This DAT contains only single-file releases. A file-oriented model or Per release shape is recommended.",
            "single" => "This DAT contains only single-file releases. A file-oriented model is recommended.",
            "multi"  when _shapeAnalysis is { IsValid: true }
                     => "This DAT contains multi-file releases compatible with Per release shape dispatch.",
            "multi"  => "This DAT contains multi-file releases. A folder-oriented model is required.",
            "mixed"  when _shapeAnalysis is { IsValid: true }
                     => "This DAT contains mixed ISO / CUE+BIN releases. Use Per release shape for CHD conversion.",
            "mixed"  => "This DAT contains both single-file and multi-file releases. A folder-oriented model is required.",
            _        => null,
        };
        if (recText is not null)
            ContentPanel.Children.Add(new TextBlock
            {
                Text         = recText,
                FontSize     = 11,
                Foreground   = _releaseShape == "single" ? text : warn,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin       = new Avalonia.Thickness(0, 0, 0, 4),
            });

        // ── Section 5: Selected Model (live) ─────────────────────────────────
        ContentPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1A1A2C")),
            Margin     = new Avalonia.Thickness(0, 12, 0, 16),
        });
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = "SELECTED MODEL",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });
        _modelInfoPanel = new StackPanel { Spacing = 2 };
        ContentPanel.Children.Add(_modelInfoPanel);

        // ── Section 6: What Will Happen (live) ───────────────────────────────
        ContentPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1A1A2C")),
            Margin     = new Avalonia.Thickness(0, 12, 0, 16),
        });
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = "WHAT WILL HAPPEN",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });
        _happenPanel = new StackPanel { Spacing = 4 };
        ContentPanel.Children.Add(_happenPanel);

        // ── Section 7: Validation (live) ─────────────────────────────────────
        ContentPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1A1A2C")),
            Margin     = new Avalonia.Thickness(0, 12, 0, 16),
        });
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = "VALIDATION",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });
        _validationPanel = new StackPanel { Spacing = 4 };
        ContentPanel.Children.Add(_validationPanel);

        // Auto-suggest: preselect best strategy when DAT is unconfigured.
        // Must run AFTER all live panels are built so RefreshLivePanels() (triggered by
        // SelectionChanged) finds non-null panel references.
        if (_d.TransformStrategyType == "none")
        {
            if (_shapeAnalysis is { IsValid: true })
                _stratBox.SelectedIndex = 3; // Per release shape
            else if (_releaseShape is "multi" or "mixed")
                _stratBox.SelectedIndex = 2; // Per release folder
        }

        // Initial render of live panels
        RefreshLivePanels();
    }

    // ── Live panel refresh ────────────────────────────────────────────────────

    private void RefreshLivePanels()
    {
        if (_modelInfoPanel is null || _happenPanel is null || _validationPanel is null) return;

        var textBrush  = new SolidColorBrush(Color.Parse("#CCCCDD"));
        var dimBrush   = new SolidColorBrush(Color.Parse("#555566"));
        var warnBrush  = new SolidColorBrush(Color.Parse("#E8A000"));
        var validBrush = new SolidColorBrush(Color.Parse("#44BB66"));
        var errorBrush = new SolidColorBrush(Color.Parse("#CC4444"));

        var stratIdx = _stratBox.SelectedIndex;
        var stratVal = stratIdx >= 0 && stratIdx < _stratValues.Length ? _stratValues[stratIdx] : "none";

        TransformRecord? folderXform = null;
        if (stratVal == "release_folder" && _folderXforms.Count > 0 && _folderBox.SelectedIndex >= 0)
            folderXform = _folderXforms[_folderBox.SelectedIndex];

        // ── Selected Model ────────────────────────────────────────────────────
        _modelInfoPanel.Children.Clear();

        var stratDisplay = stratIdx switch
        {
            1 => "Per file extension",
            2 => "Per release folder",
            3 => "Per release shape",
            _ => "None"
        };
        AddModelRow(_modelInfoPanel, "Strategy", stratDisplay, textBrush, dimBrush);

        if (stratVal == "release_folder")
        {
            if (folderXform != null)
            {
                AddModelRow(_modelInfoPanel, "Transform", folderXform.Name,                                                  textBrush, dimBrush);
                AddModelRow(_modelInfoPanel, "Processor",  folderXform.IsFileOriented ? "File-oriented" : "Folder-oriented", textBrush, dimBrush);
                AddModelRow(_modelInfoPanel, "Output",     folderXform.OutputIsFile    ? "File"          : "Folder",          textBrush, dimBrush);
            }
            else if (_folderXforms.Count == 0)
            {
                _modelInfoPanel.Children.Add(new TextBlock
                {
                    Text = "No folder-oriented transforms are defined.", FontSize = 11, Foreground = errorBrush,
                });
            }
        }
        else if (stratVal == "release_shape")
        {
            AddModelRow(_modelInfoPanel, "Processor", "Release-shape dispatch", textBrush, dimBrush);
            AddModelRow(_modelInfoPanel, "Output",    "File",                   textBrush, dimBrush);
            if (_shapeAnalysis is not null)
            {
                if (_shapeAnalysis.SingleIsoCount > 0)
                    AddModelRow(_modelInfoPanel, ".iso releases",     $"{_shapeAnalysis.SingleIsoCount:N0} → CHD DVD Compression", textBrush, dimBrush);
                if (_shapeAnalysis.CueBinCount > 0)
                    AddModelRow(_modelInfoPanel, ".cue+.bin releases", $"{_shapeAnalysis.CueBinCount:N0} → CHD CD Compression",   textBrush, dimBrush);
            }
        }

        // ── What Will Happen ──────────────────────────────────────────────────
        _happenPanel.Children.Clear();

        string happenText;
        IBrush happenBrush = textBrush;

        if (stratVal == "none")
        {
            happenText  = "Releases will be ingested without any transform. Source files are kept as-is in the source folder.";
            happenBrush = warnBrush;
        }
        else if (stratVal == "file_extension")
        {
            if (_releaseShape is "multi" or "mixed")
            {
                happenText  = "Per-extension mapping treats each file individually and cannot process a release as a unit. This configuration is invalid for this DAT.";
                happenBrush = errorBrush;
            }
            else
            {
                happenText = "Each file will be matched to a transform by its extension. Files mapped to 'Discard' will be skipped.";
            }
        }
        else if (stratVal == "release_shape")
        {
            if (_shapeAnalysis is { IsValid: true })
            {
                happenText =
                    "Each release will be inspected as a unit.\n" +
                    "Single .iso releases will use CHD DVD Compression.\n" +
                    ".cue + .bin releases will use CHD CD Compression.\n" +
                    ".bin files are required dependencies, not discarded.";
            }
            else if (_shapeAnalysis is { UnsupportedCount: > 0 })
            {
                happenText  = $"{_shapeAnalysis.UnsupportedCount} release(s) have unsupported file combinations for this strategy. This configuration is invalid.";
                happenBrush = errorBrush;
            }
            else
            {
                happenText  = "No releases with supported shapes found. Ensure the DAT has .iso or .cue+.bin releases.";
                happenBrush = errorBrush;
            }
        }
        else // release_folder
        {
            if (folderXform == null)
            {
                happenText  = "No folder transform is selected. Save is blocked until a valid transform is chosen.";
                happenBrush = errorBrush;
            }
            else if (folderXform.IsFileOriented)
            {
                happenText  = $"\"{folderXform.Name}\" is a file-oriented transform. The 'Per release folder' strategy requires a folder-oriented transform. This configuration is invalid.";
                happenBrush = errorBrush;
            }
            else
            {
                happenText = $"Each release folder will be processed as a unit using \"{folderXform.Name}\". The entire folder will be passed to the transform command.";
                if (_shapeAnalysis is { IsValid: true })
                    happenBrush = warnBrush;
            }
        }

        _happenPanel.Children.Add(new TextBlock
        {
            Text         = happenText,
            FontSize     = 11,
            Foreground   = happenBrush,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        // ── Validation ────────────────────────────────────────────────────────
        _validationPanel.Children.Clear();
        _isConfigInvalid = false;

        string validStatus;
        string validDetail;
        IBrush validColor;

        if (stratVal == "release_folder" && (folderXform == null || folderXform.IsFileOriented))
        {
            _isConfigInvalid = true;
            validStatus = "INVALID";
            validDetail = folderXform == null
                ? "No folder-oriented transform is selected. Choose a folder-oriented transform to proceed."
                : $"\"{folderXform.Name}\" is file-oriented. The release folder strategy requires a folder-oriented transform.";
            validColor = errorBrush;
        }
        else if (stratVal == "file_extension" && _releaseShape is "multi" or "mixed")
        {
            _isConfigInvalid = true;
            validStatus = "INVALID";
            validDetail = _releaseShape == "multi"
                ? "This DAT contains multi-file releases. A file-oriented model cannot handle releases correctly. Switch to a folder-oriented model."
                : "This DAT contains mixed releases. A file-oriented model cannot handle all releases correctly. Switch to a folder-oriented model.";
            validColor = errorBrush;
        }
        else if (stratVal == "release_shape" && (_shapeAnalysis is null || !_shapeAnalysis.IsValid))
        {
            _isConfigInvalid = true;
            validStatus = "INVALID";
            var unsupportedCount = _shapeAnalysis?.UnsupportedCount ?? 0;
            var examples = _shapeAnalysis?.UnsupportedExamples ?? Array.Empty<string>();
            validDetail = unsupportedCount > 0
                ? $"{unsupportedCount} release(s) have unsupported file combinations (not single .iso or .cue+.bin). " +
                  (examples.Count > 0 ? $"Examples: {string.Join(", ", examples.Take(3))}" : "")
                : "No releases with supported shapes (.iso or .cue+.bin) found.";
            validColor = errorBrush;
        }
        else if (stratVal == "none")
        {
            validStatus = "WARNING";
            validDetail = "No transform is configured. Releases will be ingested without any processing.";
            validColor  = warnBrush;
        }
        else if (stratVal == "release_folder" && folderXform != null && _releaseShape == "single")
        {
            validStatus = "WARNING";
            validDetail = "This DAT contains only single-file releases. A folder-oriented model is heavier than necessary but is safe to use.";
            validColor  = warnBrush;
        }
        else if (stratVal == "release_folder" && folderXform != null && _shapeAnalysis is { IsValid: true })
        {
            validStatus = "WARNING";
            validDetail = "Folder compression is valid, but it will not create CHD artifacts. Use Per release shape for ISO/CUE-BIN to CHD conversion.";
            validColor  = warnBrush;
        }
        else
        {
            validStatus = "VALID";
            validDetail = "This configuration is ready for ingestion.";
            validColor  = validBrush;
        }

        _validationPanel.Children.Add(new TextBlock
        {
            Text       = validStatus,
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = validColor,
            Margin     = new Avalonia.Thickness(0, 0, 0, 4),
        });
        _validationPanel.Children.Add(new TextBlock
        {
            Text         = validDetail,
            FontSize     = 11,
            Foreground   = textBrush,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        SaveBtn.IsEnabled = !_isConfigInvalid;
    }

    private static void AddModelRow(StackPanel panel, string label, string value, IBrush valueBrush, IBrush labelBrush)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("110,*"),
            Margin            = new Avalonia.Thickness(0, 1, 0, 1),
        };
        var lbl = new TextBlock { Text = label, FontSize = 11, Foreground = labelBrush, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var val = new TextBlock { Text = value, FontSize = 11, Foreground = valueBrush, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(val, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(val);
        panel.Children.Add(grid);
    }

    // ── Footer handlers ───────────────────────────────────────────────────────

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_isConfigInvalid || _d.CatalogId is null) { Close(false); return; }

        var idx      = _stratBox.SelectedIndex;
        var stratVal = idx >= 0 && idx < _stratValues.Length ? _stratValues[idx] : "none";

        string? folderTransformId = null;
        if (stratVal == "release_folder" && _folderXforms.Count > 0 && _folderBox.SelectedIndex >= 0)
            folderTransformId = _folderXforms[_folderBox.SelectedIndex].Id;

        var fhVal = _fhBox.SelectedIndex == 1 ? "all_files" : "archives_pre_extraction";
        _catalog.SaveDatLineFileHandling(_d.CatalogId, fhVal);
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

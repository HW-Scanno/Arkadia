using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Arkadia.Data;
using Arkadia.Ingestion;
using Arkadia.Library;
using Arkadia.Pending;
using Arkadia.Disks;
using Arkadia.Staging;
using Arkadia.Systems;
using Arkadia.Volumes;
using Arkadia.Themes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Arkadia;

public partial class MainWindow : Window
{
    private const double SidebarWidth = 220;

    private readonly List<Button> _navButtons = [];
    private Button? _activeButton;

    // ── View registry — maps nav button → content panel ──────────────────────
    private Dictionary<Button, Control> _views = [];

    private static readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "data");

    private readonly CatalogService _catalog = new(_dataDir);
    private bool _showDebugArtifactInfo;

    public MainWindow()
    {
        InitializeComponent();
        _showDebugArtifactInfo = _catalog.GetBoolSetting("show_debug_artifact_info");

        _navButtons.AddRange([
            NavDashboard, NavSystems, NavPending, NavStaging, NavLibrary, NavVolumes, NavDisks, NavOperations,
            NavAnalytics, NavLogs, NavSettings,
        ]);

        _views = new()
        {
            [NavDashboard]  = ViewDashboard,
            [NavSystems]    = ViewSystems,
            [NavPending]    = ViewPending,
            [NavStaging]    = ViewStaging,
            [NavLibrary]    = ViewLibrary,
            [NavDisks]      = ViewDisks,
            [NavVolumes]    = ViewVolumes,
            [NavOperations] = ViewOperations,
            [NavAnalytics]  = ViewAnalytics,
            [NavLogs]       = ViewLogs,
            [NavSettings]   = ViewSettings,
        };

        InitSystems();
        InitPending();
        InitStaging();
        InitLibrary();
        InitDisks();
        InitVolumes();
        InitSettings();
        InitOperations();
        InitAnalytics();
        InitLogs();
        ResolveFlagImages();
        SetActive(NavDashboard);
        InitDashboard();
        InitLogo();
    }

    // ── Systems ──────────────────────────────────────────────────────────────

    private List<SystemPlatform> _systemsPlatforms = [];
    // id → display name, rebuilt on every RefreshSystems
    private Dictionary<string, string> _hardwareTypeMap   = [];
    private Dictionary<string, string> _strategyNameMap   = [];

    private string?        _systemsThemeDir;
    private SystemPlatform? _selectedPlatform;   // kept for OnEditPlatform compat
    private string?        _selectedPlatformId;
    private DatLineInfo?   _selectedDatLine;

    private void InitSystems()
    {
        try
        {
            var settings = ThemeSettings.Load(
                Path.Combine(AppContext.BaseDirectory, "appconfig.json"));
            var manager  = new ThemeManager(
                Path.Combine(AppContext.BaseDirectory, "themes", "visual"),
                settings.ActiveVisualThemeId);
            manager.Scan();
            _systemsThemeDir = manager.ResolveActiveTheme()?.ThemeDirectory;
        }
        catch { /* theme dir stays null — images simply won't load */ }

        RefreshSystems();
        if (_systemsPlatforms.Count > 0)
            SelectPlatform(_systemsPlatforms[0].Id);
    }

    private void RefreshSystemsKeepSelection(string? platformId)
    {
        var keepDatId      = _selectedDatLine?.CatalogId;
        var targetPlatform = platformId ?? _selectedPlatformId;
        RefreshSystems();

        if (targetPlatform is not null)
        {
            var p = _systemsPlatforms.FirstOrDefault(x => x.Id == targetPlatform);
            if (p is not null)
            {
                SelectPlatform(p.Id);
                // Try to restore DAT selection
                if (keepDatId is not null)
                {
                    var d = BuildDatLineInfos(p.Id).FirstOrDefault(x => x.CatalogId == keepDatId);
                    if (d is not null) SelectDatLine(d, p.Id);
                }
                return;
            }
        }
        if (_systemsPlatforms.Count > 0)
            SelectPlatform(_systemsPlatforms[0].Id);
    }

    private void RefreshSystems()
    {
        _hardwareTypeMap = _catalog.LoadHardwareTypes()
            .ToDictionary(h => h.Id, h => h.Name);
        _strategyNameMap = _catalog.LoadStorageStrategies()
            .ToDictionary(s => s.Id, s => s.Name);

        var allDatLines = _catalog.LoadDatLines();

        _systemsPlatforms = _catalog.LoadPlatforms()
            .Select(platform =>
            {
                var platformDatLines = allDatLines.Where(dl => dl.PlatformId == platform.Id).ToList();
                var total   = platformDatLines.Sum(dl => dl.ReleaseCount);
                int present = 0, outdated = 0;
                foreach (var dl in platformDatLines.Where(dl => dl.DataStorePath.Length > 0))
                {
                    var absPath = Path.Combine(_dataDir, dl.DataStorePath);
                    if (File.Exists(absPath))
                    {
                        var (p, o) = new DatLineStore(absPath).GetStatusCounts();
                        present  += p;
                        outdated += o;
                    }
                }
                return new SystemPlatform
                {
                    Id           = platform.Id,
                    Name         = platform.Name,
                    Manufacturer = platform.Manufacturer,
                    HardwareType = _hardwareTypeMap.TryGetValue(platform.HardwareTypeId, out var ht) ? ht : "",
                    DatLines     = platformDatLines.Count,
                    TotalTitles  = total,
                    Present      = present,
                    Outdated     = outdated,
                    Missing      = Math.Max(0, total - present - outdated),
                    Lost         = 0,
                };
            })
            .ToList();

        BuildTree();
        UpdateActionBar();
    }

    private void SelectPlatform(string platformId)
    {
        _selectedPlatformId = platformId;
        _selectedPlatform   = _systemsPlatforms.FirstOrDefault(x => x.Id == platformId);
        _selectedDatLine    = null;
        BuildTree();
        UpdateDetailPane(_selectedPlatform, null);
        UpdateActionBar();
    }

    private void SelectDatLine(DatLineInfo d, string platformId)
    {
        _selectedPlatformId = platformId;
        _selectedPlatform   = _systemsPlatforms.FirstOrDefault(x => x.Id == platformId);
        _selectedDatLine    = d;
        BuildTree();
        UpdateDetailPane(_selectedPlatform, d);
        UpdateActionBar();
    }

    private void UpdateActionBar()
    {
        var hasPlatform = _selectedPlatformId is not null;
        var hasDat      = _selectedDatLine is not null;
        var datHasStore = hasDat && _selectedDatLine!.DataStorePath.Length > 0;

        SysActEditPlatform.IsEnabled  = hasPlatform;
        SysActConfigureDat.IsEnabled  = datHasStore;
        SysActUpdateDat.IsEnabled     = hasDat;
        SysActVerifyDat.IsEnabled     = datHasStore;
        SysActDeleteDat.IsEnabled     = hasDat;
        SysActIngestFiles.IsEnabled   = datHasStore;
    }

    private void BuildTree()
    {
        SystemsTreePanel.Children.Clear();

        if (_systemsPlatforms.Count == 0)
        {
            SystemsTreePanel.Children.Add(new TextBlock
            {
                Text         = "No platforms. Click 'Create Platform' to add one.",
                FontSize     = 12,
                Foreground   = new SolidColorBrush(Color.Parse("#555566")),
                Margin       = new Avalonia.Thickness(4, 4, 4, 0),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            return;
        }

        var groups      = _systemsPlatforms
            .GroupBy(p => p.HardwareType.Length > 0 ? p.HardwareType : "Other")
            .OrderBy(g => g.Key)
            .ToList();
        var showHeaders = groups.Count > 1;

        foreach (var group in groups)
        {
            if (showHeaders)
                SystemsTreePanel.Children.Add(MakeGroupHeader(group.Key));

            foreach (var p in group.OrderBy(p => p.Name))
            {
                var isSelectedPlatform = p.Id == _selectedPlatformId;
                SystemsTreePanel.Children.Add(MakePlatformNode(p, isSelectedPlatform));

                if (isSelectedPlatform)
                {
                    var dats = BuildDatLineInfos(p.Id);
                    foreach (var d in dats)
                    {
                        var isSelectedDat = _selectedDatLine?.CatalogId is not null &&
                                            _selectedDatLine.CatalogId == d.CatalogId;
                        SystemsTreePanel.Children.Add(MakeDatRow(d, p.Id, isSelectedDat));
                    }
                }
            }
        }
    }

    private Border MakePlatformNode(SystemPlatform p, bool isSelected)
    {
        var bgColor   = isSelected ? Color.Parse("#1E1E32") : Color.Parse("#111118");
        var bdrColor  = isSelected ? Color.Parse("#5555AA") : Color.Parse("#1E1E2E");
        var nameColor = isSelected ? Color.Parse("#E8E8FF") : Color.Parse("#CCCCDD");

        var leftBar = new Border
        {
            Width        = 3,
            Background   = new SolidColorBrush(isSelected ? Color.Parse("#7B68EE") : Color.Parse("#2A2A3E")),
            CornerRadius = new Avalonia.CornerRadius(2, 0, 0, 2),
        };

        var titleMain = p.Manufacturer.Length > 0 ? $"{p.Manufacturer} {p.Name}" : p.Name;
        var hwColor   = isSelected ? Color.Parse("#8888CC") : Color.Parse("#555566");

        var nameBlock = new TextBlock
        {
            TextWrapping      = Avalonia.Media.TextWrapping.NoWrap,
            TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        nameBlock.Inlines!.Add(new Avalonia.Controls.Documents.Run(titleMain)
        {
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(nameColor),
        });
        if (p.HardwareType.Length > 0)
            nameBlock.Inlines.Add(new Avalonia.Controls.Documents.Run($" ({p.HardwareType})")
            {
                FontSize   = 11,
                Foreground = new SolidColorBrush(hwColor),
            });

        var coverageBlock = new TextBlock
        {
            Text              = p.Coverage,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.Parse("#6B68EE")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin            = new Avalonia.Thickness(8, 0, 0, 0),
        };

        var datCountBlock = new TextBlock
        {
            Text              = $"{p.DatLines} DAT{(p.DatLines == 1 ? "" : "s")}",
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.Parse("#555566")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin            = new Avalonia.Thickness(10, 0, 0, 0),
        };

        var textRow = new Grid { Margin = new Avalonia.Thickness(10, 0, 10, 0) };
        textRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        textRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        textRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(nameBlock,     0);
        Grid.SetColumn(coverageBlock, 1);
        Grid.SetColumn(datCountBlock, 2);
        textRow.Children.Add(nameBlock);
        textRow.Children.Add(coverageBlock);
        textRow.Children.Add(datCountBlock);

        var logoImg = LoadSystemImage(p.Id, "logo")
                   ?? (_systemsThemeDir is not null ? SystemImageLoader.Load(_systemsThemeDir, p.Id) : null);

        var innerRow = new Grid();
        innerRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // accent bar
        if (logoImg is not null)
            innerRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // logo
        innerRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));  // text

        Grid.SetColumn(leftBar, 0);
        innerRow.Children.Add(leftBar);

        if (logoImg is not null)
        {
            var logoCtrl = new Image
            {
                Source            = logoImg,
                MaxHeight         = 22,
                MaxWidth          = 56,
                Stretch           = Avalonia.Media.Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin            = new Avalonia.Thickness(8, 0, 6, 0),
            };
            Grid.SetColumn(logoCtrl, 1);
            Grid.SetColumn(textRow,  2);
            innerRow.Children.Add(logoCtrl);
        }
        else
        {
            Grid.SetColumn(textRow, 1);
        }
        innerRow.Children.Add(textRow);

        var node = new Border
        {
            Background      = new SolidColorBrush(bgColor),
            BorderBrush     = new SolidColorBrush(bdrColor),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius    = new Avalonia.CornerRadius(6),
            Height          = 48,
            Cursor          = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child           = innerRow,
        };

        var platformId = p.Id;
        node.PointerPressed += (_, _) => SelectPlatform(platformId);

        return node;
    }

    private static Grid MakeGroupHeader(string label)
    {
        var lineColor = new SolidColorBrush(Color.Parse("#2A2A3E"));
        var textColor = new SolidColorBrush(Color.Parse("#44445A"));

        var leftLine = new Border
        {
            Height            = 1,
            Background        = lineColor,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var labelBlock = new TextBlock
        {
            Text              = label.ToUpperInvariant(),
            FontSize          = 9,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = textColor,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin            = new Avalonia.Thickness(8, 0),
        };
        var rightLine = new Border
        {
            Height            = 1,
            Background        = lineColor,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var header = new Grid { Margin = new Avalonia.Thickness(0, 14, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(leftLine,   0);
        Grid.SetColumn(labelBlock, 1);
        Grid.SetColumn(rightLine,  2);
        header.Children.Add(leftLine);
        header.Children.Add(labelBlock);
        header.Children.Add(rightLine);
        return header;
    }

    private Border MakeDatRow(DatLineInfo d, string platformId, bool isSelected)
    {
        var bgColor  = isSelected ? Color.Parse("#1A1A2E") : Color.Parse("#0D0D18");
        var bdrColor = isSelected ? Color.Parse("#44448A") : Color.Parse("#181826");

        // Line 1 — name
        var nameBlock = new TextBlock
        {
            Text         = d.Name,
            FontSize     = 12,
            FontWeight   = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground   = new SolidColorBrush(Color.Parse(isSelected ? "#D8D8F0" : "#AAAACC")),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        };

        // Line 2 — releases · strategy · mappings
        var stratLabel = d.TransformStrategyType switch
        {
            "file_extension" => "Per file extension",
            "release_folder" => "Per release folder",
            _                => null,
        };
        int mappingCount = 0;
        if (d.TransformStrategyType == "file_extension" && d.CatalogId is not null)
            mappingCount = _catalog.LoadExtensionMappings(d.CatalogId).Count;

        var subParts = new List<string> { $"{d.Releases:N0} releases" };
        if (stratLabel is not null)
        {
            subParts.Add(stratLabel);
            if (mappingCount > 0)
                subParts.Add($"{mappingCount} mapping{(mappingCount == 1 ? "" : "s")}");
        }

        var subBlock = new TextBlock
        {
            Text         = string.Join(" · ", subParts),
            FontSize     = 10,
            Foreground   = new SolidColorBrush(Color.Parse("#555566")),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            Margin       = new Avalonia.Thickness(0, 2, 0, 0),
        };

        var content = new StackPanel { Margin = new Avalonia.Thickness(28, 8, 10, 8) };
        content.Children.Add(nameBlock);
        content.Children.Add(subBlock);

        var row = new Border
        {
            Background      = new SolidColorBrush(bgColor),
            BorderBrush     = new SolidColorBrush(bdrColor),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius    = new Avalonia.CornerRadius(4),
            Margin          = new Avalonia.Thickness(16, 0, 0, 0),
            Cursor          = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child           = content,
        };

        var datInfo = d;
        var pId     = platformId;
        row.PointerPressed += (_, _) => SelectDatLine(datInfo, pId);

        return row;
    }

    private Bitmap? LoadDatAuthorityImage(string authority)
    {
        if (authority.Length == 0) return null;
        var key  = authority.ToLower().Replace(" ", "").Replace("-", "");
        var path = Path.Combine(_dataDir, "datimages", $"{key}.png");
        if (!File.Exists(path)) return null;
        try { return new Bitmap(path); } catch { return null; }
    }

    private Bitmap? LoadSystemImage(string platformId, string suffix)
    {
        var catalogPath = Path.Combine(_dataDir, "systemimages", $"{platformId}-{suffix}.png");
        if (File.Exists(catalogPath))
            try { return new Bitmap(catalogPath); } catch { }
        return null;
    }

    private void UpdateDetailPane(SystemPlatform? p, DatLineInfo? d)
    {
        SystemsDetailPanel.Children.Clear();

        if (p is null)
        {
            SystemsDetailEmptyMsg.IsVisible = true;
            return;
        }

        SystemsDetailEmptyMsg.IsVisible = false;

        if (d is not null)
            BuildDatDetailContent(d);
        else
            BuildPlatformDetailContent(p);
    }

    private void BuildPlatformDetailContent(SystemPlatform p)
    {
        var dim    = new SolidColorBrush(Color.Parse("#555566"));
        var text   = new SolidColorBrush(Color.Parse("#CCCCDD"));
        var accent = new SolidColorBrush(Color.Parse("#7B68EE"));
        var panel  = SystemsDetailPanel;

        // Platform image
        var img = LoadSystemImage(p.Id, "details")
               ?? LoadSystemImage(p.Id, "logo")
               ?? (_systemsThemeDir is not null ? SystemImageLoader.Load(_systemsThemeDir, p.Id) : null);
        if (img is not null)
            panel.Children.Add(new Image
            {
                Source              = img,
                Width               = 300,
                Height              = 300,
                Stretch             = Avalonia.Media.Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin              = new Avalonia.Thickness(0, 0, 0, 16),
            });

        // Platform name
        panel.Children.Add(new TextBlock
        {
            Text         = p.Name,
            FontSize     = 16,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = new SolidColorBrush(Color.Parse("#E8E8F8")),
            Margin       = new Avalonia.Thickness(0, 0, 0, 2),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        // Manufacturer + hardware type subtitle
        var subtitle = p.Manufacturer.Length > 0
            ? (p.HardwareType.Length > 0 ? $"{p.Manufacturer}  ·  {p.HardwareType}" : p.Manufacturer)
            : p.HardwareType;
        if (subtitle.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text         = subtitle,
                FontSize     = 11,
                Foreground   = dim,
                Margin       = new Avalonia.Thickness(0, 0, 0, 14),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        else
            panel.Children.Add(new Border { Height = 14 });

        // Hardware spec rows
        var record = _catalog.GetPlatform(p.Id);
        var hasHw = record is not null && (
            !string.IsNullOrEmpty(record.Cpu)               ||
            !string.IsNullOrEmpty(record.Memory)            ||
            !string.IsNullOrEmpty(record.Graphics)          ||
            !string.IsNullOrEmpty(record.Sound)             ||
            !string.IsNullOrEmpty(record.DisplayResolution) ||
            !string.IsNullOrEmpty(record.AspectRatio));

        if (hasHw)
        {
            void AddHwRow(string label, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                var g = new Grid { Margin = new Avalonia.Thickness(0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(100)));
                g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var k = new TextBlock { Text = label, FontSize = 11, Foreground = dim };
                var v = new TextBlock { Text = value, FontSize = 11, Foreground = text,
                                        TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                Grid.SetColumn(k, 0); Grid.SetColumn(v, 1);
                g.Children.Add(k); g.Children.Add(v);
                panel.Children.Add(g);
            }
            AddHwRow("CPU",        record!.Cpu);
            AddHwRow("Memory",     record.Memory);
            AddHwRow("Graphics",   record.Graphics);
            AddHwRow("Sound",      record.Sound);
            AddHwRow("Resolution", record.DisplayResolution);
            AddHwRow("Aspect",     record.AspectRatio);
        }

        // Divider
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1E1E3A")),
            Margin     = new Avalonia.Thickness(0, 10, 0, 10),
        });

        // Stats grid
        var statsGrid = new Grid();
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        void AddStat(string label, string value, bool isAccent = false)
        {
            var row = statsGrid.RowDefinitions.Count;
            statsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = label, FontSize = 12, Foreground = dim,
                                    Margin = new Avalonia.Thickness(0, 3) };
            var v = new TextBlock { Text = value, FontSize = 12,
                                    Foreground   = isAccent ? accent : text,
                                    FontWeight   = FontWeight.Medium,
                                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                    Margin = new Avalonia.Thickness(0, 3) };
            Grid.SetRow(k, row); Grid.SetColumn(k, 0);
            Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            statsGrid.Children.Add(k); statsGrid.Children.Add(v);
        }

        AddStat("DAT Lines", $"{p.DatLines:N0}");
        AddStat("Titles",    $"{p.TotalTitles:N0}");
        AddStat("Present",   $"{p.Present:N0}");
        if (p.Outdated > 0) AddStat("Outdated", $"{p.Outdated:N0}");
        AddStat("Missing",   $"{p.Missing:N0}");
        if (p.Lost > 0)     AddStat("Lost",     $"{p.Lost:N0}");
        AddStat("Coverage",  p.Coverage, isAccent: true);

        panel.Children.Add(statsGrid);
    }

    private void BuildDatDetailContent(DatLineInfo d)
    {
        var dim   = new SolidColorBrush(Color.Parse("#555566"));
        var text  = new SolidColorBrush(Color.Parse("#E0E0F0"));
        var panel = SystemsDetailPanel;

        // Compute status counts from store
        int present        = 0;
        int reconciliation = 0;  // = 'lost' releases only
        if (d.DataStorePath.Length > 0)
        {
            var absPath = Path.Combine(_dataDir, d.DataStorePath);
            if (File.Exists(absPath))
            {
                var counts     = new DatLineStore(absPath).GetAllStatusCounts();
                present        = counts.Present;
                reconciliation = counts.Lost;
            }
        }
        var coverage = d.Releases > 0
            ? $"{(double)present / d.Releases:P1}"
            : "—";

        // ── Header ───────────────────────────────────────────────────────────

        // Authority logo
        var authorityImg = LoadDatAuthorityImage(d.Authority);
        if (authorityImg is not null)
            panel.Children.Add(new Image
            {
                Source              = authorityImg,
                Width               = 260,
                Height              = 260,
                Stretch             = Avalonia.Media.Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin              = new Avalonia.Thickness(0, 0, 0, 12),
            });

        // DAT name
        panel.Children.Add(new TextBlock
        {
            Text         = d.Name,
            FontSize     = 14,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = new SolidColorBrush(Color.Parse("#E8E8F8")),
            Margin       = new Avalonia.Thickness(0, 0, 0, 4),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        // Updated date + release count subtitle
        var headerSub = new List<string>();
        if (d.LastImport.Length > 0) headerSub.Add($"Updated {d.LastImport}");
        headerSub.Add($"{d.Releases:N0} releases");
        panel.Children.Add(new TextBlock
        {
            Text       = string.Join("  ·  ", headerSub),
            FontSize   = 11,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 16),
        });

        // ── Status block ─────────────────────────────────────────────────────

        void AddStatusRow(string label, string value)
        {
            var g = new Grid { Margin = new Avalonia.Thickness(0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.Children.Add(new TextBlock
            {
                Text              = label,
                FontSize          = 11,
                Foreground        = dim,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var val = new TextBlock
            {
                Text              = value,
                FontSize          = 14,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = text,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(val, 1);
            g.Children.Add(val);
            panel.Children.Add(g);
        }

        AddStatusRow("Present",        $"{present:N0}");
        AddStatusRow("Incomplete",     $"{d.Outdated:N0}");
        AddStatusRow("Reconciliation", $"{reconciliation:N0}");
        AddStatusRow("Coverage",       coverage);

        // ── Transform strategy summary ────────────────────────────────────────
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1E1E3A")),
            Margin     = new Avalonia.Thickness(0, 12, 0, 10),
        });

        panel.Children.Add(new TextBlock
        {
            Text       = "TRANSFORM STRATEGY",
            FontSize   = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = dim,
            Margin     = new Avalonia.Thickness(0, 0, 0, 6),
        });

        if (d.TransformStrategyType == "file_extension" && d.CatalogId is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text       = "Per file extension",
                FontSize   = 12,
                FontWeight = FontWeight.Medium,
                Foreground = text,
                Margin     = new Avalonia.Thickness(0, 0, 0, 8),
            });

            var allTransforms = _catalog.LoadTransforms()
                .ToDictionary(t => t.Id, t => t.Name);
            var mappings = _catalog.LoadExtensionMappings(d.CatalogId)
                .OrderBy(m => m.FileExtension)
                .ToList();

            const int maxShow = 5;
            foreach (var m in mappings.Take(maxShow))
            {
                var transformName = m.IsDiscard ? "Discard"
                    : (allTransforms.TryGetValue(m.TransformId, out var tn) ? tn : m.TransformId);

                var mappingRow = new Grid { Margin = new Avalonia.Thickness(0, 2) };
                mappingRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(70)));
                mappingRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                mappingRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                var extBlock = new TextBlock
                {
                    Text      = m.FileExtension,
                    FontSize  = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#8888BB")),
                    FontFamily = new Avalonia.Media.FontFamily("Consolas, monospace"),
                };
                var arrowBlock = new TextBlock
                {
                    Text       = "→",
                    FontSize   = 11,
                    Foreground = dim,
                    Margin     = new Avalonia.Thickness(6, 0),
                };
                var nameBlock2 = new TextBlock
                {
                    Text         = transformName,
                    FontSize     = 11,
                    Foreground   = m.IsDiscard
                        ? new SolidColorBrush(Color.Parse("#EF5350"))
                        : text,
                    TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(extBlock,   0);
                Grid.SetColumn(arrowBlock, 1);
                Grid.SetColumn(nameBlock2, 2);
                mappingRow.Children.Add(extBlock);
                mappingRow.Children.Add(arrowBlock);
                mappingRow.Children.Add(nameBlock2);
                panel.Children.Add(mappingRow);
            }

            if (mappings.Count > maxShow)
                panel.Children.Add(new TextBlock
                {
                    Text       = $"+{mappings.Count - maxShow} more mapping{(mappings.Count - maxShow == 1 ? "" : "s")}",
                    FontSize   = 10,
                    Foreground = dim,
                    Margin     = new Avalonia.Thickness(0, 4, 0, 0),
                });

            if (mappings.Count == 0)
                panel.Children.Add(new TextBlock
                {
                    Text       = "No mappings configured",
                    FontSize   = 11,
                    Foreground = dim,
                });
        }
        else if (d.TransformStrategyType == "release_folder")
        {
            panel.Children.Add(new TextBlock
            {
                Text       = "Per release folder",
                FontSize   = 12,
                FontWeight = FontWeight.Medium,
                Foreground = text,
                Margin     = new Avalonia.Thickness(0, 0, 0, 6),
            });

            if (d.FolderTransformId.Length > 0)
            {
                var allTransforms = _catalog.LoadTransforms()
                    .ToDictionary(t => t.Id, t => t.Name);
                var folderName = allTransforms.TryGetValue(d.FolderTransformId, out var fn) ? fn : d.FolderTransformId;

                var folderRow = new Grid { Margin = new Avalonia.Thickness(0, 2) };
                folderRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(70)));
                folderRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                folderRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var folderLabel = new TextBlock
                {
                    Text      = "Folder",
                    FontSize  = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#8888BB")),
                };
                var arrow2 = new TextBlock
                {
                    Text = "→", FontSize = 11, Foreground = dim,
                    Margin = new Avalonia.Thickness(6, 0),
                };
                var folderNameBlock = new TextBlock
                {
                    Text         = folderName,
                    FontSize     = 11,
                    Foreground   = text,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(folderLabel,     0);
                Grid.SetColumn(arrow2,          1);
                Grid.SetColumn(folderNameBlock, 2);
                folderRow.Children.Add(folderLabel);
                folderRow.Children.Add(arrow2);
                folderRow.Children.Add(folderNameBlock);
                panel.Children.Add(folderRow);
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No folder transform assigned", FontSize = 11, Foreground = dim,
                });
            }
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text       = "Not configured",
                FontSize   = 11,
                Foreground = dim,
            });
        }
    }

    private List<DatLineInfo> BuildDatLineInfos(string platformId)
        => _catalog.LoadDatLines()
            .Where(dl => dl.PlatformId == platformId)
            .Select(dl =>
            {
                int outdated = 0;
                if (dl.DataStorePath.Length > 0)
                {
                    var absPath = Path.Combine(_dataDir, dl.DataStorePath);
                    if (File.Exists(absPath))
                        outdated = new DatLineStore(absPath).GetStatusCounts().Outdated;
                }
                return new DatLineInfo(
                    Name:                  dl.Name,
                    Releases:              dl.ReleaseCount,
                    Outdated:              outdated,
                    LastImport:            dl.ImportedAtUtc.ToString("yyyy-MM-dd"),
                    StorageStrategy:       _strategyNameMap.TryGetValue(dl.StorageStrategyId, out var sn) ? sn : "",
                    Authority:             dl.Authority,
                    DatCategory:           dl.DatCategory,
                    DataStorePath:         dl.DataStorePath,
                    CatalogId:             dl.Id,
                    CatalogPlatformId:     dl.PlatformId,
                    TransformStrategyType: dl.TransformStrategyType,
                    FolderTransformId:     dl.FolderTransformId);
            })
            .ToList();

    // ── Action bar event handlers ─────────────────────────────────────────────

    private async void OnSysConfigureDat(object? sender, RoutedEventArgs e)
    {
        if (_selectedDatLine is null || _selectedDatLine.DataStorePath.Length == 0) return;
        var d          = _selectedDatLine;
        var platformId = _selectedPlatformId;
        var dialog     = new ConfigureDatLineDialog(d, _catalog, _dataDir);
        var saved      = await dialog.ShowDialog<bool>(this);
        if (saved) RefreshSystemsKeepSelection(platformId);
    }

    private void OnSysUpdateDat(object? sender, RoutedEventArgs e)
    {
        if (_selectedDatLine is null) return;
        _ = OnUpdateDatLine(_selectedDatLine);
    }

    private void OnSysVerifyDat(object? sender, RoutedEventArgs e)
    {
        if (_selectedDatLine is null) return;
        _ = OnVerifyDatLine(_selectedDatLine);
    }

    private async void OnSysDeleteDat(object? sender, RoutedEventArgs e)
    {
        if (_selectedDatLine is null) return;
        var d = _selectedDatLine;
        if (d.CatalogId is null || d.CatalogPlatformId is null) return;
        await OnDeleteDatLine(d.CatalogId, d.CatalogPlatformId, d);
    }

    private void OnSysIngestFiles(object? sender, RoutedEventArgs e)
    {
        if (_selectedDatLine is null) return;
        OnIngestDatLine(_selectedDatLine);
    }

    private void NavigateToLibrary(string platform, string datLine)
    {
        // Suppress both handlers while we set both selectors atomically
        LibraryContextPlatform.SelectionChanged -= OnLibraryContextPlatformChanged;
        LibraryContextDatLine.SelectionChanged  -= OnLibraryContextDatLineChanged;

        var platforms = LibraryContextPlatform.ItemsSource as List<string>;
        var pIdx      = platforms?.IndexOf(platform) ?? -1;
        if (pIdx >= 0)
            LibraryContextPlatform.SelectedIndex = pIdx;

        var datLines = _activeLibraryDatasets
            .Where(d => d.Platform == platform)
            .Select(d => d.DatLine)
            .ToList();
        LibraryContextDatLine.ItemsSource = datLines;
        var dIdx = datLines.IndexOf(datLine);
        LibraryContextDatLine.SelectedIndex = dIdx >= 0 ? dIdx : 0;

        LibraryContextPlatform.SelectionChanged += OnLibraryContextPlatformChanged;
        LibraryContextDatLine.SelectionChanged  += OnLibraryContextDatLineChanged;

        LoadActiveDataset(platform, dIdx >= 0 ? datLine : (datLines.Count > 0 ? datLines[0] : null));
        SetActive(NavLibrary);
    }

    private void ResolveFlagImages()
    {
        if (_systemsThemeDir is null) return;
        foreach (var dataset in _activeLibraryDatasets)
            foreach (var entry in dataset.Entries)
                entry.FlagImages = entry.Languages
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => FlagImageLoader.Load(_systemsThemeDir, c))
                    .OfType<Bitmap>()   // drops nulls (no file found)
                    .ToList();
    }

    // ── Staging ───────────────────────────────────────────────────────────────

    private StagingReleaseNode? _selectedStagingRelease;
    private static readonly string _stagingRoot = Path.Combine(AppContext.BaseDirectory, "staging");

    private void InitStaging() => RefreshStaging();

    private void RefreshStaging()
    {
        var (platforms, summary) = BuildStagingTree();

        // Summary card
        StagingSumFiles.Text    = summary.FilesPresent.ToString("N0");
        StagingSumSize.Text     = summary.SizeGbLabel;
        StagingSumReleases.Text = summary.IncompleteReleases.ToString("N0");
        StagingCountText.Text   = $"{summary.IncompleteReleases} incomplete";

        // Populate tree
        StagingTree.Children.Clear();
        _selectedStagingRelease = null;
        UpdateStagingDetailPanel(null);

        if (platforms.Count == 0)
        {
            StagingTreeScroll.IsVisible = false;
            StagingTreeEmpty.IsVisible  = true;
            return;
        }

        StagingTreeEmpty.IsVisible  = false;
        StagingTreeScroll.IsVisible = true;

        var secondary  = new SolidColorBrush(Color.Parse("#888899"));
        var primary    = new SolidColorBrush(Color.Parse("#D0D0E0"));
        var barBg      = new SolidColorBrush(Color.Parse("#242433"));
        var barFill    = new SolidColorBrush(Color.Parse("#555577"));

        foreach (var platform in platforms)
        {
            // Platform header
            var platformHeader = new TextBlock
            {
                Text       = platform.PlatformName,
                FontSize   = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = secondary,
                Margin     = new Avalonia.Thickness(16, 10, 16, 4),
            };
            StagingTree.Children.Add(platformHeader);

            foreach (var datLine in platform.DatLines)
            {
                // DAT line header
                var datLineHeader = new TextBlock
                {
                    Text       = datLine.DatLineName,
                    FontSize   = 11,
                    Foreground = secondary,
                    Margin     = new Avalonia.Thickness(24, 6, 16, 2),
                };
                StagingTree.Children.Add(datLineHeader);

                foreach (var release in datLine.Releases)
                {
                    var capturedRelease = release;

                    // Progress bar
                    var barTrack = new Border
                    {
                        Width         = 60,
                        Height        = 4,
                        CornerRadius  = new Avalonia.CornerRadius(2),
                        Background    = barBg,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Child = new Border
                        {
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                            Width        = 60 * release.ProgressRatio,
                            Height       = 4,
                            CornerRadius = new Avalonia.CornerRadius(2),
                            Background   = barFill,
                        },
                    };

                    var label = new TextBlock
                    {
                        Text         = release.ReleaseName,
                        FontSize     = 12,
                        Foreground   = primary,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    };

                    var progressLabel = new TextBlock
                    {
                        Text       = release.ProgressLabel,
                        FontSize   = 11,
                        Foreground = secondary,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Margin     = new Avalonia.Thickness(6, 0, 0, 0),
                    };

                    var row = new Grid { Margin = new Avalonia.Thickness(32, 1, 16, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                    Grid.SetColumn(label,         0);
                    Grid.SetColumn(progressLabel, 1);
                    Grid.SetColumn(barTrack,      2);
                    barTrack.Margin = new Avalonia.Thickness(8, 0, 0, 0);

                    row.Children.Add(label);
                    row.Children.Add(progressLabel);
                    row.Children.Add(barTrack);

                    var rowBorder = new Border
                    {
                        Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        Padding    = new Avalonia.Thickness(0, 5),
                        Background = Avalonia.Media.Brushes.Transparent,
                        Child      = row,
                    };

                    rowBorder.PointerPressed += (_, _) =>
                    {
                        _selectedStagingRelease = capturedRelease;
                        UpdateStagingDetailPanel(capturedRelease);
                    };
                    rowBorder.PointerEntered += (_, _) =>
                        rowBorder.Background = new SolidColorBrush(Color.Parse("#1A1A2A"));
                    rowBorder.PointerExited  += (_, _) =>
                        rowBorder.Background = Avalonia.Media.Brushes.Transparent;

                    StagingTree.Children.Add(rowBorder);
                }
            }
        }
    }

    private (List<StagingPlatformNode> Platforms, StagingSummary Summary) BuildStagingTree()
    {
        var summary   = new StagingSummary();
        var platforms = new List<StagingPlatformNode>();

        if (!Directory.Exists(_stagingRoot))
            return (platforms, summary);

        var allPlatforms = _catalog.LoadPlatforms()
            .ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);
        var allDatLines  = _catalog.LoadDatLines()
            .ToDictionary(dl => dl.Id, dl => dl, StringComparer.Ordinal);

        foreach (var platformDir in Directory.GetDirectories(_stagingRoot))
        {
            var platformId   = Path.GetFileName(platformDir);
            var platformName = allPlatforms.TryGetValue(platformId, out var pn) ? pn : platformId;
            var datLineNodes = new List<StagingDatLineNode>();

            foreach (var datLineDir in Directory.GetDirectories(platformDir))
            {
                var datLineId   = Path.GetFileName(datLineDir);
                var datLineName = allDatLines.TryGetValue(datLineId, out var dl) ? dl.Name : datLineId;

                // Open the DAT-line DB to get releases and their expected files
                if (!allDatLines.TryGetValue(datLineId, out var dlRecord) ||
                    dlRecord.DataStorePath.Length == 0) continue;

                var absDbPath = Path.Combine(_dataDir, dlRecord.DataStorePath);
                if (!File.Exists(absDbPath)) continue;

                var store    = new DatLineStore(absDbPath);
                var releases = store.LoadReleases()
                    .Where(r => r.Status != "outdated")
                    .ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
                var allFiles = store.LoadAllReleaseFiles();

                var releaseNodes = new List<StagingReleaseNode>();

                foreach (var releaseDir in Directory.GetDirectories(datLineDir))
                {
                    var folderName = Path.GetFileName(releaseDir);

                    // Match folder name to a release by safe-name comparison
                    var matchedRelease = releases.Values.FirstOrDefault(r =>
                        string.Equals(SafeFileName(r.Name), folderName,
                            StringComparison.OrdinalIgnoreCase));
                    if (matchedRelease is null) continue;

                    var expectedFiles = allFiles.TryGetValue(matchedRelease.Id, out var ef)
                        ? ef : new List<Data.ReleaseFileRecord>();

                    int filesPresent = 0;
                    long dirSize     = 0;
                    int fileCount    = 0;

                    foreach (var f in expectedFiles)
                    {
                        var fp = Path.Combine(releaseDir, f.RomName);
                        if (File.Exists(fp))
                        {
                            filesPresent++;
                            try
                            {
                                var fi = new FileInfo(fp);
                                dirSize   += fi.Length;
                                fileCount++;
                            }
                            catch { }
                        }
                    }

                    // Also count any extra files present in the folder
                    try
                    {
                        foreach (var fp in Directory.GetFiles(releaseDir))
                        {
                            var fn = Path.GetFileName(fp);
                            if (!expectedFiles.Any(ef2 => ef2.RomName == fn))
                            {
                                try { dirSize += new FileInfo(fp).Length; fileCount++; } catch { }
                            }
                        }
                    }
                    catch { }

                    summary.FilesPresent       += fileCount;
                    summary.TotalSizeBytes     += dirSize;
                    summary.IncompleteReleases++;

                    releaseNodes.Add(new StagingReleaseNode
                    {
                        ReleaseName   = matchedRelease.Name,
                        ReleasePath   = releaseDir,
                        ReleaseId     = matchedRelease.Id,
                        PlatformId    = platformId,
                        DatLineId     = datLineId,
                        ExpectedFiles = expectedFiles,
                        FilesPresent  = filesPresent,
                    });
                }

                if (releaseNodes.Count == 0) continue;

                // Sort: completion ratio DESC, then name
                releaseNodes.Sort((a, b) =>
                {
                    int cmp = b.ProgressRatio.CompareTo(a.ProgressRatio);
                    return cmp != 0 ? cmp : string.Compare(a.ReleaseName, b.ReleaseName,
                        StringComparison.OrdinalIgnoreCase);
                });

                datLineNodes.Add(new StagingDatLineNode
                {
                    DatLineId   = datLineId,
                    DatLineName = datLineName,
                    Releases    = releaseNodes,
                });
            }

            if (datLineNodes.Count == 0) continue;

            platforms.Add(new StagingPlatformNode
            {
                PlatformId   = platformId,
                PlatformName = platformName,
                DatLines     = datLineNodes,
            });
        }

        return (platforms, summary);
    }

    private void UpdateStagingDetailPanel(StagingReleaseNode? release)
    {
        if (release is null)
        {
            StagingDetailEmpty.IsVisible   = true;
            StagingDetailContent.IsVisible = false;
            return;
        }

        StagingDetailName.Text     = release.ReleaseName;
        StagingDetailPlatform.Text = _catalog.GetPlatform(release.PlatformId)?.Name ?? release.PlatformId;
        StagingDetailDatLine.Text  = release.DatLineId;
        StagingDetailProgress.Text = release.ProgressLabel;

        StagingDetailFiles.Children.Clear();
        var green    = new SolidColorBrush(Color.Parse("#4CAF50"));
        var red      = new SolidColorBrush(Color.Parse("#EF5350"));
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var mono     = new FontFamily("Consolas,Courier New,monospace");

        foreach (var f in release.ExpectedFiles)
        {
            var present  = File.Exists(Path.Combine(release.ReleasePath, f.RomName));
            var icon     = new TextBlock
            {
                Text       = present ? "✓" : "✗",
                Foreground = present ? green : red,
                FontSize   = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin     = new Avalonia.Thickness(0, 1, 6, 0),
            };

            var fileName = new TextBlock
            {
                Text         = f.RomName,
                FontSize     = 11,
                Foreground   = new SolidColorBrush(Color.Parse("#D0D0E0")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };

            static TextBlock Meta(string text, SolidColorBrush fg, FontFamily ff) => new()
            {
                Text       = text,
                FontSize   = 10,
                Foreground = fg,
                FontFamily = ff,
            };

            var metaStack = new StackPanel { Spacing = 1 };
            metaStack.Children.Add(fileName);
            if (f.Size.Length > 0)
                metaStack.Children.Add(Meta($"Size: {f.Size}", secondary, mono));
            if (f.Crc.Length > 0)
                metaStack.Children.Add(Meta($"CRC:  {f.Crc}", secondary, mono));
            if (f.Md5.Length > 0)
                metaStack.Children.Add(Meta($"MD5:  {f.Md5}", secondary, mono));
            if (f.Sha1.Length > 0)
                metaStack.Children.Add(Meta($"SHA1: {f.Sha1}", secondary, mono));

            var row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin      = new Avalonia.Thickness(0, 0, 0, 8),
                Children    = { icon, metaStack },
            };

            StagingDetailFiles.Children.Add(row);
        }

        StagingDetailEmpty.IsVisible   = false;
        StagingDetailContent.IsVisible = true;
    }

    private void OnStagingOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_selectedStagingRelease is null) return;
        var path = _selectedStagingRelease.ReleasePath;
        if (!Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    // Active dataset entries (set when DAT line changes)
    private IReadOnlyList<LibraryEntry> _activeDatasetEntries = [];
    private List<LibraryDataset> _activeLibraryDatasets = [];

    // ── Disks ─────────────────────────────────────────────────────────────────

    private List<DiskEntry> _allDiskEntries  = [];
    private List<DiskEntry> _filteredDisks   = [];

    private void InitDisks() => RefreshDisks();

    private void RefreshDisks()
    {
        var disks = _catalog.GetDisks();
        var diskLabels = disks.ToDictionary(d => d.Id, d => d.Label, StringComparer.Ordinal);

        _allDiskEntries = disks.Select(d =>
        {
            var (cap, used, _) = _catalog.GetDiskUsage(d.Id);
            return new DiskEntry
            {
                Id                    = d.Id,
                Label                 = d.Label,
                Status                = d.Status,
                DeclaredCapacityBytes = cap > 0 ? cap : d.DeclaredCapacityBytes,
                UsedBytes             = used,
                Filesystem            = d.Filesystem,
                Brand                 = d.Brand,
                Model                 = d.Model,
                Serial                = d.Serial,
            };
        }).ToList();

        ApplyDisksFilter();
    }

    private void ApplyDisksFilter()
    {
        var search = DisksSearchBox.Text?.Trim() ?? string.Empty;
        _filteredDisks = search.Length == 0
            ? _allDiskEntries
            : _allDiskEntries
                .Where(d => d.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            d.Model.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

        DisksList.ItemsSource  = _filteredDisks;
        DisksCountText.Text    = _filteredDisks.Count == _allDiskEntries.Count
            ? $"{_allDiskEntries.Count} disks"
            : $"{_filteredDisks.Count} of {_allDiskEntries.Count} disks";
        UpdateDiskDetailPanel(null);
    }

    private void OnDisksSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        => ApplyDisksFilter();

    private void OnDiskSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => UpdateDiskDetailPanel(DisksList.SelectedItem as DiskEntry);

    private async void OnAddDisk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Peek (no increment) for display; label is only committed on confirm.
        var previewLabel = _catalog.PeekNextDiskLabel();
        var dialog       = new CreateDiskDialog(previewLabel);
        var ok           = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.Result is null || dialog.SelectedDrive is null) return;

        var mountpoint = dialog.SelectedDrive.Mountpoint;

        try
        {
            // ── Commit label sequence ─────────────────────────────────────────
            var confirmedLabel = _catalog.NextDiskLabel();

            // ── Safety: no marker overwrite in Add Disk ───────────────────────
            var markerPath = Data.DiskDiscoveryService.MarkerPath(mountpoint);
            if (File.Exists(markerPath))
            {
                await new InfoDialog("Drive Already Initialized",
                    $"The selected drive already has an Arkadia marker:\n{markerPath}\n\n" +
                    "Use Reinitialize Disk to rebind an existing disk record to this drive.\n\n" +
                    "No disk record was created.")
                    .ShowDialog(this);
                return;
            }

            // ── Apply filesystem label ────────────────────────────────────────
            if (!string.Equals(dialog.SelectedDrive.FileSystemLabel, confirmedLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                bool set = Data.VolumeLabel.TrySet(mountpoint, confirmedLabel);
                if (!set)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    await new InfoDialog("Label Set Failed",
                        $"Could not set volume label to \"{confirmedLabel}\".\n" +
                        $"Win32 error: {err}\n\n" +
                        "Try running as Administrator, or ensure the drive is not read-only.\n\n" +
                        "No disk record was created.")
                        .ShowDialog(this);
                    return;
                }
            }

            // ── Write marker ──────────────────────────────────────────────────
            var raw        = dialog.Result;
            var now        = DateTime.UtcNow;
            var markerJson = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    marker_type     = "arkadia_disk",
                    marker_version  = 1,
                    disk_id         = raw.Id,
                    disk_label      = confirmedLabel,
                    initialized_utc = now.ToString("o"),
                    capacity_bytes  = raw.DeclaredCapacityBytes,
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(markerPath, markerJson);
            bool markerWritten = true;

            // ── Save disk record — only after all init succeeds ───────────────
            // If SaveDisk throws, roll back the marker so no orphaned marker exists.
            var record = new Data.DiskRecord
            {
                Id                    = raw.Id,
                Label                 = confirmedLabel,
                Status                = "available",
                DeclaredCapacityBytes = raw.DeclaredCapacityBytes,
                Filesystem            = raw.Filesystem,
                Brand                 = raw.Brand,
                Model                 = raw.Model,
                Serial                = raw.Serial,
                CreatedAt             = now,
                UpdatedAt             = now,
            };
            try
            {
                _catalog.SaveDisk(record);
            }
            catch
            {
                // Roll back the marker before surfacing the error.
                if (markerWritten)
                    try { File.Delete(markerPath); } catch { /* best-effort */ }
                throw;
            }

            RefreshDisks();

            await new InfoDialog("Disk Added",
                $"Disk \"{confirmedLabel}\" has been created and initialized.\n\n" +
                $"Drive:    {mountpoint}\n" +
                $"Capacity: {FormatBytes(record.DeclaredCapacityBytes)}\n" +
                $"Marker:   {markerPath}")
                .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Add Disk Error", ex.Message).ShowDialog(this);
        }
    }

    private async void OnInitializeDisk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = DisksList.SelectedItem as DiskEntry;
        if (entry is null)
        {
            await new InfoDialog("No Disk Selected",
                "Select a disk from the list before initializing.")
                .ShowDialog(this);
            return;
        }

        try
        {
            // ── Discover mounted volumes ──────────────────────────────────────
            var drives = Data.DiskDiscoveryService.DiscoverAll();
            if (drives.Count == 0)
            {
                await new InfoDialog("No Drives Found",
                    "No fixed or removable drives were detected.")
                    .ShowDialog(this);
                return;
            }

            var rows = drives.ConvertAll(d => new DiscoveredDiskRow { Source = d });

            var dialog = new InitializeDiskDialog(entry.Label, rows);
            var ok     = await dialog.ShowDialog<bool>(this);
            if (!ok || dialog.SelectedDrive is null) return;

            var drive      = dialog.SelectedDrive;
            var mountpoint = drive.Mountpoint;

            // ── Check for existing marker ─────────────────────────────────────
            var markerPath = Data.DiskDiscoveryService.MarkerPath(mountpoint);
            if (File.Exists(markerPath))
            {
                await new InfoDialog("Disk Already Initialized",
                    $"A marker file already exists at:\n{markerPath}\n\n" +
                    "Re-initialization is not supported in v1. " +
                    "Remove the marker manually if you need to reinitialize.")
                    .ShowDialog(this);
                return;
            }

            // ── Set filesystem label if it differs ────────────────────────────
            if (!string.Equals(drive.FileSystemLabel, entry.Label,
                    StringComparison.OrdinalIgnoreCase))
            {
                bool set = Data.VolumeLabel.TrySet(mountpoint, entry.Label);
                if (!set)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    await new InfoDialog("Label Set Failed",
                        $"Could not set volume label to \"{entry.Label}\".\n" +
                        $"Win32 error: {err}\n\n" +
                        "Try running as Administrator, or ensure the drive is not read-only.\n\n" +
                        "No marker was written.")
                        .ShowDialog(this);
                    return;
                }
            }

            // ── Detect capacity ───────────────────────────────────────────────
            long capacityBytes = drive.TotalCapacityBytes;

            // ── Write marker ──────────────────────────────────────────────────
            var now       = DateTime.UtcNow;
            var nowIso    = now.ToString("o");
            var markerJson = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    marker_type     = "arkadia_disk",
                    marker_version  = 1,
                    disk_id         = entry.Id,
                    disk_label      = entry.Label,
                    initialized_utc = nowIso,
                    capacity_bytes  = capacityBytes,
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(markerPath, markerJson);

            // ── Persist capacity into disk record ─────────────────────────────
            _catalog.UpdateDiskCapacity(entry.Id, capacityBytes);

            // ── Refresh UI ────────────────────────────────────────────────────
            RefreshDisks();
            var updated = _filteredDisks.FirstOrDefault(d => d.Id == entry.Id);
            if (updated is not null)
            {
                DisksList.SelectedItem = updated;
                UpdateDiskDetailPanel(updated);
            }

            await new InfoDialog("Disk Initialized",
                $"Disk \"{entry.Label}\" has been initialized.\n\n" +
                $"Drive:    {mountpoint}\n" +
                $"Capacity: {FormatBytes(capacityBytes)}\n" +
                $"Marker:   {markerPath}")
                .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Initialize Disk Error", ex.Message).ShowDialog(this);
        }
    }

    private async void OnReinitializeDisk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = DisksList.SelectedItem as DiskEntry;
        if (entry is null)
        {
            await new InfoDialog("No Disk Selected",
                "Select a disk from the list before reinitializing.")
                .ShowDialog(this);
            return;
        }

        try
        {
            // ── Discover mounted volumes ──────────────────────────────────────
            var drives = Data.DiskDiscoveryService.DiscoverAll();
            if (drives.Count == 0)
            {
                await new InfoDialog("No Drives Found",
                    "No fixed or removable drives were detected.")
                    .ShowDialog(this);
                return;
            }

            var rows   = drives.ConvertAll(d => new DiscoveredDiskRow { Source = d });
            var dialog = new InitializeDiskDialog($"Reinitialize — {entry.Label}", rows);
            var ok     = await dialog.ShowDialog<bool>(this);
            if (!ok || dialog.SelectedDrive is null) return;

            var drive      = dialog.SelectedDrive;
            var mountpoint = drive.Mountpoint;
            var markerPath = Data.DiskDiscoveryService.MarkerPath(mountpoint);

            // ── Marker overwrite confirmation (if present) ────────────────────
            if (drive.HasMarker)
            {
                var existingInfo = drive.DiskId == entry.Id
                    ? $"This drive is already marked as this disk ({drive.DiskLabel})."
                    : $"This drive carries a DIFFERENT Arkadia marker:\n" +
                      $"  Disk ID:    {drive.DiskId}\n" +
                      $"  Disk Label: {drive.DiskLabel}\n\n" +
                      $"That marker will be permanently overwritten.";

                var confirmed = await new ConfirmDialog(
                    "Overwrite Existing Marker",
                    $"The selected drive already has an ARKADIA.DISK.json marker.\n\n" +
                    existingInfo + "\n\n" +
                    $"Reinitializing will write a new marker for \"{entry.Label}\" " +
                    $"(ID: {entry.Id}).\n\n" +
                    "This cannot be undone. Proceed?")
                    .ShowDialog<bool>(this);
                if (!confirmed) return;
            }

            // ── Set filesystem label if it differs ────────────────────────────
            if (!string.Equals(drive.FileSystemLabel, entry.Label,
                    StringComparison.OrdinalIgnoreCase))
            {
                bool set = Data.VolumeLabel.TrySet(mountpoint, entry.Label);
                if (!set)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    await new InfoDialog("Label Set Failed",
                        $"Could not set volume label to \"{entry.Label}\".\n" +
                        $"Win32 error: {err}\n\n" +
                        "Try running as Administrator, or ensure the drive is not read-only.\n\n" +
                        "No marker was written.")
                        .ShowDialog(this);
                    return;
                }
            }

            // ── Detect capacity ───────────────────────────────────────────────
            long capacityBytes = drive.TotalCapacityBytes;

            // ── Write (or overwrite) marker ───────────────────────────────────
            var nowIso    = DateTime.UtcNow.ToString("o");
            var markerJson = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    marker_type     = "arkadia_disk",
                    marker_version  = 1,
                    disk_id         = entry.Id,
                    disk_label      = entry.Label,
                    initialized_utc = nowIso,
                    capacity_bytes  = capacityBytes,
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(markerPath, markerJson);

            // ── Persist capacity into disk record ─────────────────────────────
            _catalog.UpdateDiskCapacity(entry.Id, capacityBytes);

            // ── Refresh UI ────────────────────────────────────────────────────
            RefreshDisks();
            var updated = _filteredDisks.FirstOrDefault(d => d.Id == entry.Id);
            if (updated is not null)
            {
                DisksList.SelectedItem = updated;
                UpdateDiskDetailPanel(updated);
            }

            await new InfoDialog("Disk Reinitialized",
                $"Disk \"{entry.Label}\" has been reinitialized.\n\n" +
                $"Drive:    {mountpoint}\n" +
                $"Capacity: {FormatBytes(capacityBytes)}\n" +
                $"Marker:   {markerPath}")
                .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Reinitialize Disk Error", ex.Message).ShowDialog(this);
        }
    }

    private async void OnMarkDiskLost(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = DisksList.SelectedItem as DiskEntry;
        if (entry is null)
        {
            await new InfoDialog("No Disk Selected",
                "Select a disk in the list before marking it lost.")
                .ShowDialog(this);
            return;
        }

        if (entry.Status == "lost")
        {
            await new InfoDialog("Already Lost",
                $"Disk \"{entry.Label}\" is already marked lost.")
                .ShowDialog(this);
            return;
        }

        var confirm = await new ConfirmDialog(
            "Mark Disk Lost",
            $"Mark disk \"{entry.Label}\" as LOST?\n\n" +
            "This will propagate as follows:\n" +
            "  \u2022 The disk is marked LOST\n" +
            "  \u2022 All volumes on the disk are marked LOST\n" +
            "  \u2022 All artifacts on those volumes are marked LOST\n" +
            "  \u2022 Present releases whose artifacts are now lost are marked LOST\n\n" +
            "LOST is a manual persistent state \u2014 it is NOT the same as\n" +
            "\"Disk Not Mounted\", which is a runtime-only observation.\n" +
            "This action cannot be undone from the UI at this time.")
            .ShowDialog<bool>(this);
        if (!confirm) return;

        try
        {
            var (volumeCount, artifactWork) = _catalog.MarkDiskLost(entry.Id);

            // Propagate artifact and release lost state into each affected DatLineStore.
            // This is a separate step per DB boundary — not part of the catalog transaction.
            var datLines = _catalog.LoadDatLines()
                .ToDictionary(dl => dl.Id, StringComparer.Ordinal);
            int totalArtifacts = 0, totalReleases = 0;
            foreach (var (datLineId, derivedIds) in artifactWork)
            {
                if (!datLines.TryGetValue(datLineId, out var dl) || dl.DataStorePath.Length == 0) continue;
                var dbPath = Path.Combine(_dataDir, dl.DataStorePath);
                if (!File.Exists(dbPath)) continue;
                var (artCount, relCount) = new Data.DatLineStore(dbPath).MarkArtifactsAndReleasesLost(derivedIds);
                totalArtifacts += artCount;
                totalReleases  += relCount;
            }

            RefreshDisks();
            RefreshVolumes();
            RebuildLibraryDatasets();

            await new InfoDialog("Disk Marked Lost",
                $"Disk \"{entry.Label}\" has been marked LOST.\n" +
                $"  {volumeCount} volume(s) marked LOST\n" +
                $"  {totalArtifacts} artifact(s) marked LOST\n" +
                $"  {totalReleases} release(s) marked LOST")
                .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Mark Lost Error", ex.Message).ShowDialog(this);
        }
    }

    private void UpdateDiskDetailPanel(DiskEntry? entry)
    {
        if (entry is null)
        {
            DiskDetailEmpty.IsVisible   = true;
            DiskDetailContent.IsVisible = false;
            return;
        }

        DiskDetailLabel.Text      = entry.Label;
        DiskDetailStatus.Text     = entry.StatusLabel;
        DiskDetailStatus.Foreground = entry.StatusBrush;
        DiskDetailCapacity.Text   = entry.CapacityLabel;
        DiskDetailUsed.Text       = entry.UsedLabel;
        DiskDetailFree.Text       = entry.FreeLabel;
        DiskDetailFilesystem.Text = entry.FilesystemLabel;
        DiskDetailBrand.Text      = string.IsNullOrEmpty(entry.Brand)  ? "—" : entry.Brand;
        DiskDetailModel.Text      = string.IsNullOrEmpty(entry.Model)  ? "—" : entry.Model;
        DiskDetailSerial.Text     = string.IsNullOrEmpty(entry.Serial) ? "—" : entry.Serial;

        // Segmented bar + volume list
        DiskVolumeList.Children.Clear();
        var volumes = _catalog.GetVolumesByDisk(entry.Id);
        BuildDiskSegmentBar(entry, volumes);

        foreach (var v in volumes)
        {
            var volColor = GetVolumeColor(v.Id);
            DiskVolumeList.Children.Add(new Grid
            {
                ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("Auto,*,Auto"),
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
                Children =
                {
                    new Border
                    {
                        [Grid.ColumnProperty] = 0,
                        Width             = 12,
                        Height            = 12,
                        CornerRadius      = new Avalonia.CornerRadius(2),
                        Background        = new SolidColorBrush(volColor),
                        BorderBrush       = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                        BorderThickness   = new Avalonia.Thickness(1),
                        Margin            = new Avalonia.Thickness(0, 0, 9, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1,
                        Text         = v.Label,
                        FontSize     = 12,
                        Foreground   = new SolidColorBrush(Color.Parse("#CCCCDD")),
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 2,
                        Text       = FormatBytes(v.ActualSizeBytes),
                        FontSize   = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#888899")),
                    },
                },
            });
        }

        if (volumes.Count == 0)
            DiskVolumeList.Children.Add(new TextBlock
            {
                Text = "No volumes assigned",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
            });

        DiskDetailEmpty.IsVisible   = false;
        DiskDetailContent.IsVisible = true;
    }

    private void BuildDiskSegmentBar(DiskEntry disk, List<Data.VolumeRecord> volumes)
    {
        // Replace the bar Border content with a horizontal stack of segments
        DiskSegmentBar.Child = null;
        if (disk.DeclaredCapacityBytes <= 0)
            return;

        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        const double BarWidth = 460.0; // detail panel is 500 - 40 margin

        foreach (var v in volumes)
        {
            if (v.ActualSizeBytes <= 0) continue;
            var ratio = Math.Clamp((double)v.ActualSizeBytes / disk.DeclaredCapacityBytes, 0, 1);
            var segW  = Math.Max(2, ratio * BarWidth);
            panel.Children.Add(new Border
            {
                Width      = segW,
                Height     = 17,
                Background = new SolidColorBrush(GetVolumeColor(v.Id)),
            });
        }

        // Free space segment
        var usedRatio = Math.Clamp((double)disk.UsedBytes / disk.DeclaredCapacityBytes, 0, 1);
        var freeW     = Math.Max(0, (1 - usedRatio) * BarWidth);
        if (freeW > 0)
            panel.Children.Add(new Border
            {
                Width      = freeW,
                Height     = 17,
                Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            });

        DiskSegmentBar.Child = panel;
    }

    private static readonly string[] VolumeColorPalette =
    [
        "#5C6BC0", "#26A69A", "#EF5350", "#FFA726", "#66BB6A",
        "#AB47BC", "#42A5F5", "#EC407A", "#8D6E63", "#78909C",
    ];

    /// <summary>
    /// Returns a stable color for a volume based on its ID using FNV-1a hashing.
    /// Same ID always maps to the same palette entry across refreshes.
    /// </summary>
    private static Color GetVolumeColor(string volumeId)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (var c in volumeId)
                h = (h ^ c) * 16777619u;
            return Color.Parse(VolumeColorPalette[h % (uint)VolumeColorPalette.Length]);
        }
    }

    // ── Volumes ───────────────────────────────────────────────────────────────

    private List<VolumeEntry> _allVolumeEntries = [];
    private List<VolumeEntry> _filteredVolumes  = [];

    private void InitVolumes() => RefreshVolumes();

    private void RefreshVolumes()
    {
        var disks    = _catalog.GetDisks().ToDictionary(d => d.Id, d => d.Label, StringComparer.Ordinal);
        var datLines = _catalog.LoadDatLines().ToDictionary(dl => dl.Id, dl => dl, StringComparer.Ordinal);

        _allVolumeEntries = _catalog.GetVolumes().Select(v =>
        {
            var loc = _catalog.GetCurrentLocation(v.Id);
            string locLabel = loc is null ? "—"
                : loc.LocationType == "disk" && loc.DiskId is not null && disks.TryGetValue(loc.DiskId, out var dl)
                    ? $"disk: {dl}"
                : LocTypeDisplay(loc.LocationType);

            datLines.TryGetValue(v.DatLineId, out var dlRecord);
            var dbPath = dlRecord?.DataStorePath.Length > 0
                ? Path.Combine(_dataDir, dlRecord.DataStorePath)
                : "";

            return new VolumeEntry
            {
                Id               = v.Id,
                Label            = v.Label,
                PlatformId       = v.PlatformId,
                DatLineId        = dlRecord?.Name ?? v.DatLineId,
                RawDatLineId     = v.DatLineId,
                DbPath           = dbPath,
                Status           = v.Status,
                Health           = v.Health,
                PlannedSizeBytes = v.PlannedSizeBytes,
                ActualSizeBytes  = v.ActualSizeBytes,
                CurrentLocation  = locLabel,
                DiskId           = loc?.DiskId,
                DiskLabel        = loc?.DiskId is not null && disks.TryGetValue(loc.DiskId, out var diskName)
                    ? diskName : null,
            };
        }).ToList();

        ApplyVolumesFilter();
    }

    private void ApplyVolumesFilter()
    {
        var search = VolumesSearchBox.Text?.Trim() ?? string.Empty;
        _filteredVolumes = search.Length == 0
            ? _allVolumeEntries
            : _allVolumeEntries
                .Where(v => v.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            v.DatLineId.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

        VolumesList.ItemsSource  = _filteredVolumes;
        VolumesCountText.Text    = _filteredVolumes.Count == _allVolumeEntries.Count
            ? $"{_allVolumeEntries.Count} volumes"
            : $"{_filteredVolumes.Count} of {_allVolumeEntries.Count} volumes";
        UpdateVolumeDetailPanel(null);
    }

    private void OnVolumesSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        => ApplyVolumesFilter();

    private void OnVolumeSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => UpdateVolumeDetailPanel(VolumesList.SelectedItem as VolumeEntry);

    private async void OnCreateVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var platforms = _catalog.LoadPlatforms();
        var datLines  = _catalog.LoadDatLines();
        var dialog    = new CreateVolumeDialog(platforms, datLines);
        dialog.FinishInit(platforms);
        var ok = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.Result is null) return;

        _catalog.SaveVolume(dialog.Result);
        RefreshVolumes();
    }

    private void UpdateVolumeDetailPanel(VolumeEntry? entry)
    {
        if (entry is null)
        {
            VolumeDetailEmpty.IsVisible   = true;
            VolumeDetailContent.IsVisible = false;
            return;
        }

        VolumeDetailLabel.Text        = entry.Label;
        VolumeDetailStatus.Text       = entry.StatusLabel;
        VolumeDetailStatus.Foreground = entry.StatusBrush;
        VolumeDetailPlatform.Text     = entry.PlatformId;
        VolumeDetailDatLine.Text      = entry.DatLineId;
        VolumeDetailPlanned.Text      = entry.PlannedLabel;
        VolumeDetailActual.Text       = entry.ActualLabel;
        VolumeDetailLocation.Text     = entry.CurrentLocation;
        VolumeDetailDisk.Text         = entry.DiskLabel ?? "—";

        // Archive artifacts assigned to this volume
        var assignments = _catalog.GetVolumeArtifacts(entry.Id);
        VolumeDetailArtifactCount.Text         = assignments.Count.ToString();
        VolumeDetailArtifactsBtn.IsEnabled     = assignments.Count > 0
            && entry.DbPath.Length > 0 && File.Exists(entry.DbPath);
        VolumeDetailRepairBtn.IsEnabled        = entry.Health == "crit"
            && entry.Status != "lost"
            && entry.DbPath.Length > 0 && File.Exists(entry.DbPath);

        VolumeDetailEmpty.IsVisible   = false;
        VolumeDetailContent.IsVisible = true;
    }

    private async void OnVolumeArtifactsDetails(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as Volumes.VolumeEntry;
        if (entry is null || entry.DbPath.Length == 0 || !File.Exists(entry.DbPath)) return;

        var assignments = _catalog.GetVolumeArtifacts(entry.Id);
        if (assignments.Count == 0) return;

        var store      = new DatLineStore(entry.DbPath);
        var daIds      = assignments.Select(va => va.DerivedArtifactId).ToList();
        var buildInfos = store.GetArtifactBuildInfos(daIds);

        await new VolumeArtifactsDialog(entry.Label, buildInfos).ShowDialog(this);
    }

    // ── Volume Repair ─────────────────────────────────────────────────────────

    private async void OnRepairVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as Volumes.VolumeEntry;
        if (entry is null || entry.Health != "crit" || entry.Status == "lost") return;
        if (entry.DbPath.Length == 0 || !File.Exists(entry.DbPath)) return;

        var appRoot      = AppContext.BaseDirectory;
        var platformId   = entry.PlatformId;
        var rawDatLineId = entry.RawDatLineId;

        // ── Resolve volume source path ─────────────────────────────────────
        string? volumeRoot = null;
        var wsRoot = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));
        if (Directory.Exists(wsRoot))
        {
            volumeRoot = wsRoot;
        }
        else if (entry.DiskId is not null)
        {
            var runtimeDisks = Data.DiskDiscoveryService.DiscoverAll()
                .Where(d => d.DiskId.Length > 0)
                .ToDictionary(d => d.DiskId, StringComparer.Ordinal);
            if (runtimeDisks.TryGetValue(entry.DiskId, out var rt))
            {
                var diskRoot = Path.Combine(rt.Mountpoint, SafeFileName(entry.Label));
                if (Directory.Exists(diskRoot))
                    volumeRoot = diskRoot;
            }
        }

        if (volumeRoot is null)
        {
            await new InfoDialog("Volume Not Accessible",
                $"Volume \"{entry.Label}\" could not be found in the workspace or on a mounted disk.\n\n" +
                "Mount the disk containing this volume, then try Repair again.")
                .ShowDialog(this);
            return;
        }

        // ── Storage strategy for ingest ────────────────────────────────────
        var datLine = _catalog.LoadDatLines().FirstOrDefault(dl => dl.Id == rawDatLineId);
        var storageStrategyId = datLine?.StorageStrategyId?.Length > 0
            ? datLine.StorageStrategyId : "none";

        // ── Identify repair targets (read-only volume scan) ────────────────
        var store       = new DatLineStore(entry.DbPath);
        var vaIds       = _catalog.GetVolumeArtifacts(entry.Id)
                                  .Select(va => va.DerivedArtifactId).ToList();
        var verifyInfos = store.GetArtifactVerifyInfos(vaIds);

        // Categorise every assigned artifact as OK, MISSING, or MISMATCH.
        var repairTargets = new List<Data.ArtifactVerifyInfo>();
        foreach (var vi in verifyInfos)
        {
            var absPath = Path.Combine(volumeRoot, SafeFileName(vi.ReleaseName), vi.FileName);
            if (!File.Exists(absPath))
            {
                repairTargets.Add(vi);
            }
            else if (vi.Sha1.Length > 0)
            {
                var actual = ComputeFileSha1(absPath);
                if (!string.Equals(actual, vi.Sha1, StringComparison.OrdinalIgnoreCase))
                    repairTargets.Add(vi);
            }
        }

        if (repairTargets.Count == 0)
        {
            await new InfoDialog("No Repair Targets",
                $"Volume \"{entry.Label}\" has no missing or mismatched files.\n\n" +
                "The volume may already be healthy. Run Verify to update its health status.")
                .ShowDialog(this);
            return;
        }

        // ── Check pre-existing archive / source availability ───────────────
        // daId → canonical local path (archive preferred, source fallback).
        var preAvailable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var vi in repairTargets)
        {
            var safe        = SafeFileName(vi.ReleaseName);
            var archivePath = Path.Combine(appRoot, "archive", platformId, rawDatLineId, safe, vi.FileName);
            var sourcePath  = Path.Combine(appRoot, "source",  platformId, rawDatLineId, safe, vi.FileName);
            if      (File.Exists(archivePath)) preAvailable[vi.DerivedArtifactId] = archivePath;
            else if (File.Exists(sourcePath))  preAvailable[vi.DerivedArtifactId] = sourcePath;
        }

        // ── Scan incoming-roms/<platform>/ for matches ─────────────────────
        // Only scan for targets that are not already available locally.
        var sha1ToTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vi in repairTargets)
        {
            if (preAvailable.ContainsKey(vi.DerivedArtifactId)) continue;
            if (vi.Sha1.Length > 0)
                sha1ToTarget[vi.Sha1] = vi.DerivedArtifactId;
        }

        var incomingMatches = new Dictionary<string, string>(StringComparer.Ordinal); // daId → file path
        if (sha1ToTarget.Count > 0)
        {
            var incomingDir = Path.Combine(appRoot, "incoming-roms", platformId);
            if (Directory.Exists(incomingDir))
            {
                foreach (var f in Directory.EnumerateFiles(incomingDir, "*", SearchOption.AllDirectories))
                {
                    if (incomingMatches.Count == sha1ToTarget.Count) break;
                    try
                    {
                        var fSha1 = ComputeFileSha1(f);
                        if (sha1ToTarget.TryGetValue(fSha1, out var daId) && !incomingMatches.ContainsKey(daId))
                            incomingMatches[daId] = f;
                    }
                    catch { /* unreadable — skip */ }
                }
            }
        }

        // ── Preview counts ─────────────────────────────────────────────────
        int totalTargets       = repairTargets.Count;
        int preAvailableCount  = preAvailable.Count;
        int incomingCount      = incomingMatches.Count;
        int recoverableNow     = preAvailableCount + incomingCount;
        int stillMissing       = totalTargets - recoverableNow;

        if (recoverableNow == 0)
        {
            await new InfoDialog("Nothing Recoverable",
                $"Volume \"{entry.Label}\" has {totalTargets} repair target(s), " +
                "but no matching files were found in the archive, source, or incoming-roms.\n\n" +
                $"Place the missing ROM files in:\n  incoming-roms/{platformId}/\n\nThen try Repair again.")
                .ShowDialog(this);
            return;
        }

        // ── Preview confirmation ───────────────────────────────────────────
        var locationType = volumeRoot.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase)
            ? "Local Archive"
            : entry.DiskId is not null ? $"Disk ({entry.DiskId})" : "External";

        var previewLines = new System.Text.StringBuilder();
        previewLines.AppendLine("── Volume Context ───────────────────────────────────");
        previewLines.AppendLine($"  Volume:               {entry.Label}");
        previewLines.AppendLine($"  Current health:       CRIT");
        previewLines.AppendLine($"  Location:             {locationType}");
        previewLines.AppendLine();
        previewLines.AppendLine("── Repair Targets ───────────────────────────────────");
        previewLines.AppendLine($"  Missing/Invalid:      {totalTargets}");
        previewLines.AppendLine($"  Already in archive:   {preAvailableCount}");
        previewLines.AppendLine($"  Found in incoming:    {incomingCount}");
        previewLines.AppendLine($"  ─────────────────────────────────────────────────");
        previewLines.AppendLine($"  Recoverable now:      {recoverableNow}");
        previewLines.AppendLine($"  Still unrecoverable:  {stillMissing}");
        if (stillMissing > 0)
        {
            previewLines.AppendLine();
            previewLines.AppendLine($"  {stillMissing} file(s) cannot be recovered in this pass.");
            previewLines.AppendLine($"  Add them to incoming-roms/{platformId}/ and run Repair again.");
        }
        previewLines.AppendLine();
        previewLines.Append("Cancel to abort — no changes will be made.");

        var confirmed = await new ConfirmDialog("Volume Repair", previewLines.ToString())
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        // ── Build neededSha1s for targeted ingest ──────────────────────────
        var neededSha1s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vi in repairTargets)
            if (vi.Sha1.Length > 0)
                neededSha1s.Add(vi.Sha1);

        // ── Open repair dialog, run work on background thread ──────────────
        var repairDialog = new DatLineVerifyDialog(entry.Label, platformId);
        var hdr = repairDialog.FindControl<Avalonia.Controls.TextBlock>("HeaderText");
        if (hdr is not null)
            hdr.Text = $"Volume Repair  —  Platform: {platformId}  —  Volume: {entry.Label}";

        var dlgTask = repairDialog.ShowDialog(this);

        await System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await RunVolumeRepairAsync(repairDialog, entry, store, volumeRoot,
                    platformId, rawDatLineId, storageStrategyId,
                    repairTargets, preAvailable, incomingMatches, neededSha1s);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => repairDialog.SetFailed(ex.Message));
            }
        });

        await dlgTask;
    }

    private async System.Threading.Tasks.Task RunVolumeRepairAsync(
        DatLineVerifyDialog                dialog,
        Volumes.VolumeEntry                entry,
        DatLineStore                       store,
        string                             volumeRoot,
        string                             platformId,
        string                             rawDatLineId,
        string                             storageStrategyId,
        List<Data.ArtifactVerifyInfo>      repairTargets,
        Dictionary<string, string>         preAvailable,    // daId → archive or source path
        Dictionary<string, string>         incomingMatches, // daId → incoming file path
        HashSet<string>                    neededSha1s)     // SHA1s of all repair targets
    {
        var appRoot        = AppContext.BaseDirectory;
        bool exportRepairLog = _catalog.GetBoolSetting("auto_export_repair_logs", defaultValue: true);
        var log            = new System.Text.StringBuilder();

        log.AppendLine($"Volume Repair — {entry.Label}");
        log.AppendLine($"Started:   {DateTime.UtcNow:o}");
        log.AppendLine($"Platform:  {platformId}");
        log.AppendLine();
        log.AppendLine("── Repair Targets ───────────────────────────────────────────");
        log.AppendLine($"  Volume:              {entry.Label}");
        log.AppendLine($"  Location:            {volumeRoot}");
        log.AppendLine($"  Missing/Invalid:     {repairTargets.Count}");
        log.AppendLine($"  Already in archive:  {preAvailable.Count}");
        log.AppendLine($"  Found in incoming:   {incomingMatches.Count}");
        log.AppendLine($"  Recoverable now:     {preAvailable.Count + incomingMatches.Count}");
        log.AppendLine();

        int totalTargets = repairTargets.Count;

        // ── INGEST PHASE ───────────────────────────────────────────────────
        if (incomingMatches.Count > 0)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                dialog.SetStatus($"Ingesting matched content from incoming-roms/{platformId}…");
                dialog.UpdateStats(totalTargets, 0, 0, totalTargets, 0, totalTargets);
            });

            var ingestProgress = new Progress<IngestionProgress>(p =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (p.PhaseText.Length > 0)
                        dialog.SetStatus($"Ingesting matched content: {p.PhaseText}");
                    if (p.NewOperation is not null)
                        dialog.AppendRow("INGEST", p.NewOperation.Action,
                            p.NewOperation.Object, p.NewOperation.Destination);
                }));

            var ingestResult = RunIngestionWork(
                platformId, rawDatLineId, entry.DbPath, storageStrategyId, ingestProgress,
                shouldIngest: sha1 => neededSha1s.Contains(sha1));

            log.AppendLine("── Ingest Summary ───────────────────────────────────────────");
            log.AppendLine($"  Scanned:  {ingestResult.FilesScanned}");
            log.AppendLine($"  Matched:  {ingestResult.FilesMatched}");
            log.AppendLine($"  Releases: {ingestResult.ReleasesPresent}");
            if (ingestResult.Error is not null)
                log.AppendLine($"  Error:    {ingestResult.Error}");
            log.AppendLine();
        }

        // ── POST-INGEST: Build full availability map ───────────────────────
        // Start from preAvailable and re-check archive + source for the rest.
        var available = new Dictionary<string, string>(preAvailable, StringComparer.Ordinal);
        foreach (var vi in repairTargets)
        {
            if (available.ContainsKey(vi.DerivedArtifactId)) continue;
            var safe        = SafeFileName(vi.ReleaseName);
            var archivePath = Path.Combine(appRoot, "archive", platformId, rawDatLineId, safe, vi.FileName);
            var sourcePath  = Path.Combine(appRoot, "source",  platformId, rawDatLineId, safe, vi.FileName);
            if      (File.Exists(archivePath)) available[vi.DerivedArtifactId] = archivePath;
            else if (File.Exists(sourcePath))  available[vi.DerivedArtifactId] = sourcePath;
        }

        int availableCount   = available.Count;
        int unavailableCount = totalTargets - availableCount;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            dialog.SetStatus("Checking derived availability…"));

        log.AppendLine("── Derived Availability ─────────────────────────────────────");
        log.AppendLine($"  Available in archive/source: {availableCount}");
        log.AppendLine($"  Still unavailable:           {unavailableCount}");
        log.AppendLine();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            dialog.SetStatus($"Reintegrating content into volume {entry.Label}…");
            dialog.UpdateStats(totalTargets, 0, unavailableCount, totalTargets, 0, unavailableCount);
        });

        // ── FREE SPACE CHECK ───────────────────────────────────────────────
        long bytesNeeded = repairTargets
            .Where(vi => available.ContainsKey(vi.DerivedArtifactId) && vi.SizeBytes > 0)
            .Sum(vi => vi.SizeBytes) + 64L * 1024 * 1024; // 64 MB buffer

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(volumeRoot))!);
            if (drive.AvailableFreeSpace < bytesNeeded)
            {
                var msg = $"Insufficient space on the volume drive.\n" +
                          $"Required: {FormatBytes(bytesNeeded)}  " +
                          $"Available: {FormatBytes(drive.AvailableFreeSpace)}";
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => dialog.SetFailed(msg));
                log.AppendLine($"ABORTED (space): {msg}");
                WriteRepairLog(appRoot, entry.Label, log, exportRepairLog);
                return;
            }
        }
        catch { /* DriveInfo unavailable — copy will fail naturally */ }

        // ── REINTEGRATION PHASE ────────────────────────────────────────────
        log.AppendLine("── Reintegration Summary ────────────────────────────────────");
        var reintegratedDaIds = new HashSet<string>(StringComparer.Ordinal);
        var skippedDaIds      = new List<string>();

        foreach (var vi in repairTargets)
        {
            var safe     = SafeFileName(vi.ReleaseName);
            var dispPath = $"{safe}/{vi.FileName}";
            var dstPath  = Path.Combine(volumeRoot, safe, vi.FileName);

            if (!available.TryGetValue(vi.DerivedArtifactId, out var srcPath))
            {
                skippedDaIds.Add(vi.DerivedArtifactId);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.AppendRow(entry.Label, "SKIPPED", dispPath, "no source available"));
                log.AppendLine($"  SKIPPED  {dispPath}");
                continue;
            }

            bool copied = false;
            string? copyError = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                File.Copy(srcPath, dstPath, overwrite: true);
                copied = true;
            }
            catch (Exception ex) { copyError = ex.Message; }

            if (copied)
            {
                reintegratedDaIds.Add(vi.DerivedArtifactId);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.AppendRow(entry.Label, "COPIED", dispPath, ""));
                log.AppendLine($"  COPIED  {dispPath}");
            }
            else
            {
                skippedDaIds.Add(vi.DerivedArtifactId);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.AppendRow(entry.Label, "COPY FAILED", dispPath, $"error: {copyError}"));
                log.AppendLine($"  COPY FAILED  {dispPath}  error={copyError}");
            }
        }

        // ── VERIFY REINTEGRATED FILES ──────────────────────────────────────
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            dialog.SetStatus($"Verifying repaired files ({reintegratedDaIds.Count} reintegrated)…"));

        log.AppendLine();
        log.AppendLine("── Verify Summary ───────────────────────────────────────────");
        var verifiedDaIds = new List<string>();
        var failedDaIds   = new List<string>();

        foreach (var vi in repairTargets)
        {
            if (!reintegratedDaIds.Contains(vi.DerivedArtifactId)) continue;

            var safe     = SafeFileName(vi.ReleaseName);
            var dispPath = $"{safe}/{vi.FileName}";
            var dstPath  = Path.Combine(volumeRoot, safe, vi.FileName);

            if (!File.Exists(dstPath))
            {
                failedDaIds.Add(vi.DerivedArtifactId);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.AppendRow(entry.Label, "MISSING", dispPath, "absent after copy"));
                log.AppendLine($"  MISSING  {dispPath}");
                continue;
            }

            if (vi.Sha1.Length > 0)
            {
                var actual = ComputeFileSha1(dstPath);
                if (!string.Equals(actual, vi.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    failedDaIds.Add(vi.DerivedArtifactId);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(entry.Label, "MISMATCH", dispPath,
                            $"exp:{vi.Sha1[..8]}… got:{actual[..8]}…"));
                    log.AppendLine($"  MISMATCH  {dispPath}  expected={vi.Sha1}  actual={actual}");
                    continue;
                }
            }

            verifiedDaIds.Add(vi.DerivedArtifactId);
            long fSize = 0;
            try { fSize = new FileInfo(dstPath).Length; } catch { }
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.AppendRow(entry.Label, "VERIFIED", dispPath, FormatBytes(fSize)));
            log.AppendLine($"  VERIFIED  {dispPath}");
        }

        // ── STATE UPDATES ──────────────────────────────────────────────────
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            dialog.SetStatus("Applying state updates…"));

        var missedDaIds = new List<string>(skippedDaIds.Count + failedDaIds.Count);
        missedDaIds.AddRange(skippedDaIds);
        missedDaIds.AddRange(failedDaIds);

        int artPresent = store.BatchUpdateDerivedArtifactStatus(verifiedDaIds, "present");
        int artMissing = store.BatchUpdateDerivedArtifactStatus(missedDaIds,   "missing");

        var allChanged = new List<string>(verifiedDaIds.Count + missedDaIds.Count);
        allChanged.AddRange(verifiedDaIds);
        allChanged.AddRange(missedDaIds);
        int relUpdated = store.RecalculateReleaseStatusForArtifacts(allChanged);

        // Health: ok only if every repair target was reintegrated and verified.
        int remainingIssues = missedDaIds.Count;
        var newHealth = remainingIssues == 0 ? "ok" : "crit";
        _catalog.UpdateVolumeHealth(entry.Id, newHealth);

        log.AppendLine();
        log.AppendLine("── Apply Summary ────────────────────────────────────────────");
        log.AppendLine($"  Artifacts → present:   {artPresent}");
        log.AppendLine($"  Artifacts → missing:   {artMissing}");
        log.AppendLine($"  Releases recalculated: {relUpdated}");
        log.AppendLine();
        log.AppendLine("── Final Volume Health ──────────────────────────────────────");
        log.AppendLine($"  Volume:  {entry.Label}");
        log.AppendLine($"  Health:  {newHealth.ToUpper()}");
        if (remainingIssues == 0)
            log.AppendLine("  Result:  Repair complete — all targets recovered and verified.");
        else
            log.AppendLine($"  Result:  Repair incomplete — {remainingIssues} target(s) still missing or invalid.");
        log.AppendLine();
        log.AppendLine($"  Completed:       {DateTime.UtcNow:o}");
        log.AppendLine($"  Targets:         {totalTargets}");
        log.AppendLine($"  Reintegrated:    {reintegratedDaIds.Count}");
        log.AppendLine($"  Verified:        {verifiedDaIds.Count}");
        log.AppendLine($"  Failed/Skipped:  {failedDaIds.Count + skippedDaIds.Count}");

        // ── WRITE LOG ──────────────────────────────────────────────────────
        WriteRepairLog(appRoot, entry.Label, log, exportRepairLog);

        // ── UI REFRESH ─────────────────────────────────────────────────────
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            RebuildLibraryDatasets();
            RefreshVolumes();
            dialog.UpdateStats(
                totalTargets, verifiedDaIds.Count, skippedDaIds.Count,
                totalTargets, verifiedDaIds.Count, missedDaIds.Count);
        });

        // ── FINAL STATUS ───────────────────────────────────────────────────
        string summary;
        if (remainingIssues == 0)
        {
            summary =
                $"Volume Repair complete — {entry.Label}\n" +
                $"Targets requested:     {totalTargets}\n" +
                $"Files reintegrated:    {reintegratedDaIds.Count}\n" +
                $"Files verified:        {verifiedDaIds.Count}\n" +
                $"Artifacts → present:   {artPresent}  |  Releases recalculated: {relUpdated}\n" +
                $"Volume health:         OK — Volume is now healthy.";
        }
        else
        {
            summary =
                $"Volume Repair complete (partial) — {entry.Label}\n" +
                $"Targets requested:     {totalTargets}\n" +
                $"Recovered from incoming: {incomingMatches.Count}\n" +
                $"Derived available:     {availableCount} of {totalTargets}\n" +
                $"Files reintegrated:    {reintegratedDaIds.Count}\n" +
                $"Files verified:        {verifiedDaIds.Count}\n" +
                $"Artifacts → present:   {artPresent}  |  Artifacts still missing: {artMissing}  |  Releases recalculated: {relUpdated}\n" +
                $"Volume health:         CRIT — {remainingIssues} target(s) still missing or invalid.";
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            dialog.SetStatus(
                $"Done — Targets: {totalTargets}  Reintegrated: {reintegratedDaIds.Count}  " +
                $"Verified: {verifiedDaIds.Count}  Remaining: {remainingIssues}  " +
                $"Health: {newHealth.ToUpper()}");
            dialog.SetCompleted(summary);
        });
    }

    private static void WriteRepairLog(string appRoot, string volumeLabel, System.Text.StringBuilder log, bool enabled)
    {
        if (!enabled) return;
        try
        {
            var logDir  = Path.Combine(appRoot, "logs", "repair");
            Directory.CreateDirectory(logDir);
            var ts      = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safe    = SafeFileName(volumeLabel);
            var logFile = Path.Combine(logDir, $"{ts}-repair-{safe}.log");
            File.WriteAllText(logFile, log.ToString());
        }
        catch { /* non-fatal */ }
    }

    // ── Copy-to-clipboard helpers ─────────────────────────────────────────────

    private System.Timers.Timer? _toastTimer;

    private void CopyAndToast(string clipboardText)
    {
        if (clipboardText.Length == 0 || clipboardText == "—") return;
        // Show toast immediately on UI thread.
        _toastTimer?.Stop();
        _toastTimer?.Dispose();
        var display = clipboardText.Length > 52 ? clipboardText[..52] + "…" : clipboardText;
        CopyToastText.Text        = $"Copied: {display}";
        CopyToastBorder.IsVisible = true;
        _toastTimer = new System.Timers.Timer(2000) { AutoReset = false };
        _toastTimer.Elapsed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => CopyToastBorder.IsVisible = false);
        _toastTimer.Start();
        // Write to clipboard asynchronously.
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(clipboardText);
        });
    }

    /// <summary>
    /// Wires hover highlight and left-click copy on <paramref name="tb"/>.
    /// No-op when <paramref name="onCopy"/> is null or <paramref name="clipboardValue"/> is absent.
    /// </summary>
    private static void MakeCopyable(
        TextBlock tb, string clipboardValue, SolidColorBrush normalColor, Action<string>? onCopy)
    {
        if (onCopy is null || clipboardValue.Length == 0 || clipboardValue == "—") return;
        tb.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        var hover = BrightenBrush(normalColor);
        tb.PointerEntered += (_, _) => tb.Foreground = hover;
        tb.PointerExited  += (_, _) => tb.Foreground = normalColor;
        tb.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
                onCopy(clipboardValue);
        };
    }

    private static SolidColorBrush BrightenBrush(SolidColorBrush b)
    {
        var c = b.Color;
        static byte Up(byte x) => (byte)Math.Min(255, x + 40);
        return new SolidColorBrush(new Color(c.A, Up(c.R), Up(c.G), Up(c.B)));
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>Streams the file and returns its SHA1 hex string (lowercase).</summary>
    private static string ComputeFileSha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Streams the file once and returns (SHA1, MD5, CRC32) hex strings (all lowercase).
    /// CRC32 uses the standard ISO 3309 / IEEE 802.3 polynomial — the same used by ZIP, Ethernet,
    /// and ROM preservation DATs (No-Intro, Redump, MAME).
    /// </summary>
    private static (string Sha1, string Md5, string Crc32) ComputeSourceHashes(string path)
    {
        using var fs   = File.OpenRead(path);
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        using var md5a = System.Security.Cryptography.MD5.Create();

        uint crc = 0xFFFFFFFF;
        var  buf = new byte[81920];
        int  read;

        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            sha1.TransformBlock(buf, 0, read, null, 0);
            md5a.TransformBlock(buf, 0, read, null, 0);
            for (int i = 0; i < read; i++)
            {
                crc ^= buf[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1u)));
            }
        }
        sha1.TransformFinalBlock([], 0, 0);
        md5a.TransformFinalBlock([], 0, 0);
        crc ^= 0xFFFFFFFF;

        return (
            Convert.ToHexString(sha1.Hash!).ToLowerInvariant(),
            Convert.ToHexString(md5a.Hash!).ToLowerInvariant(),
            crc.ToString("x8")
        );
    }

    /// <summary>
    /// Copy→verify→cleanup-source engine used by Move Volume.
    /// Opens and drives WriteVolumeToDiskDialog; returns when the dialog is closed by the user.
    /// Free space check, destination-exists guard, and log writing are all handled internally.
    /// Returns (Success=false, ...) when any pre-flight check fails (InfoDialog already shown).
    /// </summary>
    private async Task<(bool Success, int FileCount, long CopiedBytes, string? CleanupError, TimeSpan Elapsed)>
        RunCopyMoveAsync(
            string operationTitle,
            string srcFolder,
            string dstFolder,
            string dialogHeader,
            string logSubdir,
            string logLabel)
    {
        // ── Pre-enumerate source files ────────────────────────────────────
        var files = Directory
            .EnumerateFiles(srcFolder, "*", SearchOption.AllDirectories)
            .Select(f =>
            {
                var rel  = Path.GetRelativePath(srcFolder, f);
                var dst  = Path.Combine(dstFolder, rel);
                var size = new FileInfo(f).Length;
                return (SrcPath: f, DstPath: dst, RelPath: rel, Size: size);
            })
            .ToList();

        if (files.Count == 0)
        {
            await new InfoDialog("Empty Volume Folder",
                $"No files found in:\n{srcFolder}")
                .ShowDialog(this);
            return (false, 0, 0, null, TimeSpan.Zero);
        }

        // ── Destination must not already exist ────────────────────────────
        if (Directory.Exists(dstFolder))
        {
            await new InfoDialog("Destination Already Exists",
                $"The destination folder already exists:\n{dstFolder}\n\n" +
                "Remove it manually before proceeding.")
                .ShowDialog(this);
            return (false, 0, 0, null, TimeSpan.Zero);
        }

        // ── Free space check ──────────────────────────────────────────────
        long totalBytes = files.Sum(x => x.Size);
        try
        {
            var dstDrive = new DriveInfo(Path.GetPathRoot(dstFolder)!);
            if (totalBytes > dstDrive.AvailableFreeSpace)
            {
                await new InfoDialog("Insufficient Space",
                    $"Required: {FormatBytes(totalBytes)}\n" +
                    $"Available: {FormatBytes(dstDrive.AvailableFreeSpace)}\n\n" +
                    "Free up space on the destination and try again.")
                    .ShowDialog(this);
                return (false, 0, 0, null, TimeSpan.Zero);
            }
        }
        catch { /* DriveInfo unavailable — copy will fail naturally if space is truly exhausted */ }

        // ── Log setup ─────────────────────────────────────────────────────
        bool logEnabled = _catalog.GetBoolSetting("log_on_copy", true);
        System.Text.StringBuilder? log = logEnabled ? new System.Text.StringBuilder() : null;
        var startTime = DateTime.UtcNow;

        if (log is not null)
        {
            log.AppendLine(operationTitle);
            log.AppendLine($"Started:     {startTime:o}");
            log.AppendLine($"Source:      {srcFolder}");
            log.AppendLine($"Destination: {dstFolder}");
            log.AppendLine($"Files:       {files.Count}");
            log.AppendLine($"Bytes:       {totalBytes}");
            log.AppendLine();
        }

        // ── Open progress dialog ──────────────────────────────────────────
        var progDialog = new WriteVolumeToDiskDialog(dialogHeader, totalBytes, files.Count);
        var dlgTask    = progDialog.ShowDialog<bool>(this);

        // ── Copy + verify on background thread ────────────────────────────
        string? errorMessage  = null;
        long copiedBytes = 0, verifiedBytes = 0;
        int  filesProcessed   = 0;

        try
        {
            await Task.Run(async () =>
            {
                // Copy phase
                foreach (var (srcPath, dstPath, relPath, size) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                    File.Copy(srcPath, dstPath, overwrite: false);
                    copiedBytes    += size;
                    filesProcessed++;
                    var elapsed    = DateTime.UtcNow - startTime;
                    var sizeLabel  = FormatBytes(size);
                    log?.AppendLine($"COPY   {relPath}  ({sizeLabel})");
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progDialog.AppendRow("copy", relPath, sizeLabel);
                        progDialog.UpdateStats(copiedBytes, verifiedBytes, filesProcessed, elapsed);
                    });
                }

                // Verify phase — Phase 1: fast precheck (existence + size)
                foreach (var (srcPath, dstPath, relPath, size) in files)
                {
                    var info = new FileInfo(dstPath);
                    if (!info.Exists)
                        throw new InvalidOperationException($"Verify failed — file missing: {relPath}");
                    if (info.Length != size)
                        throw new InvalidOperationException(
                            $"Verify failed — size mismatch: {relPath} " +
                            $"(expected {size}, got {info.Length})");
                }
                if (files.Count != Directory
                        .EnumerateFiles(dstFolder, "*", SearchOption.AllDirectories).Count())
                    throw new InvalidOperationException(
                        "Verify failed — destination file count does not match source file count.");

                // Verify phase — Phase 2: SHA1 authoritative verification
                log?.AppendLine();
                log?.AppendLine("Verify (SHA1):");
                foreach (var (srcPath, dstPath, relPath, size) in files)
                {
                    var sizeLabel = FormatBytes(size);
                    log?.AppendLine($"VERIFY {relPath}  ({sizeLabel})");

                    var srcSha1 = ComputeFileSha1(srcPath);
                    var dstSha1 = ComputeFileSha1(dstPath);

                    if (!string.Equals(srcSha1, dstSha1, StringComparison.OrdinalIgnoreCase))
                    {
                        log?.AppendLine($"VERIFY FAILED  src={srcSha1}  dst={dstSha1}");
                        throw new InvalidOperationException(
                            $"Verify failed — SHA1 mismatch: {relPath}\n" +
                            $"  src: {srcSha1}\n" +
                            $"  dst: {dstSha1}");
                    }

                    log?.AppendLine($"VERIFY OK  sha1={dstSha1}");
                    verifiedBytes += size;
                    var elapsed   = DateTime.UtcNow - startTime;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progDialog.AppendRow("verify", relPath, sizeLabel);
                        progDialog.UpdateStats(copiedBytes, verifiedBytes, filesProcessed, elapsed);
                    });
                }
            });
        }
        catch (Exception ex) { errorMessage = ex.Message; }

        // ── Cleanup source ONLY after successful verify ───────────────────
        string? cleanupError = null;
        if (errorMessage is null)
        {
            try
            {
                Directory.Delete(srcFolder, recursive: true);
                log?.AppendLine();
                log?.AppendLine($"Cleanup:     OK");
                log?.AppendLine($"Removed:     {srcFolder}");
            }
            catch (Exception ex)
            {
                cleanupError = ex.Message;
                log?.AppendLine();
                log?.AppendLine($"Cleanup:     FAILED");
                log?.AppendLine($"Target:      {srcFolder}");
                log?.AppendLine($"Error:       {cleanupError}");
            }
        }

        // ── Write log ─────────────────────────────────────────────────────
        if (log is not null)
        {
            var endTime = DateTime.UtcNow;
            log.AppendLine();
            log.AppendLine($"Completed:   {endTime:o}");
            log.AppendLine($"Duration:    {(endTime - startTime).TotalSeconds:F1}s");
            log.AppendLine($"Result:      {(errorMessage is null ? "OK (SHA1 verified)" : "FAILED")}");
            if (errorMessage is not null)
                log.AppendLine($"Error:       {errorMessage}");
            try
            {
                var logDir  = Path.Combine(AppContext.BaseDirectory, "logs", logSubdir);
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, $"{startTime:yyyyMMdd-HHmmss}-{logSubdir}-{logLabel}.log");
                File.WriteAllText(logFile, log.ToString());
            }
            catch { /* non-fatal */ }
        }

        // ── Finalize dialog ───────────────────────────────────────────────
        if (errorMessage is null)
        {
            var completionText = cleanupError is null
                ? $"Completed — {files.Count} file(s) copied, verified, and source removed."
                : $"Completed — {files.Count} file(s) copied and verified. " +
                  $"Source cleanup failed: {cleanupError}";
            progDialog.SetCompleted(files.Count, copiedBytes, dstFolder, completionText);
        }
        else
            progDialog.SetFailed(errorMessage);

        await dlgTask;

        return (errorMessage is null, files.Count, copiedBytes, cleanupError, DateTime.UtcNow - startTime);
    }

    /// <summary>
    /// Runs the Apply step (DB insert + recalculate + refresh) without any dialog.
    /// Caller is responsible for preconditions and confirmation.
    /// </summary>
    private System.Threading.Tasks.Task<(int ReleaseCount, int LinkedCount)> ApplyPlanCore(
        VolumeEntry entry, Data.PlanningResult planResult, DatLineStore store)
    {
        var included           = planResult.Items.Where(d => d.Decision == "include").ToList();
        var includedReleaseIds = included.Select(d => d.ReleaseId).ToList();
        var derivedByRelease   = store.GetDerivedArtifactIdsForReleases(includedReleaseIds);

        var now   = DateTime.UtcNow;
        var batch = new List<Data.VolumeArtifactRecord>();
        foreach (var releaseId in includedReleaseIds)
        {
            if (!derivedByRelease.TryGetValue(releaseId, out var daItems)) continue;
            foreach (var (daId, ck) in daItems)
            {
                batch.Add(new Data.VolumeArtifactRecord
                {
                    Id                 = Guid.NewGuid().ToString("N"),
                    VolumeId           = entry.Id,
                    DatLineId          = entry.RawDatLineId,
                    DerivedArtifactId  = daId,
                    ContentIdentityKey = ck,
                    Status             = "present_in_final",
                    AddedAtUtc         = now,
                });
            }
        }

        int linkedCount  = _catalog.SaveVolumeArtifactsBatch(batch);
        var allDerived   = store.GetDerivedArtifacts();
        var sizeByDrvId  = allDerived.ToDictionary(d => d.Id, d => d.DerivedSizeBytes, StringComparer.Ordinal);
        _catalog.RecalculateVolumeActualSize(entry.Id, sizeByDrvId);

        RefreshVolumes();
        RefreshDisks();

        var updated = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updated is not null)
        {
            VolumesList.SelectedItem = updated;
            UpdateVolumeDetailPanel(updated);
        }

        return System.Threading.Tasks.Task.FromResult((included.Count, linkedCount));
    }

    /// <summary>
    /// Runs the Build step (pre-scan + move + location record + refresh) without any dialog.
    /// Throws on inconsistency. Returns movedCount=0 if already built.
    /// Caller is responsible for preconditions.
    /// </summary>
    private System.Threading.Tasks.Task<(int MovedCount, int AlreadyBuiltCount, int TotalCount, string VolumeFolder)> BuildVolumeCore(
        VolumeEntry entry)
    {
        var volumeArtifacts = _catalog.GetVolumeArtifacts(entry.Id);
        if (volumeArtifacts.Count == 0)
            throw new InvalidOperationException(
                $"Volume \"{entry.Label}\" has no derived artifacts assigned.");

        var store      = new Data.DatLineStore(entry.DbPath);
        var daIds      = volumeArtifacts.Select(va => va.DerivedArtifactId).ToList();
        var buildInfos = store.GetArtifactBuildInfos(daIds);

        if (buildInfos.Count == 0)
            throw new InvalidOperationException(
                "Could not resolve release/file info for any assigned artifact.");

        var appRoot      = AppContext.BaseDirectory;
        var volumeFolder = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));

        var notBuilt     = new List<(Data.ArtifactBuildInfo Info, string Src, string Dst)>();
        var alreadyBuilt = new List<Data.ArtifactBuildInfo>();
        var inconsistent = new List<(Data.ArtifactBuildInfo Info, string Reason)>();

        foreach (var info in buildInfos)
        {
            var src = Path.Combine(appRoot,
                info.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(volumeFolder,
                SafeFileName(info.ReleaseName), info.FileName);

            bool srcExists = File.Exists(src);
            bool dstExists = File.Exists(dst);

            if (srcExists && dstExists)
                inconsistent.Add((info,
                    $"both source and destination exist (source was not moved): {info.RelativePath}"));
            else if (dstExists)
                alreadyBuilt.Add(info);
            else if (srcExists)
                notBuilt.Add((info, src, dst));
            else
                inconsistent.Add((info,
                    $"archive source missing, not yet built: {info.RelativePath}"));
        }

        if (inconsistent.Count > 0)
        {
            const int maxExamples = 5;
            var lines   = inconsistent.Take(maxExamples).Select(x => $"  {x.Info.FileName}: {x.Reason}");
            var trailer = inconsistent.Count > maxExamples
                ? $"\n  …and {inconsistent.Count - maxExamples} more." : "";
            throw new InvalidOperationException(
                $"{inconsistent.Count} artifact(s) in inconsistent state:\n\n" +
                string.Join("\n", lines) + trailer);
        }

        if (notBuilt.Count == 0)
            return System.Threading.Tasks.Task.FromResult((0, alreadyBuilt.Count, buildInfos.Count, volumeFolder));

        int movedCount = 0;
        foreach (var (info, src, dst) in notBuilt)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Move(src, dst);
            movedCount++;
        }

        _catalog.SetCurrentLocation(new Data.VolumeLocationRecord
        {
            Id           = Guid.NewGuid().ToString("N"),
            VolumeId     = entry.Id,
            LocationType = "workspace",
            DiskId       = null,
            Path         = volumeFolder,
            IsCurrent    = true,
            CreatedAt    = DateTime.UtcNow,
        });

        RefreshVolumes();
        RefreshDisks();

        var updated = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updated is not null)
        {
            VolumesList.SelectedItem = updated;
            UpdateVolumeDetailPanel(updated);
        }

        return System.Threading.Tasks.Task.FromResult((movedCount, alreadyBuilt.Count, buildInfos.Count, volumeFolder));
    }

    // ── Make Volume ───────────────────────────────────────────────────────────

    private async void OnMakeVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;

        try
        {
            // ── Preconditions ─────────────────────────────────────────────────
            if (entry.DbPath.Length == 0 || !File.Exists(entry.DbPath))
            {
                await new InfoDialog(
                    "No DAT Line DB",
                    $"Volume \"{entry.Label}\" has no DAT line database on disk.\n\n" +
                    "Import the DAT line first.")
                    .ShowDialog(this);
                return;
            }

            if (entry.PlannedSizeBytes <= 0)
            {
                await new InfoDialog(
                    "No Capacity Set",
                    $"Volume \"{entry.Label}\" has no planned capacity.\n\n" +
                    "Set the volume capacity before making.")
                    .ShowDialog(this);
                return;
            }

            var store       = new DatLineStore(entry.DbPath);
            var assignedIds = _catalog.GetAssignedDerivedIdsByDatLine(entry.RawDatLineId);
            var candidates  = store.GetPlanningCandidates(AppContext.BaseDirectory, assignedIds);

            if (candidates.Count == 0)
            {
                await new InfoDialog(
                    "No Planning Candidates",
                    $"No archive-complete releases found for DAT line \"{entry.DatLineId}\".\n\n" +
                    "Run ingestion on this DAT line first.")
                    .ShowDialog(this);
                return;
            }

            // ── Plan ──────────────────────────────────────────────────────────
            var planResult = Data.VolumePlanner.Plan(
                entry.PlannedSizeBytes,
                entry.ActualSizeBytes,
                candidates);

            var included = planResult.Items.Where(d => d.Decision == "include").ToList();

            if (included.Count == 0)
            {
                await new InfoDialog(
                    "Nothing to Include",
                    "The plan has no releases to include.\n\n" +
                    "All candidates were already assigned, archive-incomplete, or exceed remaining capacity.")
                    .ShowDialog(this);
                return;
            }

            // ── Preview ───────────────────────────────────────────────────────
            var preview = new PlanVolumeDialog(planResult);
            (preview.FindControl<Avalonia.Controls.TextBlock>("DialogTitle"))!.Text =
                $"Make Volume — {entry.Label}";
            var proceed = await preview.ShowDialog<bool>(this);
            if (!proceed) return;

            // ── Apply ─────────────────────────────────────────────────────────
            var (releaseCount, linkedCount) = await ApplyPlanCore(entry, planResult, store);

            // ── Build ─────────────────────────────────────────────────────────
            var (movedCount, alreadyBuiltCount, totalCount, volumeFolder) =
                await BuildVolumeCore(entry);

            // ── Done ──────────────────────────────────────────────────────────
            var buildNote = movedCount == 0
                ? $"Build:          already complete ({alreadyBuiltCount} file(s) present)"
                : $"Files moved:    {movedCount}  (already built: {alreadyBuiltCount})";

            await new InfoDialog(
                "Make Volume — Complete",
                $"Volume:         {entry.Label}\n" +
                $"Releases:       {releaseCount}\n" +
                $"Artifacts:      {linkedCount}\n" +
                buildNote + "\n" +
                $"Destination:    {volumeFolder}")
                .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Make Volume Error", ex.Message).ShowDialog(this);
        }
    }

    private async void OnMoveVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;

        try
        {
            var appRoot         = AppContext.BaseDirectory;
            var workspaceFolder = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));

            // ── Resolve source (workspace first, then disk) ───────────────────
            string  srcFolder;
            bool    srcIsWorkspace;
            string? srcDiskId       = null;
            string? srcMountpoint   = null;

            if (Directory.Exists(workspaceFolder))
            {
                srcFolder      = workspaceFolder;
                srcIsWorkspace = true;
            }
            else if (entry.DiskId is not null)
            {
                var rtForSrc = DiskDiscoveryService.DiscoverAll();
                var srcDisk  = rtForSrc.FirstOrDefault(d =>
                    d.HasMarker &&
                    string.Equals(d.DiskId, entry.DiskId, StringComparison.Ordinal));

                if (srcDisk is null)
                {
                    await new InfoDialog("Source Not Found",
                        $"Volume \"{entry.Label}\" is not in the Local Archive and its associated disk " +
                        $"(\"{entry.DiskLabel}\", ID: {entry.DiskId}) is not currently mounted.\n\n" +
                        "Connect the source disk and try again.")
                        .ShowDialog(this);
                    return;
                }

                srcFolder = Path.Combine(srcDisk.Mountpoint, SafeFileName(entry.Label));
                if (!Directory.Exists(srcFolder))
                {
                    await new InfoDialog("Volume Not Found on Source Disk",
                        $"Expected the volume folder at:\n{srcFolder}\n\n" +
                        "The folder was not found on the disk.")
                        .ShowDialog(this);
                    return;
                }

                srcIsWorkspace = false;
                srcDiskId      = srcDisk.DiskId;
                srcMountpoint  = srcDisk.Mountpoint;
            }
            else
            {
                await new InfoDialog("Volume Not Found",
                    $"Volume \"{entry.Label}\" is not present in the Local Archive " +
                    "and has no associated disk.")
                    .ShowDialog(this);
                return;
            }

            // ── Calculate required bytes from resolved source ─────────────────
            long requiredBytes = 0;
            foreach (var f in Directory.EnumerateFiles(srcFolder, "*", SearchOption.AllDirectories))
                requiredBytes += new FileInfo(f).Length;

            // ── Build destination list ────────────────────────────────────────
            var catalogDisks    = _catalog.GetDisks();
            var runtimeDisks    = DiskDiscoveryService.DiscoverAll();
            var runtimeByDiskId = runtimeDisks
                .Where(d => d.HasMarker)
                .ToDictionary(d => d.DiskId);

            var destinations = new System.Collections.Generic.List<Volumes.VolumeDestination>();

            // Workspace destination — only when source is NOT workspace
            if (!srcIsWorkspace)
            {
                try
                {
                    var wsDrive = new DriveInfo(Path.GetPathRoot(appRoot)!);
                    var wsState = wsDrive.AvailableFreeSpace >= requiredBytes
                        ? Volumes.DestinationState.Ready
                        : Volumes.DestinationState.NotEnoughFreeSpace;
                    destinations.Add(new Volumes.VolumeDestination
                    {
                        DisplayName        = $"Local Archive  ({appRoot})",
                        DestinationType    = Volumes.DestinationType.Workspace,
                        DiskId             = null,
                        DiskLabel          = null,
                        TotalCapacityBytes = wsDrive.TotalSize,
                        FreeSpaceBytes     = wsDrive.AvailableFreeSpace,
                        RequiredBytes      = requiredBytes,
                        State              = wsState,
                        Mountpoint         = appRoot,
                    });
                }
                catch { /* non-fatal */ }
            }

            // Catalog disk destinations — skip source disk
            foreach (var disk in catalogDisks)
            {
                if (srcDiskId is not null && disk.Id == srcDiskId) continue;

                if (runtimeByDiskId.TryGetValue(disk.Id, out var rt))
                {
                    var state = rt.FreeSpaceBytes >= requiredBytes
                        ? Volumes.DestinationState.Ready
                        : Volumes.DestinationState.NotEnoughFreeSpace;
                    destinations.Add(new Volumes.VolumeDestination
                    {
                        DisplayName        = $"{disk.Label}  ({rt.Mountpoint})",
                        DestinationType    = Volumes.DestinationType.Disk,
                        DiskId             = disk.Id,
                        DiskLabel          = disk.Label,
                        TotalCapacityBytes = rt.TotalCapacityBytes,
                        FreeSpaceBytes     = rt.FreeSpaceBytes,
                        RequiredBytes      = requiredBytes,
                        State              = state,
                        Mountpoint         = rt.Mountpoint,
                    });
                }
                else
                {
                    destinations.Add(new Volumes.VolumeDestination
                    {
                        DisplayName        = disk.Label,
                        DestinationType    = Volumes.DestinationType.Disk,
                        DiskId             = disk.Id,
                        DiskLabel          = disk.Label,
                        TotalCapacityBytes = disk.DeclaredCapacityBytes,
                        FreeSpaceBytes     = 0,
                        RequiredBytes      = requiredBytes,
                        State              = Volumes.DestinationState.NotMounted,
                        Mountpoint         = null,
                    });
                }
            }

            if (destinations.Count == 0)
            {
                await new InfoDialog("No Destinations Available",
                    "No valid move destinations were found.\n\n" +
                    "Add and initialize a disk, or ensure one is mounted.")
                    .ShowDialog(this);
                return;
            }

            // ── Open destination picker ───────────────────────────────────────
            var dlg       = new MoveVolumeDialog(entry.Label, requiredBytes, destinations);
            var confirmed = await dlg.ShowDialog<bool>(this);
            if (!confirmed || dlg.SelectedDestination is null) return;

            var dest = dlg.SelectedDestination;

            // ── Resolve destination path at operation time (fresh discovery) ───
            string  dstFolder;
            string? dstDiskId       = null;
            string? dstDiskLabel    = null;
            string? dstMountpoint   = null;

            if (dest.DestinationType == Volumes.DestinationType.Workspace)
            {
                dstFolder = workspaceFolder;
            }
            else
            {
                var rtForDst = DiskDiscoveryService.DiscoverAll();
                var dstDisk  = rtForDst.FirstOrDefault(d =>
                    d.HasMarker &&
                    string.Equals(d.DiskId, dest.DiskId, StringComparison.Ordinal));

                if (dstDisk is null)
                {
                    await new InfoDialog("Destination Disk Not Found",
                        $"Destination disk \"{dest.DiskLabel}\" (ID: {dest.DiskId}) " +
                        "is no longer mounted.\n\nConnect the disk and try again.")
                        .ShowDialog(this);
                    return;
                }

                dstFolder    = Path.Combine(dstDisk.Mountpoint, SafeFileName(entry.Label));
                dstDiskId    = dstDisk.DiskId;
                dstDiskLabel = dest.DiskLabel;
                dstMountpoint = dstDisk.Mountpoint;
            }

            // ── No-op guard ───────────────────────────────────────────────────
            if (string.Equals(
                    Path.GetFullPath(srcFolder).TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(dstFolder).TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                await new InfoDialog("No Move Needed",
                    $"Source and destination resolve to the same location:\n{srcFolder}")
                    .ShowDialog(this);
                return;
            }

            // ── Build header label ────────────────────────────────────────────
            static string DiskDesc(string? label, string? diskId, string? mountpoint)
            {
                var name = label ?? diskId ?? "disk";
                return mountpoint is not null ? $"{name} mounted in {mountpoint}" : name;
            }
            var srcDesc = srcIsWorkspace
                ? "Local Archive"
                : DiskDesc(entry.DiskLabel, srcDiskId, srcMountpoint);
            var dstDesc = dest.DestinationType == Volumes.DestinationType.Workspace
                ? "Local Archive"
                : DiskDesc(dstDiskLabel, dstDiskId, dstMountpoint);
            var header = $"Volume: {entry.Label}  —  Source: {srcDesc}  —  Destination: {dstDesc}";

            // ── Execute copy → verify → cleanup source ────────────────────────
            var (success, fileCount, copiedBytes, cleanupError, elapsed) =
                await RunCopyMoveAsync(
                    "Move Volume", srcFolder, dstFolder, header,
                    "volume-move", SafeFileName(entry.Label));

            if (!success) return;

            // ── Prune empty archive directories (best-effort) ─────────────────
            // Build Volume moved files out of archive/<platform>/<datline>/<release>/.
            // After source cleanup those dirs may be empty; remove them bottom-up.
            // Preserves archive/ and archive/<platform>/; removes deeper empty dirs.
            if (cleanupError is null)
            {
                var archiveRoot = Path.Combine(appRoot, "archive");
                if (Directory.Exists(archiveRoot))
                {
                    foreach (var platformDir in Directory.EnumerateDirectories(archiveRoot))
                        PruneEmptyDirectories(platformDir);
                }
            }

            // ── Update catalog location ───────────────────────────────────────
            _catalog.SetCurrentLocation(new Data.VolumeLocationRecord
            {
                Id           = Guid.NewGuid().ToString("N"),
                VolumeId     = entry.Id,
                LocationType = dest.DestinationType == Volumes.DestinationType.Workspace
                    ? "workspace" : "disk",
                DiskId       = dstDiskId,
                Path         = dest.DestinationType == Volumes.DestinationType.Workspace
                    ? dstFolder : null,
                IsCurrent    = true,
                CreatedAt    = DateTime.UtcNow,
            });

            RefreshVolumes();
            RefreshDisks();

            var updatedEntry = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
            if (updatedEntry is not null)
            {
                VolumesList.SelectedItem = updatedEntry;
                UpdateVolumeDetailPanel(updatedEntry);
            }

            // ── Write operation summary log ───────────────────────────────────
            try
            {
                var logDir  = Path.Combine(AppContext.BaseDirectory, "logs", "volume-move");
                Directory.CreateDirectory(logDir);
                var logTs   = DateTime.Now;
                var volSlug = SafeFileName(entry.Label);
                var logFile = Path.Combine(logDir,
                    $"{logTs:yyyyMMdd-HHmmss}-volume-move-{volSlug}.log");

                var secs        = elapsed.TotalSeconds;
                var speedStr    = secs > 0 ? FormatSpeed(copiedBytes / secs) : "—";
                var elapsedStr  = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                var srcRemoval  = cleanupError is null ? "OK" : $"FAILED — {cleanupError}";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Volume Move Completed");
                sb.AppendLine();
                sb.AppendLine($"Volume: {entry.Label}");
                sb.AppendLine();
                sb.AppendLine("Source Path:");
                sb.AppendLine(srcFolder);
                sb.AppendLine();
                sb.AppendLine("Destination Path:");
                sb.AppendLine(dstFolder);
                sb.AppendLine();
                sb.AppendLine($"Total Files:          {fileCount:N0}");
                sb.AppendLine($"Total Size:           {FormatBytes(copiedBytes)}");
                sb.AppendLine($"Transfer Speed (avg): {speedStr}");
                sb.AppendLine($"Total Elapsed:        {elapsedStr}");
                sb.AppendLine();
                sb.AppendLine($"Verification:   OK");
                sb.AppendLine($"Source Removal: {srcRemoval}");

                File.WriteAllText(logFile, sb.ToString());
            }
            catch { /* non-fatal — log failure must not interrupt UI flow */ }

            await new MoveCompleteDialog(
                entry.Label, srcFolder, dstFolder,
                fileCount, copiedBytes, elapsed, cleanupError)
                .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Move Volume Error", ex.Message).ShowDialog(this);
        }
    }

    private static string FormatBytes(long b)
    {
        if (b <= 0)                    return "0 B";
        if (b < 1024L)                 return $"{b} B";
        if (b < 1024L * 1024)          return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)   return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FormatSpeed(double bps)
    {
        if (bps >= 1024.0 * 1024 * 1024) return $"{bps / (1024.0 * 1024 * 1024):F2} GB/s";
        if (bps >= 1024.0 * 1024)        return $"{bps / (1024.0 * 1024):F1} MB/s";
        if (bps >= 1024.0)               return $"{bps / 1024.0:F0} KB/s";
        return $"{bps:F0} B/s";
    }

    // ── Library ───────────────────────────────────────────────────────────────

    private void InitLibrary()
    {
        RebuildLibraryDatasets();
        LibraryStatusFilter.ItemsSource   = new[] { "All Statuses", "Present", "Outdated", "Pending", "Missing", "Lost", "New" };
        LibraryStatusFilter.SelectedIndex = 0;
    }

    private void RebuildLibraryDatasets()
    {
        var allPlatforms = _catalog.LoadPlatforms();
        var allDatLines  = _catalog.LoadDatLines();

        var merged = new List<LibraryDataset>();

        foreach (var dl in allDatLines)
        {
            if (dl.DataStorePath.Length == 0) continue;

            var platformName = allPlatforms.FirstOrDefault(p => p.Id == dl.PlatformId)?.Name
                               ?? dl.PlatformId;
            var absPath  = Path.Combine(_dataDir, dl.DataStorePath);
            if (!File.Exists(absPath)) continue;

            var store       = new DatLineStore(absPath);
            var filesByRelId = store.LoadAllReleaseFiles();

            var releases = store.LoadReleases()
                .Select(r =>
                {
                    filesByRelId.TryGetValue(r.Id, out var romFiles);
                    return new LibraryEntry
                    {
                        Name                  = r.Name,
                        Platform              = platformName,
                        Status                = Capitalize(r.Status),
                        Region                = r.Region,
                        Languages             = r.Languages.ToUpperInvariant(),
                        Format                = r.Format,
                        Size                  = r.Size,
                        Tier                  = r.Tier,
                        RomFiles              = romFiles ?? [],
                        ReleaseId             = r.Id,
                        DbPath                = absPath,
                        DatLineId             = dl.Id,
                        TransformStrategyType = dl.TransformStrategyType,
                        IntroducedAtUtc       = r.IntroducedAtUtc,
                    };
                })
                .ToList();

            if (releases.Count > 0)
                merged.Add(new LibraryDataset(platformName, dl.Name, releases));
        }

        _activeLibraryDatasets = merged;

        // Preserve current platform selection across rebuild
        var currentPlatform = LibraryContextPlatform.SelectedItem as string;
        var currentDatLine  = LibraryContextDatLine.SelectedItem  as string;

        var platforms = _activeLibraryDatasets.Select(d => d.Platform).Distinct().ToList();

        // Suppress handlers while updating both selectors atomically
        LibraryContextPlatform.SelectionChanged -= OnLibraryContextPlatformChanged;
        LibraryContextDatLine.SelectionChanged  -= OnLibraryContextDatLineChanged;

        LibraryContextPlatform.ItemsSource = platforms;

        var pIdx = currentPlatform is not null ? platforms.IndexOf(currentPlatform) : -1;
        LibraryContextPlatform.SelectedIndex = pIdx >= 0 ? pIdx : 0;

        var activePlatform = LibraryContextPlatform.SelectedItem as string ?? (platforms.Count > 0 ? platforms[0] : null);
        var datLines = activePlatform is not null
            ? _activeLibraryDatasets.Where(d => d.Platform == activePlatform).Select(d => d.DatLine).ToList()
            : [];
        LibraryContextDatLine.ItemsSource = datLines;

        var dIdx = currentDatLine is not null ? datLines.IndexOf(currentDatLine) : -1;
        LibraryContextDatLine.SelectedIndex = dIdx >= 0 ? dIdx : (datLines.Count > 0 ? 0 : -1);

        LibraryContextPlatform.SelectionChanged += OnLibraryContextPlatformChanged;
        LibraryContextDatLine.SelectionChanged  += OnLibraryContextDatLineChanged;

        var selectedDatLine = LibraryContextDatLine.SelectedItem as string;
        if (activePlatform is not null)
            LoadActiveDataset(activePlatform, selectedDatLine);
    }

    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    /// <summary>
    /// Maps an internal location-type token to its user-facing display label.
    /// Never call with values that must round-trip to the database.
    /// </summary>
    private static string LocTypeDisplay(string locType) => locType switch
    {
        "workspace" => "Local Archive",
        _           => locType,
    };

    // Fired when the Platform context ComboBox changes
    private void OnLibraryContextPlatformChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        var platform = LibraryContextPlatform.SelectedItem as string;
        if (platform is null) return;

        var datLines = _activeLibraryDatasets
            .Where(d => d.Platform == platform)
            .Select(d => d.DatLine)
            .ToList();

        // Suppress DatLine's SelectionChanged while repopulating to avoid a double-filter call
        LibraryContextDatLine.SelectionChanged -= OnLibraryContextDatLineChanged;
        LibraryContextDatLine.ItemsSource       = datLines;
        LibraryContextDatLine.SelectedIndex     = 0;
        LibraryContextDatLine.SelectionChanged += OnLibraryContextDatLineChanged;

        LoadActiveDataset(platform, datLines.Count > 0 ? datLines[0] : null);
    }

    // Fired when the DAT Line context ComboBox changes
    private void OnLibraryContextDatLineChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        var platform = LibraryContextPlatform.SelectedItem as string;
        var datLine  = LibraryContextDatLine.SelectedItem  as string;
        if (platform is null || datLine is null) return;
        LoadActiveDataset(platform, datLine);
    }

    private void LoadActiveDataset(string platform, string? datLine)
    {
        _activeDatasetEntries = _activeLibraryDatasets
            .FirstOrDefault(d => d.Platform == platform && d.DatLine == datLine)
            ?.Entries ?? [];
        ApplyLibraryFilter();
    }

    private void OnLibrarySearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        => ApplyLibraryFilter();

    private void OnLibraryFilterChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => ApplyLibraryFilter();

    private void OnLibrarySelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => UpdateDetailPanel(LibraryList.SelectedItem as LibraryEntry);

    private void ApplyLibraryFilter()
    {
        var search = LibrarySearchBox.Text?.Trim() ?? string.Empty;
        var status = LibraryStatusFilter.SelectedItem as string ?? "All Statuses";

        var filtered = _activeDatasetEntries
            .Where(e => search == string.Empty ||
                        e.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(e => status == "All Statuses"
                     || (status == "New" ? e.IsNew : e.Status == status))
            .ToList();

        LibraryList.ItemsSource = filtered;
        UpdateDetailPanel(null);

        var total = _activeDatasetEntries.Count;
        LibraryCountText.Text = filtered.Count == total
            ? $"{total} items"
            : $"{filtered.Count} of {total} items";
    }

    private void UpdateDetailPanel(LibraryEntry? entry)
    {
        if (entry is null)
        {
            DetailEmptyState.IsVisible = true;
            DetailContent.IsVisible    = false;
            return;
        }

        DetailName.Text             = entry.Name;
        DetailPlatform.Text         = entry.Platform;
        DetailStatusText.Text       = entry.Status;
        DetailStatusText.Foreground = entry.StatusBrush;
        DetailNewBadge.IsVisible    = entry.IsNew;
        DetailTier.Text             = entry.TierDisplay;
        DetailTier.Foreground       = entry.TierBrush;
        DetailRegion.Text           = entry.Region;
        DetailLanguages.Text        = entry.Languages;
        DetailFormat.Text           = entry.Format;
        DetailSize.Text             = entry.Size;

        // SOURCE FILES + DERIVED FILES
        DetailSourceFiles.Children.Clear();
        DetailDerivedFiles.Children.Clear();

        if (entry.RomFiles.Count > 0)
        {
            DatLineStore? fileStore = entry.DbPath.Length > 0 && File.Exists(entry.DbPath)
                ? new DatLineStore(entry.DbPath) : null;

            var transformNames = _catalog.LoadTransforms().ToDictionary(t => t.Id, t => t.Name);
            var extTransforms  = new Dictionary<string, (bool IsDiscard, string Name)>(StringComparer.OrdinalIgnoreCase);
            if (entry.TransformStrategyType == "file_extension" && entry.DatLineId.Length > 0)
            {
                foreach (var m in _catalog.LoadExtensionMappings(entry.DatLineId))
                {
                    var name = m.IsDiscard ? "Discard"
                        : (transformNames.TryGetValue(m.TransformId, out var n) ? n : m.TransformId);
                    extTransforms[m.FileExtension] = (m.IsDiscard, name);
                }
            }

            // Collect (file, source, derived) for all ROM files
            var triples = new List<(Data.ReleaseFileRecord F, Data.SourceArtifactRecord? Src, Data.DerivedArtifactRecord? Dst)>();
            foreach (var f in entry.RomFiles)
            {
                var ck  = f.Sha1.Length > 0 ? $"sha1:{f.Sha1}"
                        : f.Md5.Length  > 0 ? $"md5:{f.Md5}"
                        : "";
                var src = ck.Length > 0 ? fileStore?.GetSourceByContentKey(ck)  : null;
                var dst = ck.Length > 0 ? fileStore?.GetDerivedByContentKey(ck) : null;
                triples.Add((f, src, dst));
            }

            // SOURCE FILES — first 5 rows, then a "view all" link
            const int MaxSourceDisplay = 5;
            foreach (var (f, src, _) in triples.Take(MaxSourceDisplay))
                DetailSourceFiles.Children.Add(MakeSourceFileRow(f, src, extTransforms, CopyAndToast));

            if (triples.Count > MaxSourceDisplay)
            {
                var allLink = new TextBlock
                {
                    Text      = $"View complete files list ({triples.Count} files)",
                    FontSize  = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#7B68EE")),
                    Cursor    = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Margin    = new Avalonia.Thickness(0, 4, 0, 0),
                };
                allLink.PointerPressed += (_, _) => ShowAllSourceFiles(entry.Name, triples, extTransforms);
                DetailSourceFiles.Children.Add(allLink);
            }

            // Release-level source verification totals used by derived section
            int srcTotal    = triples.Count;
            int srcVerified = triples.Count(t => IsSourceVerified(t.F, t.Src));

            // DERIVED FILES — unique by derived FileName
            var seenDerived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (f, src, dst) in triples)
            {
                if (dst is null) continue;
                if (!seenDerived.Add(dst.FileName)) continue;
                bool thisSrcOk      = IsSourceVerified(f, src);
                bool hasDerivedHash = dst.HashedDerivedSha1.Length > 0;
                bool derivedOk      = thisSrcOk && hasDerivedHash;
                var  xformName      = transformNames.TryGetValue(dst.StorageStrategyId, out var xn) ? xn : dst.StorageStrategyId;
                DetailDerivedFiles.Children.Add(MakeDerivedFileRow(dst, xformName, srcVerified, srcTotal, derivedOk, CopyAndToast));
            }

            if (DetailDerivedFiles.Children.Count == 0)
                DetailDerivedFiles.Children.Add(new TextBlock
                {
                    Text       = "No derived artifacts on record",
                    FontSize   = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#555566")),
                });
        }
        else
        {
            DetailSourceFiles.Children.Add(new TextBlock
            {
                Text       = "No source files on record",
                FontSize   = 12,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
            });
            DetailDerivedFiles.Children.Add(new TextBlock
            {
                Text       = "No derived artifacts on record",
                FontSize   = 12,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
            });
        }

        // PREVIOUSLY SEEN IN — only for non-present releases with at least one overlap
        DetailOverlapList.Children.Clear();
        bool showOverlap = false;
        if (entry.Status != "Present" && entry.ReleaseId.Length > 0 && entry.DbPath.Length > 0
            && File.Exists(entry.DbPath))
        {
            var overlaps = new DatLineStore(entry.DbPath).GetHistoricalOverlaps(entry.ReleaseId);
            if (overlaps.Count > 0)
            {
                showOverlap = true;
                foreach (var (_, relName, sharedCount) in overlaps)
                {
                    var label = sharedCount == 1
                        ? $"{relName} — 1 shared file"
                        : $"{relName} — {sharedCount} shared files";
                    DetailOverlapList.Children.Add(new TextBlock
                    {
                        Text       = label,
                        FontSize   = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    });
                }
            }
        }
        DetailOverlapDivider.IsVisible = showOverlap;
        DetailOverlapTitle.IsVisible   = showOverlap;
        DetailOverlapList.IsVisible    = showOverlap;

        // STORAGE — volumes & disks that contain this release's derived artifacts
        DetailStorageList.Children.Clear();
        bool showStorage = false;
        if (entry.ReleaseId.Length > 0 && entry.DbPath.Length > 0 && File.Exists(entry.DbPath))
        {
            var derivedIds = new DatLineStore(entry.DbPath)
                .GetDerivedArtifactIdsByRelease(entry.ReleaseId)
                .ToList();
            if (derivedIds.Count > 0)
            {
                var rows = _catalog.GetVolumeStorageForDerivedIds(derivedIds);
                if (rows.Count > 0)
                {
                    showStorage = true;
                    var mountedDiskIds = new HashSet<string>(
                        DiskDiscoveryService.DiscoverAll()
                            .Where(d => d.DiskId.Length > 0)
                            .Select(d => d.DiskId),
                        StringComparer.Ordinal);
                    var allDisks = _catalog.GetDisks()
                        .ToDictionary(d => d.Id, StringComparer.Ordinal);

                    foreach (var (vol, diskId, locType) in rows)
                    {
                        string diskLabel, statusText;
                        SolidColorBrush statusBrush;

                        if (vol.Status == "lost")
                        {
                            diskLabel  = diskId is not null && allDisks.TryGetValue(diskId, out var ld) ? ld.Label : "—";
                            statusText = "LOST";
                            statusBrush = new SolidColorBrush(Color.Parse("#EF5350"));
                        }
                        else if (locType == "workspace")
                        {
                            diskLabel   = "—";
                            statusText  = "LOCAL ARCHIVE";
                            statusBrush = new SolidColorBrush(Color.Parse("#64B5F6"));
                        }
                        else if (diskId is not null && allDisks.TryGetValue(diskId, out var d))
                        {
                            diskLabel   = d.Label;
                            statusText  = mountedDiskIds.Contains(diskId) ? "ON DISK" : "DISK NOT MOUNTED";
                            statusBrush = mountedDiskIds.Contains(diskId)
                                ? new SolidColorBrush(Color.Parse("#81C784"))
                                : new SolidColorBrush(Color.Parse("#FFB74D"));
                        }
                        else
                        {
                            diskLabel   = "—";
                            statusText  = "UNKNOWN";
                            statusBrush = new SolidColorBrush(Color.Parse("#555566"));
                        }

                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                        row.Children.Add(new TextBlock
                        {
                            Text       = vol.Label,
                            FontSize   = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            [Grid.ColumnProperty] = 0,
                        });
                        row.Children.Add(new TextBlock
                        {
                            Text       = diskLabel,
                            FontSize   = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#888899")),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Margin     = new Avalonia.Thickness(8, 0),
                            [Grid.ColumnProperty] = 1,
                        });
                        row.Children.Add(new TextBlock
                        {
                            Text       = statusText,
                            FontSize   = 10,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = statusBrush,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            [Grid.ColumnProperty] = 2,
                        });
                        DetailStorageList.Children.Add(row);
                    }
                }
            }
        }
        DetailStorageDivider.IsVisible = showStorage;
        DetailStorageTitle.IsVisible   = showStorage;
        DetailStorageList.IsVisible    = showStorage;

        // DEBUG INFO — only rendered when the setting is enabled
        DetailDebugDivider.IsVisible  = _showDebugArtifactInfo;
        DetailDebugTitle.IsVisible    = _showDebugArtifactInfo;
        DetailArtifacts.IsVisible     = _showDebugArtifactInfo;
        DetailArtifactsEmpty.IsVisible = false;

        if (_showDebugArtifactInfo)
        {
            DetailArtifacts.Children.Clear();
            var artifacts = BuildArtifacts(entry);
            if (artifacts.Count > 0)
            {
                foreach (var f in artifacts)
                    DetailArtifacts.Children.Add(MakeFileRow(f));
            }
            else
            {
                DetailArtifactsEmpty.IsVisible = true;
                DetailArtifacts.IsVisible      = false;
            }
        }

        DetailEmptyState.IsVisible = false;
        DetailContent.IsVisible    = true;
    }

    // ── ROM file helpers ──────────────────────────────────────────────────────

    private static List<Data.ReleaseFileRecord> ToReleaseFiles(
        string releaseId, List<DatParser.ParsedRom> roms)
    {
        var result = new List<Data.ReleaseFileRecord>(roms.Count);
        foreach (var rom in roms)
            result.Add(new Data.ReleaseFileRecord
            {
                Id        = System.Guid.NewGuid().ToString("N"),
                ReleaseId = releaseId,
                RomName   = rom.Name,
                Size      = rom.Size,
                Crc       = rom.Crc,
                Md5       = rom.Md5,
                Sha1      = rom.Sha1,
            });
        return result;
    }

    private static bool IsSourceVerified(Data.ReleaseFileRecord f, Data.SourceArtifactRecord? src)
    {
        if (src is null) return false;
        // All DAT hashes that are present must match; if none are present, cannot verify.
        bool anyDat = false;
        if (f.Sha1.Length > 0) { anyDat = true; if (!string.Equals(src.HashedSourceSha1,         f.Sha1, StringComparison.OrdinalIgnoreCase)) return false; }
        if (f.Md5.Length  > 0) { anyDat = true; if (!string.Equals(src.HashedSourceMd5  ?? "",   f.Md5,  StringComparison.OrdinalIgnoreCase)) return false; }
        if (f.Crc.Length  > 0) { anyDat = true; if (!string.Equals(src.HashedSourceCrc32 ?? "",  f.Crc,  StringComparison.OrdinalIgnoreCase)) return false; }
        return anyDat;
    }

    private static Control MakeSourceFileRow(
        Data.ReleaseFileRecord     f,
        Data.SourceArtifactRecord? source,
        Dictionary<string, (bool IsDiscard, string Name)>? extTransforms = null,
        Action<string>? onCopy = null)
    {
        var primary   = new SolidColorBrush(Color.Parse("#D0D0E0"));
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var accent    = new SolidColorBrush(Color.Parse("#5588AA"));
        var dim       = new SolidColorBrush(Color.Parse("#444455"));
        var green     = new SolidColorBrush(Color.Parse("#66BB6A"));
        var red       = new SolidColorBrush(Color.Parse("#EF5350"));
        var gray      = new SolidColorBrush(Color.Parse("#555566"));
        var mono      = new FontFamily("Consolas,Courier New,monospace");

        bool datOk = f.Crc.Length > 0 || f.Md5.Length > 0 || f.Sha1.Length > 0;

        // SOURCE: all present DAT hashes must match; no DAT hash = indeterminate.
        string srcSymbol; SolidColorBrush srcColor;
        if (source is null || !datOk)
        {
            srcSymbol = "—"; srcColor = gray;
        }
        else
        {
            srcSymbol = IsSourceVerified(f, source) ? "✔" : "✖";
            srcColor  = srcSymbol == "✔" ? green : red;
        }

        var panel = new StackPanel { Spacing = 2 };

        // File name — copyable
        var fileNameTb = new TextBlock
        {
            Text       = f.RomName.Length > 0 ? f.RomName : "(unnamed)",
            FontSize   = 12,
            FontWeight = FontWeight.Medium,
            Foreground = primary,
        };
        MakeCopyable(fileNameTb, f.RomName, primary, onCopy);
        panel.Children.Add(fileNameTb);

        var statusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 14,
            Margin      = new Avalonia.Thickness(0, 3, 0, 4),
        };

        void AddItem(string label, string symbol, SolidColorBrush symbolColor)
        {
            var grp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            grp.Children.Add(new TextBlock
            {
                Text              = label,
                FontSize          = 10,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = gray,
                LetterSpacing     = 0.6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            grp.Children.Add(new TextBlock
            {
                Text              = symbol,
                FontSize          = 13,
                FontWeight        = FontWeight.Bold,
                Foreground        = symbolColor,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            statusRow.Children.Add(grp);
        }

        AddItem("DAT",    datOk ? "✔" : "✖", datOk ? green : red);
        AddItem("SOURCE", srcSymbol,          srcColor);

        if (extTransforms is { Count: > 0 })
        {
            var ext = Path.GetExtension(f.RomName).ToLowerInvariant();
            if (ext.Length == 0) ext = "(no ext)";
            if (extTransforms.TryGetValue(ext, out var xform))
            {
                statusRow.Children.Add(new TextBlock
                {
                    Text              = "→",
                    FontSize          = 11,
                    Foreground        = dim,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                var xformColor = xform.IsDiscard
                    ? new SolidColorBrush(Color.Parse("#EF5350"))
                    : new SolidColorBrush(Color.Parse("#7B68EE"));
                var xformGrp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
                xformGrp.Children.Add(new TextBlock
                {
                    Text              = "TRANSFORM",
                    FontSize          = 10,
                    FontWeight        = FontWeight.SemiBold,
                    Foreground        = gray,
                    LetterSpacing     = 0.6,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                var xformValTb = new TextBlock
                {
                    Text              = xform.Name,
                    FontSize          = 11,
                    FontWeight        = FontWeight.SemiBold,
                    Foreground        = xformColor,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                MakeCopyable(xformValTb, xform.Name, xformColor, onCopy);
                xformGrp.Children.Add(xformValTb);
                statusRow.Children.Add(xformGrp);
            }
        }

        panel.Children.Add(statusRow);

        // 2-column metadata grid: label (80px fixed) | value; display and clipboard may differ.
        Grid MetaRowSrc(string label, string displayVal, string clipVal, SolidColorBrush valueFg, FontFamily? valueFf = null)
        {
            var g   = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
            var lbl = new TextBlock { Text = label,      FontSize = 11, Foreground = secondary };
            var val = new TextBlock { Text = displayVal, FontSize = 11, Foreground = valueFg, FontFamily = valueFf ?? FontFamily.Default };
            MakeCopyable(val, clipVal, valueFg, onCopy);
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(val, 1);
            g.Children.Add(lbl);
            g.Children.Add(val);
            return g;
        }

        var datCrc   = f.Crc.Length  > 0 ? f.Crc  : "—";
        var datMd5   = f.Md5.Length  > 0 ? f.Md5  : "—";
        var datSha1  = f.Sha1.Length > 0 ? f.Sha1 : "—";
        var srcCrc32 = source?.HashedSourceCrc32 is { Length: > 0 } c ? c : "—";
        var srcMd5   = source?.HashedSourceMd5   is { Length: > 0 } m ? m : "—";
        var srcSha1  = source?.HashedSourceSha1  is { Length: > 0 } s ? s : "—";

        // Bytes: display with thousands separators; clipboard = raw digits.
        string bytesDisplay, bytesClip;
        if (long.TryParse(f.Size, out var sizeVal)) { bytesDisplay = sizeVal.ToString("N0"); bytesClip = sizeVal.ToString(); }
        else { bytesDisplay = f.Size.Length > 0 ? f.Size : "—"; bytesClip = bytesDisplay; }

        var meta = new StackPanel { Spacing = 1, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        var srcBytesRow = MetaRowSrc("Bytes", bytesDisplay, bytesClip, secondary);
        srcBytesRow.Margin = new Avalonia.Thickness(0, 0, 0, 2);
        meta.Children.Add(srcBytesRow);
        meta.Children.Add(MetaRowSrc("DAT CRC32", datCrc,  datCrc,  f.Crc.Length  > 0 ? secondary : dim, f.Crc.Length  > 0 ? mono : null));
        meta.Children.Add(MetaRowSrc("DAT MD5",   datMd5,  datMd5,  f.Md5.Length  > 0 ? secondary : dim, f.Md5.Length  > 0 ? mono : null));
        meta.Children.Add(MetaRowSrc("DAT SHA1",  datSha1, datSha1, f.Sha1.Length > 0 ? secondary : dim, f.Sha1.Length > 0 ? mono : null));
        meta.Children.Add(MetaRowSrc("SRC CRC32", srcCrc32, srcCrc32, srcCrc32 != "—" ? accent : dim, srcCrc32 != "—" ? mono : null));
        meta.Children.Add(MetaRowSrc("SRC MD5",   srcMd5,   srcMd5,   srcMd5   != "—" ? accent : dim, srcMd5   != "—" ? mono : null));
        meta.Children.Add(MetaRowSrc("SRC SHA1",  srcSha1,  srcSha1,  srcSha1  != "—" ? accent : dim, srcSha1  != "—" ? mono : null));
        panel.Children.Add(meta);

        return panel;
    }

    private static Control MakeDerivedFileRow(
        Data.DerivedArtifactRecord derived,
        string transformName,
        int    sourcesVerified,
        int    sourcesTotal,
        bool   derivedOk,
        Action<string>? onCopy = null)
    {
        var primary   = new SolidColorBrush(Color.Parse("#D0D0E0"));
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var accent    = new SolidColorBrush(Color.Parse("#5588AA"));
        var dim       = new SolidColorBrush(Color.Parse("#444455"));
        var green     = new SolidColorBrush(Color.Parse("#66BB6A"));
        var red       = new SolidColorBrush(Color.Parse("#EF5350"));
        var gray      = new SolidColorBrush(Color.Parse("#555566"));
        var purple    = new SolidColorBrush(Color.Parse("#7B68EE"));
        var mono      = new FontFamily("Consolas,Courier New,monospace");

        var panel = new StackPanel { Spacing = 2 };

        // File name — copyable
        var fileNameTb = new TextBlock
        {
            Text       = derived.FileName.Length > 0 ? derived.FileName : "(unnamed)",
            FontSize   = 12,
            FontWeight = FontWeight.Medium,
            Foreground = primary,
        };
        MakeCopyable(fileNameTb, derived.FileName, primary, onCopy);
        panel.Children.Add(fileNameTb);

        // Status line: DERIVED ✔/✖   TRANSFORM <name>   SOURCES X/Y verified
        var statusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 14,
            Margin      = new Avalonia.Thickness(0, 3, 0, 4),
        };

        // Returns the value TextBlock so callers can wire copy if desired.
        TextBlock StatusItem(string label, string value, SolidColorBrush valueColor, bool valueBold = false)
        {
            var grp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            grp.Children.Add(new TextBlock
            {
                Text              = label,
                FontSize          = 10,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = gray,
                LetterSpacing     = 0.6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var valTb = new TextBlock
            {
                Text              = value,
                FontSize          = valueBold ? 13 : 11,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = valueColor,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            grp.Children.Add(valTb);
            statusRow.Children.Add(grp);
            return valTb;
        }

        var srcCountColor = sourcesVerified == sourcesTotal ? green
                          : sourcesVerified == 0            ? red
                          :                                   secondary;

        StatusItem("DERIVED",   derivedOk ? "✔" : "✖",                      derivedOk ? green : red, valueBold: true);
        var xformTb = StatusItem("TRANSFORM", transformName,                 purple);
        StatusItem("SOURCES",   $"{sourcesVerified}/{sourcesTotal} verified", srcCountColor);
        MakeCopyable(xformTb, transformName, purple, onCopy);

        panel.Children.Add(statusRow);

        // 2-column metadata grid: label (80px fixed) | value; display and clipboard may differ.
        Grid MetaRowDer(string label, string displayVal, string clipVal, SolidColorBrush valueFg, FontFamily? valueFf = null)
        {
            var g   = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
            var lbl = new TextBlock { Text = label,      FontSize = 11, Foreground = secondary };
            var val = new TextBlock { Text = displayVal, FontSize = 11, Foreground = valueFg, FontFamily = valueFf ?? FontFamily.Default };
            MakeCopyable(val, clipVal, valueFg, onCopy);
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(val, 1);
            g.Children.Add(lbl);
            g.Children.Add(val);
            return g;
        }

        var derCrc32 = derived.HashedDerivedCrc32 is { Length: > 0 } c ? c : "—";
        var derMd5   = derived.HashedDerivedMd5   is { Length: > 0 } m ? m : "—";
        var derSha1  = derived.HashedDerivedSha1  is { Length: > 0 } s ? s : "—";

        // Bytes: display with thousands separators; clipboard = raw digits.
        var derBytesDisplay = derived.DerivedSizeBytes.ToString("N0");
        var derBytesClip    = derived.DerivedSizeBytes.ToString();

        var meta = new StackPanel { Spacing = 1, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        var derBytesRow = MetaRowDer("Bytes", derBytesDisplay, derBytesClip, secondary);
        derBytesRow.Margin = new Avalonia.Thickness(0, 0, 0, 2);
        meta.Children.Add(derBytesRow);
        meta.Children.Add(MetaRowDer("DER CRC32", derCrc32, derCrc32, derCrc32 != "—" ? accent : dim, derCrc32 != "—" ? mono : null));
        meta.Children.Add(MetaRowDer("DER MD5",   derMd5,   derMd5,   derMd5   != "—" ? accent : dim, derMd5   != "—" ? mono : null));
        meta.Children.Add(MetaRowDer("DER SHA1",  derSha1,  derSha1,  derSha1  != "—" ? accent : dim, derSha1  != "—" ? mono : null));
        panel.Children.Add(meta);

        return panel;
    }

    private void ShowAllSourceFiles(
        string releaseName,
        List<(Data.ReleaseFileRecord F, Data.SourceArtifactRecord? Src, Data.DerivedArtifactRecord? Dst)> triples,
        Dictionary<string, (bool IsDiscard, string Name)> extTransforms)
    {
        var list = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(20, 16) };
        foreach (var (f, src, _) in triples)
            list.Children.Add(MakeSourceFileRow(f, src, extTransforms, CopyAndToast));

        var scroll = new ScrollViewer
        {
            Content = list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var win = new Window
        {
            Title                 = $"Source Files — {releaseName}",
            Width                 = 720,
            Height                = 600,
            Background            = new SolidColorBrush(Color.Parse("#0F0F14")),
            FontFamily            = new FontFamily("Inter,Segoe UI,sans-serif"),
            Content               = scroll,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        win.ShowDialog(this);
    }

    private static Control MakeRomFileRow(
        Data.ReleaseFileRecord      f,
        Data.SourceArtifactRecord?  source,
        Data.DerivedArtifactRecord? derived,
        Dictionary<string, (bool IsDiscard, string Name)>? extTransforms = null)
    {
        var primary   = new SolidColorBrush(Color.Parse("#D0D0E0"));
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var accent    = new SolidColorBrush(Color.Parse("#5588AA"));
        var dim       = new SolidColorBrush(Color.Parse("#444455"));
        var green     = new SolidColorBrush(Color.Parse("#66BB6A"));
        var red       = new SolidColorBrush(Color.Parse("#EF5350"));
        var gray      = new SolidColorBrush(Color.Parse("#555566"));
        var mono      = new FontFamily("Consolas,Courier New,monospace");

        static TextBlock Label(string text, SolidColorBrush fg, FontFamily? ff = null)
            => new TextBlock
            {
                Text       = text,
                FontSize   = 11,
                Foreground = fg,
                FontFamily = ff ?? FontFamily.Default,
            };

        // ── Status computation ────────────────────────────────────────────────
        bool anyDatHash = f.Sha1.Length > 0 || f.Md5.Length > 0 || f.Crc.Length > 0;

        // SOURCE: all present DAT hashes must match; no DAT hash = indeterminate.
        string srcSymbol; SolidColorBrush srcColor;
        if (source is null || !anyDatHash)
        {
            srcSymbol = "—"; srcColor = gray;
        }
        else
        {
            srcSymbol = IsSourceVerified(f, source) ? "✔" : "✖";
            srcColor  = srcSymbol == "✔" ? green : red;
        }

        // DERIVED: pipeline-based — valid when source matched + derived artifact exists.
        // Never compare derived hash against DAT hash (e.g. CHD ≠ BIN/CUE hash by design).
        string dstSymbol; SolidColorBrush dstColor;
        if (srcSymbol == "—")
        {
            // Source unknown — can't determine pipeline validity.
            dstSymbol = "—";
            dstColor  = gray;
        }
        else if (srcSymbol == "✔" && derived is not null)
        {
            // Source matched DAT + derived artifact exists → pipeline complete.
            dstSymbol = "✔";
            dstColor  = green;
        }
        else
        {
            // Source failed or derived artifact absent → pipeline incomplete.
            dstSymbol = "✖";
            dstColor  = red;
        }

        // ── Build panel ───────────────────────────────────────────────────────
        var panel = new StackPanel { Spacing = 2 };

        // Filename
        panel.Children.Add(new TextBlock
        {
            Text       = f.RomName.Length > 0 ? f.RomName : "(unnamed)",
            FontSize   = 12,
            FontWeight = FontWeight.Medium,
            Foreground = primary,
        });

        // Status block: DAT ✔   SOURCE ✔/✖/—   DERIVED ✔/✖
        var statusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 14,
            Margin      = new Avalonia.Thickness(0, 3, 0, 4),
        };
        void AddStatusItem(string labelText, string symbol, SolidColorBrush symbolColor)
        {
            var grp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            grp.Children.Add(new TextBlock
            {
                Text                = labelText,
                FontSize            = 10,
                FontWeight          = FontWeight.SemiBold,
                Foreground          = gray,
                LetterSpacing       = 0.6,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            });
            grp.Children.Add(new TextBlock
            {
                Text              = symbol,
                FontSize          = 13,
                FontWeight        = FontWeight.Bold,
                Foreground        = symbolColor,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            statusRow.Children.Add(grp);
        }
        AddStatusItem("DAT",     "✔",       green);
        AddStatusItem("SOURCE",  srcSymbol, srcColor);

        // TRANSFORM step — resolved from file extension via strategy mapping
        if (extTransforms is { Count: > 0 })
        {
            var ext = Path.GetExtension(f.RomName).ToLowerInvariant();
            if (ext.Length == 0) ext = "(no ext)";

            if (extTransforms.TryGetValue(ext, out var xform))
            {
                // Separator arrow
                statusRow.Children.Add(new TextBlock
                {
                    Text              = "→",
                    FontSize          = 11,
                    Foreground        = dim,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });

                var xformColor = xform.IsDiscard
                    ? new SolidColorBrush(Color.Parse("#EF5350"))
                    : new SolidColorBrush(Color.Parse("#7B68EE"));

                var xformGrp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
                xformGrp.Children.Add(new TextBlock
                {
                    Text              = "TRANSFORM",
                    FontSize          = 10,
                    FontWeight        = FontWeight.SemiBold,
                    Foreground        = gray,
                    LetterSpacing     = 0.6,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                xformGrp.Children.Add(new TextBlock
                {
                    Text              = xform.Name,
                    FontSize          = 11,
                    FontWeight        = FontWeight.SemiBold,
                    Foreground        = xformColor,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                statusRow.Children.Add(xformGrp);
            }
        }

        AddStatusItem("DERIVED", dstSymbol, dstColor);
        panel.Children.Add(statusRow);

        // DAT detail lines
        if (f.Size.Length > 0)
            panel.Children.Add(Label($"Size  {f.Size}", secondary));
        if (f.Crc.Length > 0)
            panel.Children.Add(Label($"CRC   {f.Crc}", secondary, mono));
        if (f.Md5.Length > 0)
            panel.Children.Add(Label($"MD5   {f.Md5}", secondary, mono));
        if (f.Sha1.Length > 0)
            panel.Children.Add(Label($"SHA1  {f.Sha1}", secondary, mono));

        // Source artifact hash (ingest proof)
        var srcHash = source?.HashedSourceSha1 is { Length: > 0 } s ? s : "";
        panel.Children.Add(Label(
            $"SRC   {(srcHash.Length > 0 ? srcHash : "—")}",
            srcHash.Length > 0 ? accent : dim,
            srcHash.Length > 0 ? mono   : null));

        // Derived artifact hash (archive copy)
        var dstHash = derived?.HashedDerivedSha1 is { Length: > 0 } d ? d : "";
        panel.Children.Add(Label(
            $"DST   {(dstHash.Length > 0 ? dstHash : "—")}",
            dstHash.Length > 0 ? accent : dim,
            dstHash.Length > 0 ? mono   : null));

        return panel;
    }

    private static List<FileRecord> BuildArtifacts(LibraryEntry entry)
    {
        if (entry.Status == "Missing")
            return [];

        var slug = Slugify(entry.Name);
        return [new FileRecord($"{slug}{entry.Format}", FakeHash(entry.Name + entry.Format, 64))];
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var s = sb.ToString().Trim('_');
        while (s.Contains("__"))
            s = s.Replace("__", "_");
        return s;
    }

    /// <summary>Deterministic fake hash using FNV-1a, extended to <paramref name="len"/> hex digits.</summary>
    private static string FakeHash(string seed, int len)
    {
        ulong h = 14695981039346656037UL;
        foreach (var c in seed)
            h = (h ^ c) * 1099511628211UL;

        var sb = new StringBuilder(len + 16);
        while (sb.Length < len)
        {
            sb.Append($"{h:x16}");
            h = (h ^ 0xdeadbeefUL) * 1099511628211UL;
        }
        return sb.ToString()[..len];
    }

    private static Control MakeFileRow(FileRecord f)
    {
        var primary   = new SolidColorBrush(Color.Parse("#F0F0F0"));
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var mono      = new FontFamily("Consolas,Courier New,monospace");

        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text            = f.FileName,
                    FontSize        = 12,
                    FontWeight      = FontWeight.Medium,
                    Foreground      = primary,
                    TextTrimming    = Avalonia.Media.TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text        = f.Hash,
                    FontSize    = 10,
                    FontFamily  = mono,
                    Foreground  = secondary,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            },
        };
    }

    private void InitLogo()
    {
        try
        {
            var settings = ThemeSettings.Load(
                Path.Combine(AppContext.BaseDirectory, "appconfig.json"));

            var manager = new ThemeManager(
                Path.Combine(AppContext.BaseDirectory, "themes", "visual"),
                settings.ActiveVisualThemeId);

            manager.Scan();

            var theme = manager.ResolveActiveTheme();
            if (theme is null)
                return;

            var logoPath = ResolveLogoPath(theme.ThemeDirectory);
            if (logoPath is null)
                return;

            SidebarLogoImage.Source    = new Bitmap(logoPath);
            SidebarLogoImage.IsVisible = true;
            SidebarLogoText.IsVisible  = false;
        }
        catch
        {
            // Fallback text stays visible — no crashes on missing files
        }
    }

    /// <summary>
    /// Returns the best logo PNG path for the current display.
    /// Prefers logomain2x.png on high-DPI screens (scaling ≥ 1.5), falls back to logomain.png.
    /// Returns null if neither file exists.
    /// </summary>
    private string? ResolveLogoPath(string themeDir)
    {
        var scaling = Screens?.Primary?.Scaling ?? 1.0;
        if (scaling >= 1.5)
        {
            var path2x = Path.Combine(themeDir, "logomain2x.png");
            if (File.Exists(path2x))
                return path2x;
        }
        var path1x = Path.Combine(themeDir, "logomain.png");
        return File.Exists(path1x) ? path1x : null;
    }

    // ── Dashboard ─────────────────────────────────────────────────────────────

    private void InitDashboard()
    {
        // ── Archive Overview ─────────────────────────────────────────────────
        var platforms = _catalog.LoadPlatforms();
        var datLines  = _catalog.LoadDatLines();
        var volumes   = _catalog.GetVolumes();
        var disks     = _catalog.GetDisks();

        DashPlatformsCount.Text = platforms.Count.ToString("N0");
        DashDatLinesCount.Text  = datLines.Count.ToString("N0");
        DashReleasesCount.Text  = datLines.Sum(dl => dl.ReleaseCount).ToString("N0");
        DashArtifactsCount.Text = _catalog.CountStoredArtifacts().ToString("N0");
        DashVolumesCount.Text   = volumes.Count.ToString("N0");
        DashDisksCount.Text     = disks.Count.ToString("N0");

        // ── Integrity & Attention ────────────────────────────────────────────
        DashVolOk.Text   = volumes.Count(v => v.Status != "lost" && v.Health == "ok").ToString("N0");
        DashVolCrit.Text = volumes.Count(v => v.Health == "crit").ToString("N0");
        DashVolLost.Text = volumes.Count(v => v.Status == "lost").ToString("N0");
        DashDiskLost.Text = disks.Count(d => d.Status == "lost").ToString("N0");

        int relMissing = 0, relPending = 0, relOutdated = 0;
        foreach (var dl in datLines)
        {
            if (dl.DataStorePath.Length == 0) continue;
            var dbPath = Path.Combine(_dataDir, dl.DataStorePath);
            if (!File.Exists(dbPath)) continue;
            var (missing, pending, outdated, _, _) = new DatLineStore(dbPath).GetAllStatusCounts();
            relMissing  += missing;
            relPending  += pending;
            relOutdated += outdated;
        }
        DashRelMissing.Text  = relMissing.ToString("N0");
        DashRelPending.Text  = relPending.ToString("N0");
        DashRelOutdated.Text = relOutdated.ToString("N0");

        // ── Tools ────────────────────────────────────────────────────────────
        var tools       = _catalog.LoadTools();
        var appRoot     = AppContext.BaseDirectory;
        var builtInIds  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "7zip" };
        int toolBuiltIn = 0, toolPresent = 0, toolMissing = 0;
        foreach (var tool in tools)
        {
            if (builtInIds.Contains(tool.Id)) { toolBuiltIn++;  continue; }
            var exePath = Path.Combine(appRoot, "tools", tool.FolderName, tool.ExecutableName);
            if (File.Exists(exePath)) toolPresent++;
            else toolMissing++;
        }
        DashToolsBuiltIn.Text = toolBuiltIn.ToString("N0");
        DashToolsPresent.Text = toolPresent.ToString("N0");
        DashToolsMissing.Text = toolMissing.ToString("N0");

        LoadLatestLogs();
    }

    private void OnDashboardRefresh(object? sender, RoutedEventArgs e) => InitDashboard();

    private void LoadLatestLogs()
    {
        DashLatestLogsPanel.Children.Clear();

        var logsRoot = Path.Combine(AppContext.BaseDirectory, "logs");
        var folderTypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ingest"]      = "Ingest",
            ["verify"]      = "Verify",
            ["repair"]      = "Repair",
            ["volume-move"] = "Volume Move",
            ["unexpected"]  = "Unexpected",
        };

        var entries = new List<(string Type, DateTime Timestamp, string FileName, string FullPath)>();

        foreach (var (folder, typeName) in folderTypeMap)
        {
            var dir = Path.Combine(logsRoot, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
            {
                entries.Add((typeName, File.GetLastWriteTime(file), Path.GetFileName(file), file));
            }
        }

        var recent = entries.OrderByDescending(e => e.Timestamp).Take(10).ToList();

        if (recent.Count == 0)
        {
            DashLatestLogsPanel.Children.Add(new TextBlock
            {
                Text       = "No operations recorded yet.",
                FontSize   = 12,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#555566")),
            });
            return;
        }

        // Column header
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("90,150,*,60"), Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        void AddHeader(int col, string text)
        {
            var tb = new TextBlock
            {
                Text       = text,
                FontSize   = 10,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#444455")),
            };
            Grid.SetColumn(tb, col);
            header.Children.Add(tb);
        }
        AddHeader(0, "TYPE");
        AddHeader(1, "TIMESTAMP");
        AddHeader(2, "FILE");
        DashLatestLogsPanel.Children.Add(header);

        // Divider
        DashLatestLogsPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E2C")),
            Margin     = new Avalonia.Thickness(0, 0, 0, 8),
        });

        var typeColors = new Dictionary<string, string>
        {
            ["Verify"]      = "#7B68EE",
            ["Repair"]      = "#4A90D9",
            ["Volume Move"] = "#E07040",
            ["Unexpected"]  = "#FFA726",
        };

        foreach (var (type, ts, fileName, fullPath) in recent)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("90,150,*,60"),
                Margin = new Avalonia.Thickness(0, 0, 0, 6),
            };

            var typeColor = typeColors.GetValueOrDefault(type, "#888899");

            var typeBlock = new TextBlock
            {
                Text              = type,
                FontSize          = 11,
                FontWeight        = Avalonia.Media.FontWeight.SemiBold,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse(typeColor)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var timeBlock = new TextBlock
            {
                Text              = ts.ToString("yyyy-MM-dd HH:mm"),
                FontSize          = 11,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse("#555566")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var nameBlock = new TextBlock
            {
                Text              = fileName,
                FontSize          = 11,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse("#AAAACC")),
                TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var openBtn = new Button
            {
                Content     = "Open",
                Tag         = fullPath,
                Classes     = { "view-toggle" },
                Padding     = new Avalonia.Thickness(8, 2),
                FontSize    = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            };
            openBtn.Click += OnDashLogOpen;

            Grid.SetColumn(typeBlock, 0);
            Grid.SetColumn(timeBlock, 1);
            Grid.SetColumn(nameBlock, 2);
            Grid.SetColumn(openBtn,   3);
            row.Children.Add(typeBlock);
            row.Children.Add(timeBlock);
            row.Children.Add(nameBlock);
            row.Children.Add(openBtn);
            DashLatestLogsPanel.Children.Add(row);
        }
    }

    private void OnDashLogOpen(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath) return;
        OpenLogFileInViewer(filePath);
    }

    private void OpenLogFileInViewer(string filePath)
    {
        SetActive(NavLogs);             // rebuilds tree, clears viewer
        LogsFileLabel.Text = filePath;
        try   { LogsContentBox.Text = File.ReadAllText(filePath); }
        catch { LogsContentBox.Text = "Failed to load log."; }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    // ── DAT operations ────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task OnVerifyDatLine(Systems.DatLineInfo info)
    {
        if (info.CatalogId is null || info.DataStorePath.Length == 0)
        {
            await new InfoDialog("Cannot Verify",
                "This DAT line has no data store path. Import the DAT line first.")
                .ShowDialog(this);
            return;
        }

        var dbPath = Path.Combine(_dataDir, info.DataStorePath);
        if (!File.Exists(dbPath))
        {
            await new InfoDialog("Cannot Verify",
                $"DAT line database not found at:\n{dbPath}")
                .ShowDialog(this);
            return;
        }

        // Collect all volumes for this DAT line
        var volumes = _catalog.GetVolumes()
            .Where(v => v.DatLineId == info.CatalogId)
            .ToList();

        if (volumes.Count == 0)
        {
            await new InfoDialog("No Volumes",
                $"No volumes are assigned to DAT line \"{info.Name}\".")
                .ShowDialog(this);
            return;
        }

        var platform    = info.CatalogPlatformId is not null
                              ? _catalog.GetPlatform(info.CatalogPlatformId)
                              : null;
        var platformDesc = platform is not null
                              ? $"{platform.Manufacturer} {platform.Name}".Trim()
                              : (info.CatalogPlatformId ?? "Unknown Platform");
        var dialog = new DatLineVerifyDialog(info.Name, platformDesc);
        var dlgTask = dialog.ShowDialog(this);

        await System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await RunDatLineVerify(dialog, info, dbPath, volumes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => dialog.SetFailed(ex.Message));
            }
        });

        await dlgTask;
    }

    private async System.Threading.Tasks.Task RunDatLineVerify(
        DatLineVerifyDialog dialog,
        Systems.DatLineInfo info,
        string dbPath,
        List<Data.VolumeRecord> volumes)
    {
        var appRoot       = AppContext.BaseDirectory;
        var store         = new Data.DatLineStore(dbPath);
        var allDisks      = _catalog.GetDisks().ToDictionary(d => d.Id, StringComparer.Ordinal);
        var runtimeDisks  = Data.DiskDiscoveryService.DiscoverAll()
            .Where(d => d.DiskId.Length > 0)
            .ToDictionary(d => d.DiskId, StringComparer.Ordinal);

        bool quarantineMismatch   = _catalog.GetBoolSetting("quarantine_mismatch_on_verify",   defaultValue: true);
        bool quarantineUnexpected = _catalog.GetBoolSetting("quarantine_unexpected_on_verify", defaultValue: false);
        var  quarantineBaseDir  = Path.Combine(
            appRoot, "incoming-skip",
            SafeFileName(info.CatalogPlatformId ?? "unknown"),
            SafeFileName(info.Name));

        int totalVols = volumes.Count, verifiedVols = 0, skippedVols = 0;
        int totalExpected = 0, totalVerified = 0, totalMissing = 0, totalMismatch = 0,
            totalUnexpected = 0, totalQuarantined = 0;
        long verifiedBytes = 0;
        int  quarantineFailures = 0;
        int  unexpectedQuarantined = 0;
        int  unexpectedQuarantineFailures = 0;
        bool applyCancelled = false;

        // Mismatches discovered during the read-only scan phase — consumed by apply phase below.
        var mismatchFiles   = new List<(string AbsPath, string FileName, string DispPath, string HashDetail, string VolLabel, string DaId)>();
        // Unexpected files discovered during scan — consumed by apply phase if quarantine is enabled.
        var unexpectedFiles = new List<(string AbsPath, string FileName, string DispPath, string VolLabel)>();

        // Artifact outcome lists — populated during the read-only scan, applied after confirmation.
        var verifiedDaIds = new List<string>();
        var missingDaIds  = new List<string>();

        // Tracks which volume IDs and their health were actually scanned (not skipped/lost).
        var scannedVolIds  = new HashSet<string>(StringComparer.Ordinal);
        var volHealthLog   = new List<(string VolId, string Label, string Health)>();

        var log = new System.Text.StringBuilder();
        log.AppendLine($"DAT Line Verify — {info.Name}");
        log.AppendLine($"Started:   {DateTime.UtcNow:o}");
        log.AppendLine($"Volumes:   {totalVols}");
        log.AppendLine();
        log.AppendLine("── Per-Volume Scan ──────────────────────────────────────────");
        log.AppendLine();

        for (int vi = 0; vi < volumes.Count; vi++)
        {
            var vol = volumes[vi];
            var volLabel = vol.Label;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.SetStatus(
                    $"Volume {vi + 1}/{totalVols}: {volLabel}  |  " +
                    $"Verified: {totalVerified}  Missing: {totalMissing}  " +
                    $"Mismatch: {totalMismatch}  Unexpected: {totalUnexpected}"));

            // ── Resolve source path ──────────────────────────────────────────
            string? srcRoot  = null;
            string  srcLabel = "";

            var wsRoot = Path.Combine(appRoot, "volumes", SafeFileName(volLabel));
            if (Directory.Exists(wsRoot))
            {
                srcRoot  = wsRoot;
                srcLabel = "workspace";
            }
            else
            {
                var loc = _catalog.GetCurrentLocation(vol.Id);
                if (loc?.DiskId is not null)
                {
                    if (vol.Status == "lost")
                    {
                        // Skip: disk+volume are marked lost; no point resolving
                        skippedVols++;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "SKIPPED", "", "DISK LOST"));
                        log.AppendLine($"  [{volLabel}] SKIPPED — DISK LOST");
                        continue;
                    }

                    if (!runtimeDisks.TryGetValue(loc.DiskId, out var rt))
                    {
                        skippedVols++;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "SKIPPED", "", "DISK NOT MOUNTED"));
                        log.AppendLine($"  [{volLabel}] SKIPPED — DISK NOT MOUNTED");
                        continue;
                    }

                    var diskRoot = Path.Combine(rt.Mountpoint, SafeFileName(volLabel));
                    if (!Directory.Exists(diskRoot))
                    {
                        skippedVols++;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "SKIPPED", "", "FOLDER NOT FOUND ON DISK"));
                        log.AppendLine($"  [{volLabel}] SKIPPED — folder not found at {diskRoot}");
                        continue;
                    }

                    srcRoot  = diskRoot;
                    srcLabel = allDisks.TryGetValue(loc.DiskId, out var dr) ? $"disk:{dr.Label}" : "disk";
                }
                else
                {
                    skippedVols++;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(volLabel, "SKIPPED", "", "NO ACCESSIBLE SOURCE"));
                    log.AppendLine($"  [{volLabel}] SKIPPED — no accessible source");
                    continue;
                }
            }

            // ── Build expected file set ──────────────────────────────────────
            var vaIds     = _catalog.GetVolumeArtifacts(vol.Id)
                                    .Select(va => va.DerivedArtifactId).ToList();
            var expected  = store.GetArtifactVerifyInfos(vaIds);

            // Map relative path (within volume root) → verify info
            // Physical path: srcRoot / SafeFileName(ReleaseName) / FileName
            var expectedByRelPath = new Dictionary<string, Data.ArtifactVerifyInfo>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var e in expected)
            {
                var rel = Path.Combine(SafeFileName(e.ReleaseName), e.FileName);
                expectedByRelPath[rel] = e;
            }

            // ── Enumerate actual files ───────────────────────────────────────
            var actualFiles = Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(srcRoot.Length).TrimStart(Path.DirectorySeparatorChar,
                                                                    Path.AltDirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int volExpected = expected.Count, volVerified = 0, volMissing = 0,
                volMismatch = 0, volUnexpected = 0;

            log.AppendLine($"  [{volLabel}] source={srcLabel}  expected={volExpected}");

            // ── Verify expected files (read-only scan) ───────────────────────
            foreach (var ei in expected)
            {
                var relPath  = Path.Combine(SafeFileName(ei.ReleaseName), ei.FileName);
                var absPath  = Path.Combine(srcRoot, relPath);
                var dispPath = $"{SafeFileName(ei.ReleaseName)}/{ei.FileName}";

                if (!File.Exists(absPath))
                {
                    volMissing++;
                    missingDaIds.Add(ei.DerivedArtifactId);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(volLabel, "MISSING", dispPath, ""));
                    log.AppendLine($"    MISSING  {dispPath}");
                    continue;
                }

                // Fast size precheck
                var actualSize = new FileInfo(absPath).Length;
                var sizeOk     = ei.SizeBytes <= 0 || actualSize == ei.SizeBytes;

                // SHA1 — always compute for expected files that exist
                var actualSha1 = ComputeFileSha1(absPath);

                if (ei.Sha1.Length > 0 &&
                    !string.Equals(actualSha1, ei.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    volMismatch++;
                    var hashDetail = $"exp:{ei.Sha1[..8]}… got:{actualSha1[..8]}…";
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(volLabel, "MISMATCH", dispPath, hashDetail));
                    log.AppendLine($"    MISMATCH  {dispPath}  expected={ei.Sha1}  actual={actualSha1}");
                    // Record for apply phase — no filesystem changes here.
                    mismatchFiles.Add((absPath, ei.FileName, dispPath, hashDetail, volLabel, ei.DerivedArtifactId));
                }
                else
                {
                    volVerified++;
                    verifiedDaIds.Add(ei.DerivedArtifactId);
                    verifiedBytes += actualSize;
                    var detail = sizeOk ? FormatBytes(actualSize) : $"size:{actualSize}≠{ei.SizeBytes}";
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(volLabel, "VERIFIED", dispPath, detail));
                    log.AppendLine($"    VERIFIED  {dispPath}  sha1={actualSha1}");
                }
            }

            // ── Unexpected files ─────────────────────────────────────────────
            foreach (var rel in actualFiles)
            {
                if (!expectedByRelPath.ContainsKey(rel))
                {
                    volUnexpected++;
                    unexpectedFiles.Add((Path.Combine(srcRoot, rel), Path.GetFileName(rel), rel, volLabel));
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(volLabel, "UNEXPECTED", rel, ""));
                    log.AppendLine($"    UNEXPECTED  {rel}");
                }
            }

            verifiedVols++;
            totalExpected    += volExpected;
            totalVerified    += volVerified;
            totalMissing     += volMissing;
            totalMismatch    += volMismatch;
            totalUnexpected  += volUnexpected;

            scannedVolIds.Add(vol.Id);
            var health = (volMissing + volMismatch == 0) ? "OK" : "CRIT";
            volHealthLog.Add((vol.Id, volLabel, health));

            log.AppendLine($"    → verified={volVerified} missing={volMissing} " +
                           $"mismatch={volMismatch} unexpected={volUnexpected}  health={health}");

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.UpdateStats(totalVols, verifiedVols, skippedVols,
                                   totalExpected, totalVerified, totalMissing));
        }

        // ── Scan complete — update dialog before any apply ────────────────────
        bool hasUnexpectedQuarantine = quarantineUnexpected && unexpectedFiles.Count > 0;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var parts = new System.Text.StringBuilder();
            parts.Append($"Scan complete — Verified: {totalVerified}  Missing: {totalMissing}  " +
                         $"Mismatch: {totalMismatch}  Unexpected: {totalUnexpected}");
            if (totalUnexpected > 0)
                parts.Append(quarantineUnexpected
                    ? "  (unexpected: will be quarantined)"
                    : "  (unexpected: scan-only, no changes applied)");
            if (verifiedDaIds.Count > 0 || missingDaIds.Count > 0 ||
                (quarantineMismatch && mismatchFiles.Count > 0) || hasUnexpectedQuarantine)
                parts.Append("  |  Pending reconcile & apply.");
            dialog.SetStatus(parts.ToString());
        });

        // ── Apply phase — gated on user confirmation ──────────────────────────
        // Gates: quarantine of mismatch files, artifact-status DB writes, release recalculation.
        bool hasVerifyChanges = verifiedDaIds.Count > 0 || missingDaIds.Count > 0;
        bool hasQuarantine    = quarantineMismatch && mismatchFiles.Count > 0;

        int appliedArtifacts = 0, appliedReleases = 0;
        int artPresent = 0, artMissing = 0;

        if (hasVerifyChanges || hasQuarantine || hasUnexpectedQuarantine)
        {
            // Build confirmation message describing what will happen.
            int volsOk   = volHealthLog.Count(v => v.Health == "OK");
            int volsCrit = volHealthLog.Count(v => v.Health == "CRIT");

            var confirmLines = new System.Text.StringBuilder();
            confirmLines.AppendLine("Verify & Reconcile — Apply Results?");
            confirmLines.AppendLine();
            confirmLines.AppendLine("── Scan Results ─────────────────────────────────────");
            confirmLines.AppendLine($"  Volumes scanned:     {verifiedVols} of {totalVols}  ({skippedVols} skipped)");
            confirmLines.AppendLine($"  Files expected:      {totalExpected}");
            confirmLines.AppendLine($"    Verified:          {totalVerified}");
            confirmLines.AppendLine($"    Missing:           {totalMissing}");
            confirmLines.AppendLine($"    Mismatch:          {totalMismatch}");
            confirmLines.AppendLine();
            confirmLines.AppendLine("── Reconcile Actions ─────────────────────────────────");
            confirmLines.AppendLine($"  Artifacts → present: {verifiedDaIds.Count}");
            confirmLines.AppendLine($"  Artifacts → missing: {missingDaIds.Count}");
            if (hasQuarantine)
                confirmLines.AppendLine($"  Mismatches → quarantine: {mismatchFiles.Count}");
            if (totalUnexpected > 0)
            {
                confirmLines.AppendLine();
                confirmLines.AppendLine("── Unexpected Files ──────────────────────────────────");
                confirmLines.AppendLine($"  Found:  {totalUnexpected}");
                confirmLines.AppendLine(hasUnexpectedQuarantine
                    ? $"  Action: Quarantine → incoming-skip/unexpected/"
                    : "  Action: Report only  (quarantine disabled)");
                confirmLines.AppendLine("  Note:   unexpected files do not affect artifact or release state.");
            }
            confirmLines.AppendLine();
            confirmLines.AppendLine("── Volume Health Forecast ────────────────────────────");
            confirmLines.AppendLine($"  Volumes → OK:   {volsOk}");
            confirmLines.AppendLine($"  Volumes → CRIT: {volsCrit}");
            confirmLines.AppendLine();
            confirmLines.AppendLine("Release statuses will be recalculated.");
            confirmLines.Append("Cancel to discard — no changes will be applied.");

            bool applyConfirmed = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                async () => await new ConfirmDialog("Apply Verify Results?", confirmLines.ToString())
                    .ShowDialog<bool>(this));

            if (!applyConfirmed)
            {
                applyCancelled = true;
                goto WriteLog;
            }

            // Track mismatch daIds that were successfully quarantined.
            var quarantinedDaIds = new List<string>();

            // ── Quarantine mismatch files ──────────────────────────────────────
            if (hasQuarantine)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.SetStatus($"Applying quarantine to {mismatchFiles.Count} mismatch file(s)…"));

                log.AppendLine();
                log.AppendLine("── Quarantine Summary ───────────────────────────────────────");

                foreach (var (mAbsPath, mFileName, mDispPath, mHashDetail, mVolLabel, mDaId) in mismatchFiles)
                {
                    if (!File.Exists(mAbsPath))
                    {
                        // File was already removed between scan and apply — treat as quarantined.
                        totalQuarantined++;
                        quarantinedDaIds.Add(mDaId);
                        log.AppendLine($"    SKIP (already gone)  {mDispPath}");
                        continue;
                    }

                    bool moved = false;
                    string? moveError = null;
                    try
                    {
                        Directory.CreateDirectory(quarantineBaseDir);
                        var dest = IncomingSkipUniquePath(quarantineBaseDir, mFileName);
                        try
                        {
                            File.Move(mAbsPath, dest, overwrite: false);
                            moved = true;
                        }
                        catch
                        {
                            // Cross-volume fallback: copy then delete
                            File.Copy(mAbsPath, dest, overwrite: false);
                            File.Delete(mAbsPath);
                            moved = true;
                        }
                    }
                    catch (Exception moveEx)
                    {
                        moveError = moveEx.Message;
                    }

                    if (moved)
                    {
                        totalQuarantined++;
                        quarantinedDaIds.Add(mDaId);
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(mVolLabel, "QUARANTINED", mDispPath, "moved to incoming-skip"));
                        log.AppendLine($"    QUARANTINED  {mDispPath}  → incoming-skip");
                    }
                    else
                    {
                        quarantineFailures++;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(mVolLabel, "QUARANTINE FAILED", mDispPath, $"error: {moveError}"));
                        log.AppendLine($"    QUARANTINE FAILED  {mDispPath}  error={moveError}");
                    }
                }
            }

            // ── Quarantine unexpected files ────────────────────────────────────
            // Unexpected quarantine never changes artifact or release state.
            if (hasUnexpectedQuarantine)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.SetStatus($"Quarantining {unexpectedFiles.Count} unexpected file(s)…"));

                var unexpectedBaseDir = Path.Combine(appRoot, "incoming-skip", "unexpected");

                log.AppendLine();
                log.AppendLine("── Unexpected Summary ───────────────────────────────────────");

                foreach (var (uAbsPath, uFileName, uDispPath, uVolLabel) in unexpectedFiles)
                {
                    if (!File.Exists(uAbsPath))
                    {
                        unexpectedQuarantined++;
                        log.AppendLine($"    SKIP (already gone)  {uDispPath}");
                        continue;
                    }

                    var volQuarantineDir = Path.Combine(unexpectedBaseDir, SafeFileName(uVolLabel));
                    bool moved = false;
                    string? moveError = null;
                    try
                    {
                        Directory.CreateDirectory(volQuarantineDir);
                        var dest = IncomingSkipUniquePath(volQuarantineDir, uFileName);
                        try
                        {
                            File.Move(uAbsPath, dest, overwrite: false);
                            moved = true;
                        }
                        catch
                        {
                            // Cross-volume fallback: copy then delete
                            File.Copy(uAbsPath, dest, overwrite: false);
                            File.Delete(uAbsPath);
                            moved = true;
                        }
                    }
                    catch (Exception moveEx)
                    {
                        moveError = moveEx.Message;
                    }

                    if (moved)
                    {
                        unexpectedQuarantined++;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(uVolLabel, "QUARANTINED", uDispPath,
                                "moved to incoming-skip/unexpected"));
                        log.AppendLine($"    QUARANTINED  {uDispPath}  → incoming-skip/unexpected/{SafeFileName(uVolLabel)}");
                    }
                    else
                    {
                        unexpectedQuarantineFailures++;
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(uVolLabel, "QUARANTINE FAILED", uDispPath, $"error: {moveError}"));
                        log.AppendLine($"    QUARANTINE FAILED  {uDispPath}  error={moveError}");
                    }
                }
            }

            // ── Artifact status DB writes ──────────────────────────────────────
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.SetStatus("Updating artifact and release records…"));

            // VERIFIED → present; MISSING → missing; quarantined MISMATCH → missing.
            // Non-quarantined MISMATCH artifacts are left unchanged (state unknown).
            var missingUpdateIds = new List<string>(missingDaIds.Count + quarantinedDaIds.Count);
            missingUpdateIds.AddRange(missingDaIds);
            missingUpdateIds.AddRange(quarantinedDaIds);

            artPresent = store.BatchUpdateDerivedArtifactStatus(verifiedDaIds, "present");
            artMissing = store.BatchUpdateDerivedArtifactStatus(missingUpdateIds, "missing");
            appliedArtifacts = artPresent + artMissing;

            // ── Release recalculation ──────────────────────────────────────────
            var allChangedDaIds = new List<string>(verifiedDaIds.Count + missingUpdateIds.Count);
            allChangedDaIds.AddRange(verifiedDaIds);
            allChangedDaIds.AddRange(missingUpdateIds);
            appliedReleases = store.RecalculateReleaseStatusForArtifacts(allChangedDaIds);

            log.AppendLine();
            log.AppendLine("── Apply Summary ────────────────────────────────────────────");
            log.AppendLine($"  Artifacts marked present:  {artPresent}");
            log.AppendLine($"  Artifacts marked missing:  {artMissing}  (scan-missing={missingDaIds.Count}  quarantined={quarantinedDaIds.Count})");
            log.AppendLine($"  Releases recalculated:     {appliedReleases}");

            // ── Persist volume health ─────────────────────────────────────────
            // health is set per-scanned-volume only; skipped/lost volumes are excluded.
            log.AppendLine();
            log.AppendLine("── Volume Health Summary ────────────────────────────────────");
            foreach (var (vhVolId, vhLabel, vhHealth) in volHealthLog)
            {
                var dbHealth = vhHealth == "CRIT" ? "crit" : "ok";
                _catalog.UpdateVolumeHealth(vhVolId, dbHealth);
                log.AppendLine($"  [{vhLabel}]  {vhHealth}");
            }

            // ── UI refresh ────────────────────────────────────────────────────
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                RebuildLibraryDatasets();
                RefreshVolumes();
            });
        }

        WriteLog:
        // ── Write log ────────────────────────────────────────────────────────
        var endTime = DateTime.UtcNow;
        log.AppendLine();
        log.AppendLine("── Scan Summary ─────────────────────────────────────────────");
        log.AppendLine($"  Completed:     {endTime:o}");
        log.AppendLine($"  Volumes:       total={totalVols}  scanned={verifiedVols}  skipped={skippedVols}");
        log.AppendLine($"  Files:         expected={totalExpected}  verified={totalVerified}  missing={totalMissing}  mismatch={totalMismatch}  unexpected={totalUnexpected}");
        log.AppendLine($"  Quarantined:   {totalQuarantined}  failures={quarantineFailures}");
        if (totalUnexpected > 0)
            log.AppendLine($"  Unexpected:    found={totalUnexpected}  quarantined={unexpectedQuarantined}" +
                           $"  failures={unexpectedQuarantineFailures}" +
                           (quarantineUnexpected ? "" : "  (report only)"));
        log.AppendLine($"  SHA1-verified: {FormatBytes(verifiedBytes)}");
        if (applyCancelled)
            log.AppendLine("  Apply:         CANCELLED — no persistent changes applied");
        else if (appliedArtifacts > 0 || appliedReleases > 0)
            log.AppendLine($"  DB apply:      artifacts={appliedArtifacts}  releases={appliedReleases}");
        else
            log.AppendLine("  DB apply:      none (nothing to update)");

        if (_catalog.GetBoolSetting("auto_export_verify_logs", defaultValue: true))
        {
            try
            {
                var logDir  = Path.Combine(appRoot, "logs", "verify");
                Directory.CreateDirectory(logDir);
                var safe    = SafeFileName(info.Name);
                var logFile = Path.Combine(logDir, $"{endTime:yyyyMMdd-HHmmss}-verify-{safe}.log");
                File.WriteAllText(logFile, log.ToString());
            }
            catch { /* non-fatal */ }
        }

        // ── Final status ─────────────────────────────────────────────────────
        bool clean    = totalMissing == 0 && totalMismatch == 0;
        int critCount = volHealthLog.Count(v => v.Health == "CRIT");
        int okCount   = volHealthLog.Count(v => v.Health == "OK");
        string summary;
        string unexpectedSummaryLine = "";
        if (totalUnexpected > 0)
        {
            if (applyCancelled || !quarantineUnexpected)
                unexpectedSummaryLine = $"\nUnexpected: {totalUnexpected} found  (reported only — no artifact changes)";
            else
                unexpectedSummaryLine =
                    $"\nUnexpected: {totalUnexpected} found  |  Quarantined: {unexpectedQuarantined}" +
                    (unexpectedQuarantineFailures > 0 ? $"  |  Failed: {unexpectedQuarantineFailures}" : "") +
                    "  (no artifact changes)";
        }

        if (applyCancelled)
        {
            summary =
                $"Scan complete — no persistent changes applied.\n" +
                $"Volumes: {verifiedVols}/{totalVols} scanned  ({skippedVols} skipped)\n" +
                $"Files: expected={totalExpected}  verified={totalVerified}  missing={totalMissing}  " +
                $"mismatch={totalMismatch}  unexpected={totalUnexpected}" +
                unexpectedSummaryLine +
                (clean ? "\nAll expected files verified clean." : "");
        }
        else
        {
            summary =
                $"Verify & Reconcile complete.\n" +
                $"Volumes: {verifiedVols}/{totalVols} scanned  ({skippedVols} skipped)" +
                (critCount > 0 ? $"  |  {critCount} CRIT  {okCount} OK" : $"  |  all {okCount} OK") + "\n" +
                $"Files: expected={totalExpected}  verified={totalVerified}  missing={totalMissing}  " +
                $"mismatch={totalMismatch}  unexpected={totalUnexpected}\n" +
                $"Artifacts: {artPresent} → present  {artMissing} → missing  |  Releases recalculated: {appliedReleases}\n" +
                $"Quarantined: {totalQuarantined}" +
                (quarantineFailures > 0 ? $"  |  Quarantine failures: {quarantineFailures}" : "") +
                unexpectedSummaryLine +
                (clean ? "\nAll expected files verified clean." : "");
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            dialog.UpdateStats(totalVols, verifiedVols, skippedVols,
                               totalExpected, totalVerified, totalMissing);
            dialog.SetStatus(
                $"Volumes: {totalVols}  Scanned: {verifiedVols}  Skipped: {skippedVols}" +
                (critCount > 0 ? $"  CRIT: {critCount}" : "") +
                $"  |  Expected: {totalExpected}  OK: {totalVerified}  Missing: {totalMissing}  " +
                $"Mismatch: {totalMismatch}  Unexpected: {totalUnexpected}  |  " +
                $"SHA1-verified: {FormatBytes(verifiedBytes)}");
            dialog.SetCompleted(summary);
        });
    }

    private async System.Threading.Tasks.Task OnUpdateDatLine(Systems.DatLineInfo info)
    {
        if (info.CatalogId is null || info.DataStorePath.Length == 0) return;

        var platformName = _selectedPlatform?.Name ?? info.CatalogPlatformId ?? "";
        var strategyName = info.StorageStrategy;

        var allDatLines = _catalog.LoadDatLines();
        var record      = allDatLines.FirstOrDefault(dl => dl.Id == info.CatalogId);
        if (record is null) return;

        var updateDialog = new UpdateDatDialog(record, platformName, strategyName);
        var ok           = await updateDialog.ShowDialog<bool>(this);
        if (!ok) return;

        var parseResult = updateDialog.ParseResult;
        if (parseResult is null || !parseResult.Success) return;

        var absPath  = Path.Combine(_dataDir, info.DataStorePath);
        var version  = updateDialog.Version ?? "";

        var progressDialog = new DatOperationProgressDialog("Update DAT");
        var progress       = new Progress<DatOperationProgress>(p => progressDialog.Update(p));
        ReconciliationResult? reconResult = null;

        var workTask = System.Threading.Tasks.Task.Run(() =>
            reconResult = RunUpdateWork(absPath, record.Id, parseResult.Games, progress));

        _ = workTask.ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (t.IsFaulted)
                    progressDialog.SetFailed(t.Exception!.InnerException?.Message ?? t.Exception.Message);
                else
                    progressDialog.SetUpdateCompleted(platformName, record.Id, reconResult!);
            }),
            System.Threading.Tasks.TaskContinuationOptions.None);

        await progressDialog.ShowDialog<bool>(this);
        await workTask;

        if (workTask.IsCompletedSuccessfully && reconResult is not null)
        {
            var totalReleases = reconResult.Kept + reconResult.Pending + reconResult.Missing;
            _catalog.UpdateDatLineMetadata(
                id:            record.Id,
                version:       version,
                releaseCount:  totalReleases,
                importedAtUtc: DateTime.UtcNow);

            RebuildLibraryDatasets();
            ResolveFlagImages();
            RefreshSystemsKeepSelection(record.PlatformId);
            RefreshPending();
        }
    }

    private ReconciliationResult RunUpdateWork(
        string                      absPath,
        string                      datLineId,
        List<DatParser.ParsedGame>  games,
        IProgress<DatOperationProgress> progress)
    {
        progress.Report(new DatOperationProgress
        {
            PhaseText       = "Running reconciliation…",
            IsIndeterminate = true,
        });

        var store      = new DatLineStore(absPath);
        var reconResult = ReconciliationEngine.ApplyDatUpdate(store, datLineId, games);

        var newGameByName = games.ToDictionary(g => g.Name, StringComparer.Ordinal);
        var releases      = store.LoadReleases();
        var relevant      = releases.Where(r => newGameByName.ContainsKey(r.Name)).ToList();
        var count         = relevant.Count;
        var accepted      = reconResult.Kept + reconResult.Pending;
        var rejected      = reconResult.Outdated + reconResult.Missing;

        progress.Report(new DatOperationProgress
        {
            PhaseText       = "Saving ROM files…",
            IsIndeterminate = false,
            Total           = count,
            Processed       = 0,
            Accepted        = accepted,
            Rejected        = rejected,
        });

        for (int i = 0; i < relevant.Count; i++)
        {
            var r = relevant[i];
            if (newGameByName.TryGetValue(r.Name, out var g))
                store.SaveReleaseFiles(r.Id, ToReleaseFiles(r.Id, g.Roms));

            if (i % 25 == 0 || i == count - 1)
                progress.Report(new DatOperationProgress
                {
                    PhaseText       = "Saving ROM files…",
                    IsIndeterminate = false,
                    Total           = count,
                    Processed       = i + 1,
                    Accepted        = accepted,
                    Rejected        = rejected,
                });
        }

        progress.Report(new DatOperationProgress
        {
            PhaseText       = "Updating catalog…",
            IsIndeterminate = true,
            Processed       = count,
            Accepted        = accepted,
            Rejected        = rejected,
        });

        return reconResult;
    }

    private async System.Threading.Tasks.Task OnDeleteDatLine(
        string catalogId, string catalogPlatformId, DatLineInfo info)
    {
        // Look up authority and release count from persisted record
        var allDatLines  = _catalog.LoadDatLines();
        var record       = allDatLines.FirstOrDefault(dl => dl.Id == catalogId);
        if (record is null) return;

        var platformName = _selectedPlatform?.Name ?? catalogPlatformId;

        var dialog = new DeleteDatLineDialog(
            platformName:  platformName,
            datLineName:   record.Name,
            authority:     record.Authority,
            releaseCount:  record.ReleaseCount);

        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed) return;

        _catalog.DeleteDatLine(catalogId, catalogPlatformId);

        var prevId = _selectedPlatform?.Id;
        RebuildLibraryDatasets();
        ResolveFlagImages();
        RefreshSystemsKeepSelection(prevId);
    }

    // ── Ingestion ─────────────────────────────────────────────────────────────

    private async void OnIngestDatLine(DatLineInfo info)
    {
        if (info.CatalogId is null || info.CatalogPlatformId is null || info.DataStorePath.Length == 0)
            return;

        var platformId        = info.CatalogPlatformId;
        var datLineId         = info.CatalogId;
        var absDbPath         = Path.Combine(_dataDir, info.DataStorePath);
        var storageStrategyId = info.DataStorePath.Length > 0
            ? (_catalog.LoadDatLines().FirstOrDefault(dl => dl.Id == datLineId)?.StorageStrategyId ?? "")
            : "";

        var progressDialog = new IngestionProgressDialog($"Ingest Files — {info.Name}");
        var progress       = new Progress<IngestionProgress>(p => progressDialog.Update(p));
        IngestionResult? ingestResult = null;

        var workTask = System.Threading.Tasks.Task.Run(() =>
            ingestResult = RunIngestionWork(platformId, datLineId, absDbPath, storageStrategyId, progress));

        _ = workTask.ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (t.IsFaulted)
                    progressDialog.SetFailed(
                        t.Exception!.InnerException?.Message ?? t.Exception.Message);
                else
                    progressDialog.SetCompleted(ingestResult!);
            }),
            System.Threading.Tasks.TaskContinuationOptions.None);

        await progressDialog.ShowDialog<bool>(this);
        await workTask;

        if (!workTask.IsCompletedSuccessfully || ingestResult is null) return;

        if (_catalog.GetBoolSetting("auto_export_ingestion_logs", defaultValue: true))
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs", "ingest");
            WriteIngestionLog(logsDir, datLineId, ingestResult);
        }

        if (ingestResult.Success && ingestResult.ReleasesPresent > 0)
        {
            RebuildLibraryDatasets();
            ResolveFlagImages();
            RefreshSystemsKeepSelection(platformId);
            RefreshPending();
        }

        if (ingestResult.Success && (ingestResult.FilesCopied > 0 || ingestResult.FilesSkipped > 0 || ingestResult.ReleasesPresent > 0))
        {
            RefreshStaging();
        }
    }

    private IngestionResult RunIngestionWork(
        string                       platformId,
        string                       datLineId,
        string                       absDbPath,
        string                       storageStrategyId,
        IProgress<IngestionProgress> progress,
        Func<string, bool>?          shouldIngest = null)
    {
        var result  = new IngestionResult();
        var appRoot = AppContext.BaseDirectory;

        var incomingDir = Path.Combine(appRoot, "incoming-roms",  platformId);
        var stagingRoot = Path.Combine(appRoot, "staging",        platformId, datLineId);
        var sourceRoot  = Path.Combine(appRoot, "source",         platformId, datLineId);
        var skipDir     = Path.Combine(appRoot, "incoming-skip");

        Directory.CreateDirectory(incomingDir);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(skipDir);

        // ── Guard: check transform strategy before doing any real work ─────────
        {
            var dlRecord = _catalog.LoadDatLines().FirstOrDefault(dl => dl.Id == datLineId);
            if (dlRecord?.TransformStrategyType == "release_folder")
            {
                const string msg = "Release-folder transform strategy is not yet implemented for runtime ingest. " +
                                   "Change the DAT line's Transform Strategy to 'None' or 'Per file extension' to proceed.";
                result.Error = msg;
                result.Operations.Add(new IngestionOperation("(all files)", "aborted-release-folder-not-implemented", msg));
                return result;
            }
        }

        // ── Pre-Ingest: Extract archives ──────────────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Pre-ingest: extracting archives…" });
        RunPreIngest(incomingDir, result, progress);

        // ── Phase 1: Scan ─────────────────────────────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Scanning incoming files…" });

        var sourceFiles = Directory.GetFiles(incomingDir, "*", SearchOption.AllDirectories).ToList();
        result.FilesScanned = sourceFiles.Count;

        if (sourceFiles.Count == 0)
            return result;

        var store = new DatLineStore(absDbPath);

        // ── Build hash indexes from non-outdated release files ─────────────────
        var releases = store.LoadReleases()
            .Where(r => r.Status != "outdated")
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var allReleaseFiles = store.LoadAllReleaseFiles();

        // sha1/md5 → list of (releaseId, romName)
        var sha1Index = new Dictionary<string, List<(string ReleaseId, string RomName)>>(
            StringComparer.OrdinalIgnoreCase);
        var md5Index  = new Dictionary<string, List<(string ReleaseId, string RomName)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (releaseId, files) in allReleaseFiles)
        {
            if (!releases.ContainsKey(releaseId)) continue;
            foreach (var f in files)
            {
                if (f.Sha1.Length > 0)
                {
                    if (!sha1Index.TryGetValue(f.Sha1, out var sl)) { sl = new(); sha1Index[f.Sha1] = sl; }
                    sl.Add((releaseId, f.RomName));
                }
                if (f.Md5.Length > 0)
                {
                    if (!md5Index.TryGetValue(f.Md5, out var ml)) { ml = new(); md5Index[f.Md5] = ml; }
                    ml.Add((releaseId, f.RomName));
                }
            }
        }

        // ── Phase 2+3: Hash & Match ───────────────────────────────────────────
        progress.Report(new IngestionProgress
        {
            PhaseText       = "Hashing and matching files…",
            IsIndeterminate = false,
            Total           = sourceFiles.Count,
        });

        // sourcePath → list of (releaseId, romName)
        var copyPlan = new Dictionary<string, List<(string ReleaseId, string RomName)>>(
            StringComparer.OrdinalIgnoreCase);

        int hashProcessed = 0;
        foreach (var srcPath in sourceFiles)
        {
            hashProcessed++;
            string sha1 = "";
            string md5  = "";

            try
            {
                using var fs = File.OpenRead(srcPath);
                sha1 = Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
            }
            catch { /* unreadable — will be skipped */ }

            // If a filter predicate was supplied (e.g. from Repair), skip files
            // whose SHA1 is not in the required set.
            if (shouldIngest != null && sha1.Length > 0 && !shouldIngest(sha1))
            {
                progress.Report(new IngestionProgress
                {
                    PhaseText       = "Hashing and matching files…",
                    IsIndeterminate = false,
                    Total           = sourceFiles.Count,
                    Processed       = hashProcessed,
                    Accepted        = result.FilesMatched,
                    Rejected        = hashProcessed - result.FilesMatched,
                });
                continue;
            }

            List<(string ReleaseId, string RomName)>? matches = null;

            if (sha1.Length > 0 && sha1Index.TryGetValue(sha1, out var sha1Matches))
            {
                matches = sha1Matches;
            }
            else if (sha1.Length > 0)
            {
                // SHA1 computed but no match — try MD5
                try
                {
                    using var fs = File.OpenRead(srcPath);
                    md5 = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
                }
                catch { }

                if (md5.Length > 0 && md5Index.TryGetValue(md5, out var md5Matches))
                    matches = md5Matches;
            }

            if (matches is { Count: > 0 })
            {
                copyPlan[srcPath] = matches;
                result.FilesMatched++;
            }

            // Emit a per-file hash operation so the dialog log stays active.
            var hashOp = new IngestionOperation(Path.GetFileName(srcPath), "hash", "incoming-roms");
            progress.Report(new IngestionProgress
            {
                PhaseText       = "Hashing and matching files…",
                IsIndeterminate = false,
                Total           = sourceFiles.Count,
                Processed       = hashProcessed,
                Accepted        = result.FilesMatched,
                Rejected        = hashProcessed - result.FilesMatched,
                NewOperation    = hashOp,
            });
        }

        // ── Phase 4b: Pre-compute satisfied targets ───────────────────────────────
        // Key: "releaseId|romName". A target is satisfied if equivalent content
        // already resides at the staging destination OR the source destination.
        // Source takes precedence as source of truth for already-acquired content.
        //
        // Content equivalence for source: we trust Arkadia's own source — files
        // there were placed after a verified completeness check. We do a size sanity
        // check against the DAT-declared size when available; if sizes differ the
        // source file is not trusted and the target is left unsatisfied.

        // Build a fast lookup: "releaseId|romName" → expected size (from release_files).
        var expectedSizeIndex = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (rid, files) in allReleaseFiles)
        {
            foreach (var f in files)
            {
                if (long.TryParse(f.Size, out var sz) && sz > 0)
                    expectedSizeIndex[$"{rid}|{f.RomName}"] = sz;
            }
        }

        var satisfiedTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var destinations in copyPlan.Values)
        {
            foreach (var (releaseId, romName) in destinations)
            {
                var key        = $"{releaseId}|{romName}";
                if (satisfiedTargets.Contains(key)) continue;

                var relName    = releases.TryGetValue(releaseId, out var rel) ? rel.Name : releaseId;
                var safeFolder = SafeFileName(relName);

                // Check staging first (in-progress or resumed run).
                var stagingPath = Path.Combine(stagingRoot, safeFolder, romName);
                if (File.Exists(stagingPath))
                {
                    satisfiedTargets.Add(key);
                    continue;
                }

                // Check source (already fully acquired in a previous run).
                var sourcePath = Path.Combine(sourceRoot, safeFolder, romName);
                if (!File.Exists(sourcePath)) continue;

                // Verify size against DAT expectation when available.
                if (expectedSizeIndex.TryGetValue(key, out var expectedSize))
                {
                    try
                    {
                        if (new FileInfo(sourcePath).Length == expectedSize)
                            satisfiedTargets.Add(key);
                        // Size mismatch → source file is suspect; leave target unsatisfied
                        // so a fresh copy goes to staging and will overwrite via Phase 7.
                    }
                    catch { /* can't stat → leave unsatisfied */ }
                }
                else
                {
                    // No expected size in DAT (older import) → trust source existence.
                    satisfiedTargets.Add(key);
                }
            }
        }

        // ── Phase 5: Space preflight ──────────────────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Checking disk space…" });

        long bytesNeeded = 0;
        foreach (var (srcPath, destinations) in copyPlan)
        {
            long srcLen = 0;
            try { srcLen = new FileInfo(srcPath).Length; } catch { }
            int pendingCount = destinations.Count(d => !satisfiedTargets.Contains($"{d.ReleaseId}|{d.RomName}"));
            bytesNeeded += srcLen * pendingCount;
        }
        bytesNeeded += 256L * 1024 * 1024; // 256 MB safety buffer

        try
        {
            var stagingDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(stagingRoot))!);
            if (stagingDrive.AvailableFreeSpace < bytesNeeded)
            {
                result.Error = $"Insufficient disk space. " +
                               $"Need {bytesNeeded / (1024 * 1024):N0} MB, " +
                               $"have {stagingDrive.AvailableFreeSpace / (1024 * 1024):N0} MB free.";
                return result;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // DriveInfo unavailable — proceed without the check
        }

        // ── Phase 6: Copy to staging ──────────────────────────────────────────
        // copyTotal counts only targets not already satisfied.
        int copyTotal = copyPlan.Values.Sum(
            dests => dests.Count(d => !satisfiedTargets.Contains($"{d.ReleaseId}|{d.RomName}")));

        progress.Report(new IngestionProgress
        {
            PhaseText       = "Copying to staging…",
            IsIndeterminate = false,
            Total           = copyTotal > 0 ? copyTotal : 1,
            Processed       = 0,
        });

        // successfullyCopied: source files whose every pending target was copied OK.
        // allTargetsSatisfied: source files that had no pending targets at all (already done).
        var successfullyCopied   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allTargetsSatisfied  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedReleaseIds   = new HashSet<string>(StringComparer.Ordinal);
        int copyCount = 0;

        foreach (var (srcPath, destinations) in copyPlan)
        {
            var srcInfo = new FileInfo(srcPath);

            // Filter to targets not yet satisfied.
            var pending = destinations
                .Where(d => !satisfiedTargets.Contains($"{d.ReleaseId}|{d.RomName}"))
                .ToList();

            if (pending.Count == 0)
            {
                // Every target for this source is already covered — treat as skipped.
                allTargetsSatisfied.Add(srcPath);
                continue;
            }

            bool anyFailed = false;

            foreach (var (releaseId, romName) in pending)
            {
                var relName    = releases.TryGetValue(releaseId, out var rel) ? rel.Name : releaseId;
                var safeFolder = SafeFileName(relName);
                var stagingDir = Path.Combine(stagingRoot, safeFolder);
                var destPath   = Path.Combine(stagingDir, romName);

                Directory.CreateDirectory(stagingDir);

                try
                {
                    File.Copy(srcPath, destPath, overwrite: true);

                    if (new FileInfo(destPath).Length != srcInfo.Length)
                        throw new IOException($"Size mismatch after copy for {romName}");

                    // Mark this target satisfied so no later file re-copies it.
                    satisfiedTargets.Add($"{releaseId}|{romName}");
                    affectedReleaseIds.Add(releaseId);
                    result.FilesCopied++;
                    copyCount++;

                    var op = new IngestionOperation(
                        srcInfo.Name, "copy",
                        $"staging/{platformId}/{datLineId}/{safeFolder}/{romName}");
                    result.Operations.Add(op);

                    if (copyCount % 25 == 0 || copyCount == copyTotal)
                        progress.Report(new IngestionProgress
                        {
                            PhaseText       = "Copying to staging…",
                            IsIndeterminate = false,
                            Total           = copyTotal > 0 ? copyTotal : 1,
                            Processed       = copyCount,
                            Accepted        = result.FilesMatched,
                            Rejected        = 0,
                            NewOperation    = op,
                        });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    anyFailed = true;
                    var op = new IngestionOperation(srcInfo.Name, "copy-failed", ex.Message);
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
            }

            if (!anyFailed)
                successfullyCopied.Add(srcPath);
        }

        // ── Phase 7: Completeness check + source promotion ────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Checking completeness…" });

        var now = DateTime.UtcNow;

        // Resolve transform + tool once for this ingestion run
        var allTransforms = _catalog.LoadTransforms();
        var allTools      = _catalog.LoadTools();
        var activeXform   = allTransforms.FirstOrDefault(t => t.Id == storageStrategyId)
                         ?? new TransformRecord { Id = "no_compression", Name = "No Compression", CommandTemplate = "" };
        var activeTool    = allTools.FirstOrDefault(t => t.Id == activeXform.ToolId);

        // Load transform strategy for this DAT line (file_extension dispatch)
        var datLineStrategyType = "none";
        var extMappingDict      = new Dictionary<string, ExtensionTransformMapping>(StringComparer.OrdinalIgnoreCase);
        {
            var dlRecord = _catalog.LoadDatLines().FirstOrDefault(dl => dl.Id == datLineId);
            if (dlRecord?.TransformStrategyType == "file_extension")
            {
                datLineStrategyType = "file_extension";
                foreach (var m in _catalog.LoadExtensionMappings(datLineId))
                    extMappingDict[m.FileExtension] = m;
            }
        }

        foreach (var releaseId in affectedReleaseIds)
        {
            if (!releases.TryGetValue(releaseId, out var release)) continue;

            var safeFolder  = SafeFileName(release.Name);
            var stagingDir  = Path.Combine(stagingRoot, safeFolder);
            var sourceDir   = Path.Combine(sourceRoot,  safeFolder);
            var expectedFiles = store.LoadReleaseFiles(releaseId);

            if (expectedFiles.Count == 0) continue;

            bool complete = expectedFiles.All(f =>
                File.Exists(Path.Combine(stagingDir, f.RomName)));

            if (!complete) continue;

            // Move every file from staging → source
            bool sourceOk = true;
            try
            {
                Directory.CreateDirectory(sourceDir);
                foreach (var f in expectedFiles)
                {
                    var src  = Path.Combine(stagingDir, f.RomName);
                    var dest = Path.Combine(sourceDir,  f.RomName);
                    File.Move(src, dest, overwrite: true);
                }
                // Remove empty staging folder (best-effort)
                if (!Directory.EnumerateFileSystemEntries(stagingDir).Any())
                    try { Directory.Delete(stagingDir); } catch { }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sourceOk = false;
                var failOp = new IngestionOperation(release.Name, "source-failed", ex.Message);
                result.Operations.Add(failOp);
                progress.Report(new IngestionProgress { NewOperation = failOp });
            }

            if (!sourceOk) continue;

            // ── Per-file: verify source + persist provenance + transform ─────
            foreach (var f in expectedFiles)
            {
                // ── Strategy dispatch: resolve effective transform for this file ──
                TransformRecord effectiveXform;
                ToolRecord?     effectiveTool;
                string          effectiveStratId;
                // isDiscarded = true means the file's source hashes ARE computed and saved,
                // but no derived artifact is produced (the file is covered by another file's transform).
                bool   isDiscarded   = false;
                string discardReason = "";

                if (datLineStrategyType == "file_extension")
                {
                    var fileExt = Path.GetExtension(f.RomName).ToLowerInvariant();
                    if (fileExt.Length == 0) fileExt = "(no ext)";

                    if (!extMappingDict.TryGetValue(fileExt, out var mapping))
                    {
                        var skipOp = new IngestionOperation(f.RomName, "skipped-no-strategy",
                            $"No transform mapping for extension {fileExt}");
                        result.Operations.Add(skipOp);
                        progress.Report(new IngestionProgress { NewOperation = skipOp });
                        continue;
                    }

                    if (mapping.IsDiscard)
                    {
                        // Flag for discard — do NOT continue yet; source hashes are still computed
                        // and persisted so SOURCE ✔ can be evaluated in the Library detail pane.
                        isDiscarded      = true;
                        discardReason    = $"Extension {fileExt} is set to Discard";
                        effectiveXform   = new TransformRecord();   // unused for discard path
                        effectiveTool    = null;
                        effectiveStratId = "none";
                    }
                    else
                    {
                        var mappedXform = allTransforms.FirstOrDefault(t => t.Id == mapping.TransformId);
                        if (mappedXform == null)
                        {
                            var skipOp = new IngestionOperation(f.RomName, "skipped-transform-missing",
                                $"Mapped transform '{mapping.TransformId}' not found for extension {fileExt}");
                            result.Operations.Add(skipOp);
                            progress.Report(new IngestionProgress { NewOperation = skipOp });
                            continue;
                        }

                        effectiveXform   = mappedXform;
                        effectiveTool    = allTools.FirstOrDefault(t => t.Id == mappedXform.ToolId);
                        effectiveStratId = mappedXform.Id == "no_compression" ? "none" : mappedXform.Id;
                    }
                }
                else
                {
                    effectiveXform   = activeXform;
                    effectiveTool    = activeTool;
                    effectiveStratId = storageStrategyId.Length > 0 ? storageStrategyId : "none";
                }

                var sourceFilePath = Path.Combine(sourceDir, f.RomName);
                long fileSize      = 0;
                try { fileSize = new FileInfo(sourceFilePath).Length; } catch { }

                // content_identity_key = DAT-declared logical identity (never from hashing)
                var ck = f.Sha1.Length > 0 ? $"sha1:{f.Sha1}"
                       : f.Md5.Length  > 0 ? $"md5:{f.Md5}"
                       : "";

                if (ck.Length == 0)
                {
                    // No DAT checksum — cannot establish logical identity; skip this file.
                    var skipOp = new IngestionOperation(f.RomName, "skipped-no-checksum", "DAT provides no SHA1 or MD5");
                    result.Operations.Add(skipOp);
                    progress.Report(new IngestionProgress { NewOperation = skipOp });
                    continue;
                }

                try
                {
                    // ── 1. Ensure content identity row (DAT-declared layer) ────
                    store.EnsureContentIdentity(new Data.ContentIdentityRecord
                    {
                        ContentIdentityKey = ck,
                        DatSha1            = f.Sha1.Length > 0 ? f.Sha1 : null,
                        DatMd5             = f.Md5.Length  > 0 ? f.Md5  : null,
                        DatCrc32           = f.Crc.Length  > 0 ? f.Crc  : null,
                        CreatedAtUtc       = now,
                    });

                    // ── 2. Hash source file physically (SHA1 + MD5 + CRC32 in one pass) ──
                    var (hashedSha1, hashedMd5, hashedCrc32) = ComputeSourceHashes(sourceFilePath);

                    // Verify: if DAT has SHA1, hashed must match.
                    // If DAT has only MD5/CRC32, SHA1 is still recorded as physical proof.
                    if (f.Sha1.Length > 0 &&
                        !string.Equals(hashedSha1, f.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        var failOp = new IngestionOperation(f.RomName, "verify-failed",
                            $"SHA1 mismatch: expected {f.Sha1[..8]}… got {hashedSha1[..8]}…");
                        result.Operations.Add(failOp);
                        progress.Report(new IngestionProgress { NewOperation = failOp });
                        continue;
                    }

                    // ── 3. Persist source artifact as provenance proof ─────────
                    var srcArtifactId = Guid.NewGuid().ToString("N");
                    store.SaveSourceArtifact(new Data.SourceArtifactRecord
                    {
                        Id                 = srcArtifactId,
                        ContentIdentityKey = ck,
                        SourceSizeBytes    = fileSize,
                        HashedSourceSha1   = hashedSha1,
                        HashedSourceMd5    = hashedMd5,
                        HashedSourceCrc32  = hashedCrc32,
                        VerifiedAtUtc      = now,
                    });

                    // Resolve actual source artifact id (INSERT OR IGNORE means existing id wins)
                    srcArtifactId = store.GetSourceArtifactIdByContentKey(ck) ?? srcArtifactId;

                    // Discarded files: source provenance is now recorded; skip transform entirely.
                    if (isDiscarded)
                    {
                        var discardOp = new IngestionOperation(f.RomName, "discarded-by-strategy", discardReason);
                        result.Operations.Add(discardOp);
                        progress.Report(new IngestionProgress { NewOperation = discardOp });
                        continue;
                    }

                    // ── 4. Transform: produce derived archive file ────────────
                    var archiveDir = Path.Combine(appRoot, "archive", platformId, datLineId, safeFolder);
                    Directory.CreateDirectory(archiveDir);
                    var outputExt = effectiveXform.OutputExtension.Length > 0 ? effectiveXform.OutputExtension : "";
                    var destName  = outputExt.Length > 0
                        ? Path.GetFileNameWithoutExtension(f.RomName) + outputExt
                        : f.RomName;
                    var destPath = Path.Combine(archiveDir, destName);
                    var relPath  = $"archive/{platformId}/{datLineId}/{safeFolder}/{destName}";

                    if (!File.Exists(destPath))
                    {
                        if (!TransformEngine.ExecuteTransform(effectiveXform, effectiveTool, appRoot, sourceFilePath, destPath, out var xformError))
                            throw new InvalidOperationException($"Transform failed: {xformError}");
                    }

                    // Log transform step (skip no_compression — that's a plain copy, already captured)
                    if (effectiveXform.Id != "no_compression")
                    {
                        var xformOp = new IngestionOperation(f.RomName, "transform", $"{effectiveXform.Name} → {destName}");
                        result.Operations.Add(xformOp);
                        progress.Report(new IngestionProgress { NewOperation = xformOp });
                    }

                    // ── 5. Hash derived file independently (single pass: SHA1 + MD5 + CRC32) ─
                    // Physical hashes of the stored derived file; never compared to DAT hashes.
                    var (hashedDerivedSha1, hashedDerivedMd5, hashedDerivedCrc32) =
                        ComputeSourceHashes(destPath);

                    // ── 6. Persist/upsert derived artifact ────────────────────
                    store.IngestDerivedArtifact(
                        contentIdentityKey: ck,
                        sourceArtifactId:   srcArtifactId,
                        storageStrategyId:  effectiveStratId,
                        fileName:           destName,
                        relativePath:       relPath,
                        derivedSizeBytes:   new FileInfo(destPath).Length,
                        hashedDerivedSha1:  hashedDerivedSha1,
                        hashedDerivedMd5:   hashedDerivedMd5,
                        hashedDerivedCrc32: hashedDerivedCrc32);

                    // ── 7. Link release → content identity ────────────────────
                    store.SaveReleaseContentLink(new Data.ReleaseContentLinkRecord
                    {
                        Id                 = Guid.NewGuid().ToString("N"),
                        ReleaseId          = releaseId,
                        ContentIdentityKey = ck,
                        CreatedAtUtc       = now,
                    });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Transform failures must never block the rest of ingestion.
                    var failOp = new IngestionOperation(f.RomName, "transform-failed", ex.Message);
                    result.Operations.Add(failOp);
                    progress.Report(new IngestionProgress { NewOperation = failOp });
                }
            }

            store.UpdateReleaseStatus(releaseId, "present");
            result.ReleasesPresent++;

            var archOp = new IngestionOperation(
                release.Name, "source",
                $"source/{platformId}/{datLineId}/{safeFolder}");
            result.Operations.Add(archOp);
            progress.Report(new IngestionProgress { NewOperation = archOp });
        }

        // ── Phase 8: Source file handling ─────────────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Handling source files…" });

        foreach (var srcPath in sourceFiles)
        {
            var fileName = Path.GetFileName(srcPath);

            if (successfullyCopied.Contains(srcPath))
            {
                // All pending targets were copied successfully → delete source.
                try
                {
                    File.Delete(srcPath);
                    var op = new IngestionOperation(fileName, "delete", "incoming-roms");
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
                catch
                {
                    var op = new IngestionOperation(fileName, "delete-failed", "source file could not be removed");
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
            }
            else if (allTargetsSatisfied.Contains(srcPath))
            {
                // Equivalent content already covered all targets — delete duplicate directly.
                result.FilesSkipped++;
                try
                {
                    File.Delete(srcPath);
                    var op = new IngestionOperation(fileName, "duplicate-deleted", "incoming-roms");
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
                catch
                {
                    var op = new IngestionOperation(fileName, "duplicate-delete-failed", "duplicate source file could not be removed");
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
            }
            else
            {
                result.FilesSkipped++;
                var destPath = IncomingSkipUniquePath(skipDir, fileName);
                try
                {
                    File.Move(srcPath, destPath, overwrite: false);
                    var op = new IngestionOperation(fileName, "skip", $"incoming-skip/{Path.GetFileName(destPath)}");
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
                catch
                {
                    var op = new IngestionOperation(fileName, "skip-failed", "could not move to incoming-skip");
                    result.Operations.Add(op);
                    progress.Report(new IngestionProgress { NewOperation = op });
                }
            }
        }

        // ── Post-Ingest: Remove empty directories ─────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Post-ingest: cleaning empty directories…" });
        RunPostIngest(incomingDir);

        return result;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var sanitized = sb.ToString().Trim('_', ' ');
        return sanitized.Length > 0 ? sanitized : "release";
    }

    private static string IncomingSkipUniquePath(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext            = Path.GetExtension(fileName);
        int counter        = 1;
        while (true)
        {
            dest = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
            if (!File.Exists(dest)) return dest;
            counter++;
        }
    }

    /// <summary>
    /// Pre-ingest: extract .zip and .7z archives found recursively under <paramref name="incomingDir"/>.
    /// Extraction target is a sibling folder named after the archive without its final extension.
    /// Deletes the archive only on full extraction success.
    /// Skips archives where free space is insufficient.
    /// </summary>
    private static void RunPreIngest(
        string          incomingDir,
        IngestionResult result,
        IProgress<IngestionProgress> progress)
    {
        var archives = Directory
            .EnumerateFiles(incomingDir, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                return string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, ".7z",  StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (archives.Count == 0) return;

        foreach (var archivePath in archives)
        {
            var archiveName = Path.GetFileName(archivePath);

            // Destination folder = sibling named after archive without final extension
            var parentDir   = Path.GetDirectoryName(archivePath)!;
            var folderBase  = Path.GetFileNameWithoutExtension(archivePath);

            // Collision-safe folder resolution
            var destFolder = Path.Combine(parentDir, folderBase);
            if (Directory.Exists(destFolder))
            {
                int suffix = 2;
                while (Directory.Exists(Path.Combine(parentDir, $"{folderBase} ({suffix})")))
                    suffix++;
                destFolder = Path.Combine(parentDir, $"{folderBase} ({suffix})");
            }

            var folderName = Path.GetFileName(destFolder);

            // Check decompressed size and free space
            long decompressedSize = 0;
            try
            {
                using var af = SharpCompress.Archives.ArchiveFactory.Open(archivePath);
                decompressedSize = af.Entries
                    .Where(e => !e.IsDirectory)
                    .Sum(e => e.Size);
            }
            catch (Exception ex)
            {
                var failOp = new IngestionOperation(archiveName, "extract-failed",
                    $"could not read archive: {ex.Message}");
                result.Operations.Add(failOp);
                progress.Report(new IngestionProgress { NewOperation = failOp });
                continue;
            }

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(destFolder)!);
                if (decompressedSize > 0 && decompressedSize > drive.AvailableFreeSpace)
                {
                    var skipOp = new IngestionOperation(archiveName, "extract-skipped",
                        $"insufficient space: need {decompressedSize} bytes, " +
                        $"have {drive.AvailableFreeSpace}");
                    result.Operations.Add(skipOp);
                    progress.Report(new IngestionProgress { NewOperation = skipOp });
                    continue;
                }
            }
            catch { /* DriveInfo unavailable — proceed; extraction will fail naturally if truly out of space */ }

            // Extract
            try
            {
                Directory.CreateDirectory(destFolder);
                var fullDestRoot = Path.GetFullPath(destFolder) + Path.DirectorySeparatorChar;

                // Explicit using-block so fileStream and reader are fully disposed
                // before File.Delete runs. 'using var' would defer disposal to end
                // of the try-block, keeping the handle open during the delete call.
                using (var fileStream = File.OpenRead(archivePath))
                using (var reader = SharpCompress.Readers.ReaderFactory.Open(fileStream))
                {
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory) continue;
                        var key      = reader.Entry.Key ?? "";
                        var relPath  = key.Replace('\\', Path.DirectorySeparatorChar)
                                          .Replace('/',  Path.DirectorySeparatorChar)
                                          .TrimStart(Path.DirectorySeparatorChar);
                        var fullPath = Path.GetFullPath(Path.Combine(destFolder, relPath));
                        // path traversal guard
                        if (!fullPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                            continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        using var outStream   = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
                        using var entryStream = reader.OpenEntryStream();
                        entryStream.CopyTo(outStream);
                    }
                } // fileStream and reader disposed here — archive handle fully closed

                var okOp = new IngestionOperation(archiveName, "extract-ok", folderName);
                result.Operations.Add(okOp);
                progress.Report(new IngestionProgress { NewOperation = okOp });

                // Delete archive only on full success — safe because handle is closed above
                File.Delete(archivePath);
                var delOp = new IngestionOperation(archiveName, "archive-deleted", "incoming-roms");
                result.Operations.Add(delOp);
                progress.Report(new IngestionProgress { NewOperation = delOp });
            }
            catch (Exception ex)
            {
                // Leave archive untouched; clean up partially-created dest folder if empty
                try
                {
                    if (Directory.Exists(destFolder) &&
                        !Directory.EnumerateFileSystemEntries(destFolder).Any())
                        Directory.Delete(destFolder);
                }
                catch { /* best-effort */ }

                var failOp = new IngestionOperation(archiveName, "extract-failed", ex.Message);
                result.Operations.Add(failOp);
                progress.Report(new IngestionProgress { NewOperation = failOp });
            }
        }
    }

    /// <summary>
    /// Post-ingest: recursively delete empty directories under <paramref name="incomingDir"/>,
    /// bottom-up. The platform root itself is preserved even if it becomes empty.
    /// </summary>
    private static void RunPostIngest(string incomingDir)
    {
        // Enumerate all subdirectories, deepest first (bottom-up via OrderByDescending on path length)
        var dirs = Directory
            .EnumerateDirectories(incomingDir, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();

        foreach (var dir in dirs)
        {
            // Skip if it was already removed (a parent was removed in a prior iteration)
            if (!Directory.Exists(dir)) continue;

            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try { Directory.Delete(dir); }
                catch { /* best-effort */ }
            }
        }
        // incomingDir itself is never deleted
    }

    /// <summary>
    /// Removes empty directories bottom-up under <paramref name="root"/>.
    /// <paramref name="root"/> itself is never deleted.
    /// </summary>
    private static void PruneEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        var dirs = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try { Directory.Delete(dir); }
                catch { /* best-effort */ }
            }
        }
    }

    private static void WriteIngestionLog(
        string          logsDir,
        string          datLineId,
        IngestionResult result)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            var ts   = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safe = SafeFileName(datLineId);
            var path = Path.Combine(logsDir, $"{ts}-ingest-{safe}.log");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ARKADIA INGESTION LOG");
            sb.AppendLine($"Date:         {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"DAT Line ID:  {datLineId}");
            sb.AppendLine();
            sb.AppendLine("COUNTS");
            sb.AppendLine($"  Files scanned:    {result.FilesScanned}");
            sb.AppendLine($"  Files matched:    {result.FilesMatched}");
            sb.AppendLine($"  Files copied:     {result.FilesCopied}");
            sb.AppendLine($"  Releases present: {result.ReleasesPresent}");
            sb.AppendLine($"  Files skipped:    {result.FilesSkipped}");
            sb.AppendLine();
            sb.AppendLine("OPERATIONS");
            foreach (var op in result.Operations)
                sb.AppendLine($"  {op.Object,-50} | {op.Action,-14} | {op.Destination}");
            sb.AppendLine();
            sb.AppendLine(result.Success ? "RESULT: SUCCESS" : $"RESULT: FAILED — {result.Error}");

            File.WriteAllText(path, sb.ToString());
        }
        catch { /* log failure is non-fatal */ }
    }

    private async void OnCreatePlatform(object? sender, RoutedEventArgs e)
    {
        var existingIds   = _catalog.LoadPlatforms().Select(p => p.Id);
        var hardwareTypes = _catalog.LoadHardwareTypes();
        var dialog        = new CreatePlatformDialog(existingIds, null, Path.Combine(_dataDir, "systemimages"), hardwareTypes);
        var confirmed   = await dialog.ShowDialog<bool>(this);
        if (!confirmed || dialog.CreatedPlatform is null) return;

        var platform = dialog.CreatedPlatform;
        _catalog.SavePlatforms([platform]);

        // Copy images into data/systemimages/
        var imageDir = Path.Combine(_dataDir, "systemimages");
        Directory.CreateDirectory(imageDir);

        if (dialog.LogoImagePath is not null)
            File.Copy(dialog.LogoImagePath,
                Path.Combine(imageDir, $"{platform.Id}-logo.png"), overwrite: true);
        if (dialog.DetailsImagePath is not null)
            File.Copy(dialog.DetailsImagePath,
                Path.Combine(imageDir, $"{platform.Id}-details.png"), overwrite: true);

        RefreshSystemsKeepSelection(platform.Id);
    }

    private async void OnEditPlatform(object? sender, RoutedEventArgs e)
    {
        var platform = _selectedPlatform;
        if (platform is null) return;

        var existing = _catalog.LoadPlatforms().FirstOrDefault(p => p.Id == platform.Id);
        if (existing is null) return;

        var otherIds      = _catalog.LoadPlatforms().Select(p => p.Id).Where(id => id != existing.Id);
        var imageDir      = Path.Combine(_dataDir, "systemimages");
        var hardwareTypes = _catalog.LoadHardwareTypes();
        var dialog        = new CreatePlatformDialog(otherIds, existing, imageDir, hardwareTypes);
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed || dialog.CreatedPlatform is null) return;

        _catalog.SavePlatforms([dialog.CreatedPlatform]);

        Directory.CreateDirectory(imageDir);

        // Apply image deletions
        if (dialog.DeleteLogoImage)
        {
            var p = Path.Combine(imageDir, $"{existing.Id}-logo.png");
            if (File.Exists(p)) File.Delete(p);
        }
        if (dialog.DeleteDetailsImage)
        {
            var p = Path.Combine(imageDir, $"{existing.Id}-details.png");
            if (File.Exists(p)) File.Delete(p);
        }

        // Apply new images (overwrite if replacing)
        if (dialog.LogoImagePath is not null)
            File.Copy(dialog.LogoImagePath,
                Path.Combine(imageDir, $"{existing.Id}-logo.png"), overwrite: true);
        if (dialog.DetailsImagePath is not null)
            File.Copy(dialog.DetailsImagePath,
                Path.Combine(imageDir, $"{existing.Id}-details.png"), overwrite: true);

        RefreshSystemsKeepSelection(existing.Id);
    }

    private static string AuthorityLabel(string authority) => authority switch
    {
        "redump"   => "Redump",
        "no-intro" => "No-Intro",
        "tosec"    => "TOSEC",
        "custom"   => "Custom",
        _          => authority.Length > 0
                          ? char.ToUpperInvariant(authority[0]) + authority[1..]
                          : authority,
    };

    private async void OnImportDat(object? sender, RoutedEventArgs e)
    {
        var platforms         = _catalog.LoadPlatforms();
        var storageStrategies = _catalog.LoadStorageStrategies();
        var existingDatLines  = _catalog.LoadDatLines();
        var importDialog      = new ImportDatDialog(platforms, storageStrategies, existingDatLines);
        var ok                = await importDialog.ShowDialog<bool>(this);
        if (!ok) return;

        var platformId        = importDialog.PlatformId        ?? "";
        var authority         = importDialog.Authority         ?? "";
        var datCategory       = importDialog.DatCategory       ?? "";
        var datLineId         = importDialog.DatLineId         ?? "";
        var datLineName       = $"{AuthorityLabel(authority)}: {datCategory}";
        var version           = importDialog.Version           ?? "";
        var storageStrategyId = importDialog.StorageStrategyId ?? "";
        var parsedGames       = importDialog.ParsedGames.ToList();

        var relPath = $"systems/{platformId}/{datLineId}.db";
        var absPath = Path.Combine(_dataDir, relPath);

        var newDatLineRecord = new DatLineRecord
        {
            Id                = datLineId,
            PlatformId        = platformId,
            Name              = datLineName,
            Authority         = authority,
            DatCategory       = datCategory,
            Version           = version,
            StorageStrategyId = storageStrategyId,
            DataStorePath     = relPath,
            ReleaseCount      = parsedGames.Count,
            ImportedAtUtc     = DateTime.UtcNow,
        };

        var progressDialog = new DatOperationProgressDialog("Import DAT");
        var progress       = new Progress<DatOperationProgress>(p => progressDialog.Update(p));
        int imported       = 0;

        var workTask = System.Threading.Tasks.Task.Run(() =>
            imported = RunImportWork(absPath, datLineId, parsedGames, existingDatLines, newDatLineRecord, progress));

        _ = workTask.ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (t.IsFaulted)
                    progressDialog.SetFailed(t.Exception!.InnerException?.Message ?? t.Exception.Message);
                else
                    progressDialog.SetImportCompleted(
                        _catalog.GetPlatform(platformId)?.Name ?? platformId,
                        datLineId,
                        parsedGames.Count,
                        imported,
                        parsedGames.Count - imported);
            }),
            System.Threading.Tasks.TaskContinuationOptions.None);

        await progressDialog.ShowDialog<bool>(this);
        await workTask;

        if (workTask.IsCompletedSuccessfully)
        {
            RebuildLibraryDatasets();
            ResolveFlagImages();
            RefreshSystemsKeepSelection(platformId);
        }
    }

    private int RunImportWork(
        string                      absPath,
        string                      datLineId,
        List<DatParser.ParsedGame>  parsedGames,
        List<DatLineRecord>         existingDatLines,
        DatLineRecord               newDatLineRecord,
        IProgress<DatOperationProgress> progress)
    {
        progress.Report(new DatOperationProgress
        {
            PhaseText       = "Building releases…",
            IsIndeterminate = true,
        });

        var releases         = new List<ReleaseRecord>(parsedGames.Count);
        var filesByReleaseId = new List<(string ReleaseId, List<Data.ReleaseFileRecord> Files)>(parsedGames.Count);

        foreach (var game in parsedGames)
        {
            var releaseId = System.Guid.NewGuid().ToString("N");
            releases.Add(new ReleaseRecord
            {
                Id         = releaseId,
                DatLineId  = datLineId,
                Name       = game.Name,
                Status     = "missing",
                Region     = game.Region,
                Languages  = game.Languages,
                ReleaseContentKey = game.ContentKey,
            });
            filesByReleaseId.Add((releaseId, ToReleaseFiles(releaseId, game.Roms)));
        }

        progress.Report(new DatOperationProgress
        {
            PhaseText       = "Saving releases…",
            IsIndeterminate = true,
        });

        existingDatLines.Add(newDatLineRecord);
        _catalog.SaveDatLines(existingDatLines);

        var store = new DatLineStore(absPath);
        store.SaveReleases(releases);

        var count = filesByReleaseId.Count;

        progress.Report(new DatOperationProgress
        {
            PhaseText       = "Importing DAT entries…",
            IsIndeterminate = false,
            Total           = count,
            Processed       = 0,
            Accepted        = 0,
            Rejected        = 0,
        });

        for (int i = 0; i < count; i++)
        {
            var (rid, files) = filesByReleaseId[i];
            store.SaveReleaseFiles(rid, files);

            if (i % 25 == 0 || i == count - 1)
                progress.Report(new DatOperationProgress
                {
                    PhaseText       = "Importing DAT entries…",
                    IsIndeterminate = false,
                    Total           = count,
                    Processed       = i + 1,
                    Accepted        = i + 1,
                    Rejected        = 0,
                    CurrentItem     = releases[i].Name,
                });
        }

        return releases.Count;
    }

    private static void SetHwRow(Grid row, TextBlock valueBlock, string? value)
    {
        var hasValue      = !string.IsNullOrEmpty(value);
        row.IsVisible     = hasValue;
        valueBlock.Text   = hasValue ? value! : "";
    }

    private static string MakeSafeId(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s;
    }

    // ── Pending ──────────────────────────────────────────────────────────────

    private List<PendingItem> _pendingItems = [];

    private void InitPending() => RefreshPending();

    private void RefreshPending()
    {
        _pendingItems = BuildPendingItems();

        if (_pendingItems.Count == 0)
        {
            PendingList.IsVisible      = false;
            PendingListEmpty.IsVisible = true;
            PendingCountText.Text      = "0 pending";
        }
        else
        {
            PendingList.ItemsSource    = _pendingItems;
            PendingList.IsVisible      = true;
            PendingListEmpty.IsVisible = false;
            PendingCountText.Text      = $"{_pendingItems.Count} pending";
        }

        UpdatePendingDetailPanel(null);
    }

    private List<PendingItem> BuildPendingItems()
    {
        var allPlatforms = _catalog.LoadPlatforms();
        var allDatLines  = _catalog.LoadDatLines();
        var result       = new List<PendingItem>();

        foreach (var dl in allDatLines)
        {
            if (dl.DataStorePath.Length == 0) continue;

            var absPath = Path.Combine(_dataDir, dl.DataStorePath);
            if (!File.Exists(absPath)) continue;

            var store = new DatLineStore(absPath);
            var rows  = store.LoadPendingReconciliations("pending");
            if (rows.Count == 0) continue;

            var platformName  = allPlatforms.FirstOrDefault(p => p.Id == dl.PlatformId)?.Name ?? dl.PlatformId;
            var relById       = store.LoadReleases().ToDictionary(r => r.Id);
            var filesByRelId  = store.LoadAllReleaseFiles();

            foreach (var row in rows)
            {
                relById.TryGetValue(row.NewReleaseId,      out var newRelease);
                relById.TryGetValue(row.OutdatedReleaseId, out var outdatedRelease);

                // Skip orphaned rows: new_release_id must exist in the release set.
                // Orphans can occur if SaveReleases ran between the row being created
                // and it being loaded (e.g. re-import after an earlier update).
                if (newRelease is null) continue;

                filesByRelId.TryGetValue(row.NewReleaseId,      out var newFiles);
                filesByRelId.TryGetValue(row.OutdatedReleaseId, out var outdatedFiles);

                result.Add(new PendingItem
                {
                    ReconId            = row.Id,
                    Reason             = row.Reason,
                    CreatedAtUtc       = row.CreatedAtUtc,
                    ReconStatus        = row.Status,
                    ArtifactId         = row.ArtifactId         ?? "",
                    VolumeId           = row.VolumeId           ?? "",
                    DiskId             = row.DiskId             ?? "",
                    StoredRelativePath = row.StoredRelativePath ?? "",
                    StoredName         = row.StoredName         ?? "",
                    TargetName         = row.TargetName,
                    TargetRelativePath = row.TargetRelativePath ?? "",
                    DatLineId          = dl.Id,
                    DatLineName        = dl.Name,
                    PlatformId         = dl.PlatformId,
                    PlatformName       = platformName,
                    NewRelease         = newRelease,
                    NewRomFiles        = newFiles      ?? [],
                    OutdatedRelease    = outdatedRelease,
                    OutdatedRomFiles   = outdatedFiles ?? [],
                });
            }
        }

        return result;
    }

    private void OnPendingSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdatePendingDetailPanel(PendingList.SelectedItem as PendingItem);

    private void UpdatePendingDetailPanel(PendingItem? item)
    {
        if (item is null)
        {
            PendingDetailEmptyState.IsVisible = true;
            PendingDetailContent.IsVisible    = false;
            return;
        }

        PendingDetailReleaseName.Text = item.ReleaseName;
        PendingDetailDatLine.Text     = item.DatLineName;
        PendingDetailPlatform.Text    = item.PlatformName;
        PendingDetailReason.Text      = item.ReasonDisplay;
        PendingDetailStatus.Text      = Capitalize(item.ReconStatus);
        PendingDetailCreated.Text     = item.CreatedLabel;

        // NEW RELEASE
        PendingDetailNewRelease.Children.Clear();
        if (item.NewRelease is not null)
            PopulatePendingReleaseSection(PendingDetailNewRelease, item.NewRelease, item.NewRomFiles);
        else
            PendingDetailNewRelease.Children.Add(MakePendingSecondaryLabel("No release data"));

        // MATCHED OUTDATED RELEASE
        var hasOutdated = item.OutdatedRelease is not null;
        PendingDetailOutdatedDivider.IsVisible = hasOutdated;
        PendingDetailOutdatedTitle.IsVisible   = hasOutdated;
        PendingDetailOutdated.IsVisible        = hasOutdated;
        if (hasOutdated)
        {
            PendingDetailOutdated.Children.Clear();
            PopulatePendingReleaseSection(PendingDetailOutdated, item.OutdatedRelease!, item.OutdatedRomFiles);
        }

        // TARGET
        PendingDetailTarget.Children.Clear();
        if (item.TargetName.Length > 0)
            PendingDetailTarget.Children.Add(MakePendingDetailKV("Name", item.TargetName));
        if (item.TargetRelativePath.Length > 0)
            PendingDetailTarget.Children.Add(MakePendingDetailKV("Path", item.TargetRelativePath));
        if (PendingDetailTarget.Children.Count == 0)
            PendingDetailTarget.Children.Add(MakePendingSecondaryLabel("No target data"));

        // PHYSICAL LOCATOR
        var hasLocator = item.ArtifactId.Length > 0 || item.VolumeId.Length > 0
                      || item.DiskId.Length > 0     || item.StoredName.Length > 0
                      || item.StoredRelativePath.Length > 0;
        PendingDetailLocatorDivider.IsVisible = hasLocator;
        PendingDetailLocatorTitle.IsVisible   = hasLocator;
        PendingDetailLocator.IsVisible        = hasLocator;
        if (hasLocator)
        {
            PendingDetailLocator.Children.Clear();
            if (item.ArtifactId.Length > 0)
                PendingDetailLocator.Children.Add(MakePendingDetailKV("Artifact", item.ArtifactId));
            if (item.VolumeId.Length > 0)
                PendingDetailLocator.Children.Add(MakePendingDetailKV("Volume", item.VolumeId));
            if (item.DiskId.Length > 0)
                PendingDetailLocator.Children.Add(MakePendingDetailKV("Disk", item.DiskId));
            if (item.StoredName.Length > 0)
                PendingDetailLocator.Children.Add(MakePendingDetailKV("Stored Name", item.StoredName));
            if (item.StoredRelativePath.Length > 0)
                PendingDetailLocator.Children.Add(MakePendingDetailKV("Stored Path", item.StoredRelativePath));
        }

        PendingDetailEmptyState.IsVisible = false;
        PendingDetailContent.IsVisible    = true;
    }

    private static void PopulatePendingReleaseSection(
        StackPanel panel, ReleaseRecord release, IReadOnlyList<ReleaseFileRecord> romFiles)
    {
        var primary = new SolidColorBrush(Color.Parse("#D0D0E0"));

        panel.Children.Add(new TextBlock
        {
            Text         = release.Name,
            FontSize     = 13,
            FontWeight   = FontWeight.Medium,
            Foreground   = primary,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(MakePendingDetailKV("Status", Capitalize(release.Status)));
        if (release.Region.Length > 0)
            panel.Children.Add(MakePendingDetailKV("Region", release.Region));
        if (release.Format.Length > 0)
            panel.Children.Add(MakePendingDetailKV("Format", release.Format));
        if (release.Size.Length > 0)
            panel.Children.Add(MakePendingDetailKV("Size", release.Size));
        if (romFiles.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text       = $"{romFiles.Count} ROM file{(romFiles.Count == 1 ? "" : "s")}",
                FontSize   = 11,
                Foreground = new SolidColorBrush(Color.Parse("#888899")),
                Margin     = new Avalonia.Thickness(0, 4, 0, 0),
            });
    }

    private static Control MakePendingDetailKV(string key, string value)
    {
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var primary   = new SolidColorBrush(Color.Parse("#D0D0E0"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Margin = new Avalonia.Thickness(0, 0, 0, 6);

        var keyTb = new TextBlock { Text = key,   FontSize = 11, Foreground = secondary };
        var valTb = new TextBlock { Text = value, FontSize = 12, Foreground = primary,
                                    TextWrapping = TextWrapping.Wrap, MaxWidth = 160 };
        Grid.SetColumn(valTb, 1);
        grid.Children.Add(keyTb);
        grid.Children.Add(valTb);
        return grid;
    }

    private static TextBlock MakePendingSecondaryLabel(string text)
        => new TextBlock
        {
            Text       = text,
            FontSize   = 12,
            Foreground = new SolidColorBrush(Color.Parse("#555566")),
        };

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
            SetActive(btn);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private void InitSettings()
    {
        LoadAllSettings();
    }

    private void LoadAllSettings()
    {
        SettingQuarantineMismatch.IsChecked      = _catalog.GetBoolSetting("quarantine_mismatch_on_verify",  defaultValue: false);
        SettingQuarantineUnexpected.IsChecked    = _catalog.GetBoolSetting("quarantine_unexpected_on_verify", defaultValue: false);
        SettingAutoExportLogs.IsChecked          = _catalog.GetBoolSetting("auto_export_ingestion_logs",     defaultValue: true);
        SettingLogOnCopy.IsChecked               = _catalog.GetBoolSetting("log_on_copy",                    defaultValue: true);
        SettingAutoExportVerifyLogs.IsChecked    = _catalog.GetBoolSetting("auto_export_verify_logs",        defaultValue: true);
        SettingAutoExportRepairLogs.IsChecked    = _catalog.GetBoolSetting("auto_export_repair_logs",        defaultValue: true);
        SettingShowDebugArtifactInfo.IsChecked   = _catalog.GetBoolSetting("show_debug_artifact_info",       defaultValue: false);
    }

    private void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        _catalog.SetSetting("quarantine_mismatch_on_verify",  SettingQuarantineMismatch.IsChecked    == true ? "true" : "false");
        _catalog.SetSetting("quarantine_unexpected_on_verify", SettingQuarantineUnexpected.IsChecked  == true ? "true" : "false");
        _catalog.SetSetting("auto_export_ingestion_logs",      SettingAutoExportLogs.IsChecked        == true ? "true" : "false");
        _catalog.SetSetting("log_on_copy",                     SettingLogOnCopy.IsChecked             == true ? "true" : "false");
        _catalog.SetSetting("auto_export_verify_logs",         SettingAutoExportVerifyLogs.IsChecked  == true ? "true" : "false");
        _catalog.SetSetting("auto_export_repair_logs",         SettingAutoExportRepairLogs.IsChecked  == true ? "true" : "false");
        _catalog.SetSetting("show_debug_artifact_info",        SettingShowDebugArtifactInfo.IsChecked == true ? "true" : "false");
        // Apply show_debug_artifact_info immediately (affects current session without restart)
        _showDebugArtifactInfo = SettingShowDebugArtifactInfo.IsChecked == true;
    }

    private void OnReloadSettings(object? sender, RoutedEventArgs e)
        => LoadAllSettings();

    // ── Operations ───────────────────────────────────────────────────────────

    private List<TransformRecord>               _transforms       = [];
    private TransformRecord?                    _editingTransform;
    private Border?                             _selectedTransformBorder;
    private readonly Dictionary<string, Border> _transformBorders = new();

    private void InitOperations()
    {
        var appRoot       = AppContext.BaseDirectory;
        var tools         = _catalog.LoadTools();
        var builtInToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "7zip" };

        OperationsToolsPanel.Children.Clear();
        foreach (var tool in tools)
        {
            var exePath   = Path.Combine(appRoot, "tools", tool.FolderName, tool.ExecutableName);
            var isBuiltIn = builtInToolIds.Contains(tool.Id);
            var present   = !isBuiltIn && File.Exists(exePath);

            var statusText  = isBuiltIn ? "BUILT-IN" : (present ? "PRESENT" : "MISSING");
            var statusColor = isBuiltIn ? "#29B6F6"  : (present ? "#4CAF50"  : "#EF5350");
            var pathColor   = isBuiltIn ? "#555566"  : (present ? "#555566"  : "#EF5350");

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("180,Auto,*"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            var nameBlock = new TextBlock
            {
                Text              = tool.Id,
                FontSize          = 12,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse("#CCCCDD")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var statusBlock = new TextBlock
            {
                Text              = statusText,
                FontSize          = 10,
                FontWeight        = Avalonia.Media.FontWeight.SemiBold,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse(statusColor)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin            = new Avalonia.Thickness(0, 0, 12, 0),
            };
            var pathBlock = new TextBlock
            {
                Text              = exePath,
                FontSize          = 10,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse(pathColor)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping      = Avalonia.Media.TextWrapping.NoWrap,
            };
            Avalonia.Controls.Grid.SetColumn(nameBlock,   0);
            Avalonia.Controls.Grid.SetColumn(statusBlock, 1);
            Avalonia.Controls.Grid.SetColumn(pathBlock,   2);
            row.Children.Add(nameBlock);
            row.Children.Add(statusBlock);
            row.Children.Add(pathBlock);
            OperationsToolsPanel.Children.Add(row);
        }

        _transforms = _catalog.LoadTransforms();
        BuildTransformListPanel();
        TransformEditorPanel.IsVisible = false;
    }

    // ── Logs ─────────────────────────────────────────────────────────────────

    private void InitLogs() => BuildLogsTree();

    private void BuildLogsTree()
    {
        LogsTreePanel.Children.Clear();
        LogsContentBox.Text = "";
        LogsFileLabel.Text  = "No file selected";

        var logsRoot = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsRoot))
        {
            LogsTreePanel.Children.Add(new TextBlock
            {
                Text       = "(no logs directory)",
                FontSize   = 11,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#555566")),
                Margin     = new Avalonia.Thickness(4, 4),
            });
            return;
        }

        var folders = Directory.GetDirectories(logsRoot)
                               .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                               .ToList();

        if (folders.Count == 0)
        {
            LogsTreePanel.Children.Add(new TextBlock
            {
                Text       = "(no log folders)",
                FontSize   = 11,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#555566")),
                Margin     = new Avalonia.Thickness(4, 4),
            });
            return;
        }

        foreach (var folder in folders)
        {
            var folderName = Path.GetFileName(folder);

            // Folder header
            var folderLabel = new TextBlock
            {
                Text       = folderName + "/",
                FontSize   = 11,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#7B68EE")),
                Margin     = new Avalonia.Thickness(0, 8, 0, 2),
            };
            LogsTreePanel.Children.Add(folderLabel);

            var files = Directory.GetFiles(folder)
                                 .OrderByDescending(f => File.GetLastWriteTime(f))
                                 .ToList();

            if (files.Count == 0)
            {
                LogsTreePanel.Children.Add(new TextBlock
                {
                    Text       = "  (empty)",
                    FontSize   = 10,
                    Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#444455")),
                    Margin     = new Avalonia.Thickness(12, 0),
                });
                continue;
            }

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var filePath = file;

                var fileBtn = new Button
                {
                    Content     = fileName,
                    Tag         = filePath,
                    Background  = Avalonia.Media.Brushes.Transparent,
                    BorderThickness = new Avalonia.Thickness(0),
                    Padding     = new Avalonia.Thickness(12, 3),
                    HorizontalAlignment     = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Foreground  = new SolidColorBrush(Avalonia.Media.Color.Parse("#AAAACC")),
                    FontSize    = 11,
                    Cursor      = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                fileBtn.Click += OnLogsFileSelected;
                LogsTreePanel.Children.Add(fileBtn);
            }
        }
    }

    private void OnLogsFileSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        LogsFileLabel.Text = filePath;

        try
        {
            LogsContentBox.Text = File.ReadAllText(filePath);
        }
        catch
        {
            LogsContentBox.Text = "Failed to load log.";
        }
    }

    private void OnLogsRefresh(object? sender, RoutedEventArgs e)
        => BuildLogsTree();

    private void OnLogsOpenLatest(object? sender, RoutedEventArgs e)
    {
        var logsRoot = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsRoot))
            return;

        var latest = Directory.EnumerateFiles(logsRoot, "*", SearchOption.AllDirectories)
                              .OrderByDescending(File.GetLastWriteTime)
                              .FirstOrDefault();
        if (latest is null)
            return;

        LogsFileLabel.Text = latest;
        try
        {
            LogsContentBox.Text = File.ReadAllText(latest);
        }
        catch
        {
            LogsContentBox.Text = "Failed to load log.";
        }
    }

    private void BuildTransformListPanel(string? selectId = null)
    {
        TransformsListPanel.Children.Clear();
        _transformBorders.Clear();

        var fileItems   = _transforms.Where(t => t.TransformType != "folder_strategy").ToList();
        var folderItems = _transforms.Where(t => t.TransformType == "folder_strategy").ToList();

        void AddGroupHeader(string label)
        {
            TransformsListPanel.Children.Add(new TextBlock
            {
                Text       = label,
                FontSize   = 9,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#555566")),
                Margin     = new Avalonia.Thickness(10, 10, 10, 4),
            });
        }

        void AddItem(TransformRecord xform)
        {
            var border = new Border { Background = Brushes.Transparent };

            // Badge
            bool isFolder       = xform.IsFolderStrategy;
            var badgeBg         = isFolder ? "#1E1800" : "#2A1030";
            var badgeFg         = isFolder ? "#C8A000" : "#C060C0";
            var badgeText       = isFolder ? "FOLDER"  : "FILE";
            var badge = new Border
            {
                Background    = new SolidColorBrush(Avalonia.Media.Color.Parse(badgeBg)),
                CornerRadius  = new Avalonia.CornerRadius(3),
                Padding       = new Avalonia.Thickness(4, 1),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = badgeText,
                    FontSize   = 9,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(badgeFg)),
                },
            };

            var nameBlock = new TextBlock
            {
                Text       = xform.Name,
                FontSize   = 12,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#CCCCDD")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var topRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing     = 6,
            };
            topRow.Children.Add(badge);
            topRow.Children.Add(nameBlock);

            var idBlock = new TextBlock
            {
                Text       = xform.Id,
                FontSize   = 10,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#555566")),
            };

            var sp = new StackPanel { Margin = new Avalonia.Thickness(10, 6), Spacing = 3 };
            sp.Children.Add(topRow);
            sp.Children.Add(idBlock);
            border.Child = sp;

            border.PointerPressed += (_, _) => SelectTransform(xform, border);
            _transformBorders[xform.Id] = border;
            TransformsListPanel.Children.Add(border);
        }

        if (fileItems.Count > 0)
        {
            AddGroupHeader("── FILE TRANSFORMS ──");
            foreach (var xform in fileItems) AddItem(xform);
        }
        if (folderItems.Count > 0)
        {
            AddGroupHeader("── FOLDER TRANSFORMS ──");
            foreach (var xform in folderItems) AddItem(xform);
        }

        // Restore or set selection
        var targetId = selectId ?? _editingTransform?.Id;
        if (targetId != null && _transformBorders.TryGetValue(targetId, out var b))
        {
            var target = _transforms.FirstOrDefault(t => t.Id == targetId);
            if (target != null) SelectTransform(target, b);
        }
    }

    private void SelectTransform(TransformRecord t, Border border)
    {
        if (_selectedTransformBorder != null)
            _selectedTransformBorder.Background = Brushes.Transparent;

        border.Background        = new SolidColorBrush(Avalonia.Media.Color.Parse("#1A1A2C"));
        _selectedTransformBorder = border;
        _editingTransform        = t;

        TransformNameBox.Text          = t.Name;
        TransformIdText.Text           = t.Id;
        TransformToolText.Text         = t.ToolId.Length > 0 ? t.ToolId : "(none)";
        TransformTypeBox.SelectedIndex = t.TransformType == "folder_strategy" ? 1 : 0;
        TransformCmdBox.Text           = t.CommandTemplate;
        TransformOutputExtBox.Text     = t.OutputExtension;
        TransformEditorPanel.IsVisible = true;
        UpdateCommandPreview();
    }

    private void OnTransformFieldChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        => UpdateCommandPreview();

    private void UpdateCommandPreview()
    {
        var cmd = TransformCmdBox.Text ?? "";
        var ext = TransformOutputExtBox.Text?.Trim() ?? "";

        TransformExtWarning.IsVisible = cmd.Length > 0 && ext.Length == 0;

        if (cmd.Length == 0)
        {
            TransformPreviewText.Text = "(no command template)";
            return;
        }

        var sampleInput  = "sample_input.ext";
        var sampleOutput = "sample_output" + (ext.Length > 0 ? ext : ".ext");
        TransformPreviewText.Text = Data.TransformEngine.BuildCommand(cmd, sampleInput, sampleOutput);
    }

    private async void OnSaveTransform(object? sender, RoutedEventArgs e)
    {
        if (_editingTransform is not TransformRecord t)
            return;

        var name = TransformNameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            await new InfoDialog("Invalid Name", "Transform name cannot be empty.").ShowDialog(this);
            return;
        }

        var cmd = TransformCmdBox.Text?.Trim() ?? "";

        // Validate: all non-empty templates must contain {input} and {output}.
        if (cmd.Length > 0 &&
            (!cmd.Contains("{input}", StringComparison.Ordinal) ||
             !cmd.Contains("{output}", StringComparison.Ordinal)))
        {
            await new InfoDialog("Invalid Template",
                "Command template must include {input} and {output}.\n\n" +
                "These placeholders tell Arkadia where to read the source file\n" +
                "and where to write the output file.")
                .ShowDialog(this);
            return;
        }

        var xformType = TransformTypeBox.SelectedIndex == 1 ? "folder_strategy" : "file_strategy";

        var updated = new TransformRecord
        {
            Id              = t.Id,
            Name            = name,
            ToolId          = t.ToolId,
            TransformType   = xformType,
            CommandTemplate = cmd,
            OutputExtension = TransformOutputExtBox.Text?.Trim() ?? "",
            IsEnabled       = t.IsEnabled,
        };

        _catalog.SaveTransform(updated);
        _transforms = _catalog.LoadTransforms();
        BuildTransformListPanel(updated.Id);
    }

    private void OnNewTransform(object? sender, RoutedEventArgs e)
    {
        var draft = new TransformRecord
        {
            Id              = "custom_" + Guid.NewGuid().ToString("N")[..8],
            Name            = "New Transform",
            ToolId          = "",
            CommandTemplate = "",
            OutputExtension = "",
            IsEnabled       = true,
            TransformType   = "file_strategy",
        };
        _transforms.Add(draft);
        BuildTransformListPanel(draft.Id);
    }

    private async void OnTestTransform(object? sender, RoutedEventArgs e)
    {
        if (_editingTransform is not TransformRecord t)
            return;

        var cmd = TransformCmdBox.Text?.Trim() ?? "";

        if (t.Id == "no_compression" || cmd.Length == 0)
        {
            await new InfoDialog("Nothing to Test",
                "The no_compression transform uses a plain file copy — there is no external command to test.")
                .ShowDialog(this);
            return;
        }

        var testXform = new Data.TransformRecord
        {
            Id              = t.Id,
            Name            = t.Name,
            ToolId          = t.ToolId,
            CommandTemplate = cmd,
            OutputExtension = TransformOutputExtBox.Text?.Trim() ?? "",
            IsEnabled       = true,
        };

        var appRoot   = AppContext.BaseDirectory;
        var testTool  = _catalog.LoadTools().FirstOrDefault(tool => tool.Id == testXform.ToolId);

        var tempDir    = Path.Combine(Path.GetTempPath(), $"arkadia_test_{Guid.NewGuid():N}");
        var inputExt   = ".bin";
        var outputExt  = testXform.OutputExtension.Length > 0 ? testXform.OutputExtension : inputExt;
        var inputPath  = Path.Combine(tempDir, $"sample_input{inputExt}");
        var outputPath = Path.Combine(tempDir, $"sample_output{outputExt}");

        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(inputPath, new byte[512]);

            bool ok = Data.TransformEngine.ExecuteTransform(
                testXform, testTool, appRoot, inputPath, outputPath, out var error);

            if (ok)
                await new InfoDialog("Test Successful",
                    $"Transform executed successfully.\n\nOutput file: {Path.GetFileName(outputPath)}")
                    .ShowDialog(this);
            else
                await new InfoDialog("Test Failed",
                    $"Transform failed:\n\n{error}")
                    .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Test Error", ex.Message).ShowDialog(this);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private void SetActive(Button btn)
    {
        if (_activeButton == btn)
            return;

        _activeButton?.Classes.Remove("active");
        btn.Classes.Add("active");

        // Hide previously active view, show the new one (if registered)
        if (_activeButton is not null && _views.TryGetValue(_activeButton, out var prev))
            prev.IsVisible = false;
        if (_views.TryGetValue(btn, out var next))
            next.IsVisible = true;

        _activeButton = btn;

        if (btn.Tag is string label)
            PageTitle.Text = label;

        if (btn == NavDashboard)
            InitDashboard();

        if (btn == NavSettings)
            LoadAllSettings();

        if (btn == NavOperations)
            InitOperations();

        if (btn == NavAnalytics)
            BuildAnalytics();

        if (btn == NavLogs)
            BuildLogsTree();
    }

    // ── Analytics ─────────────────────────────────────────────────────────────

    private sealed record AnalyticsData(
        long                       TotalSourceBytes,
        long                       TotalDerivedBytes,
        long                       SavedBytes,
        double                     SavedPct,
        Dictionary<string, long>   DerivedByStrategy,
        Dictionary<string, int>    ExtensionCounts,
        int RelMissing, int RelPending, int RelOutdated, int RelPresent, int RelLost,
        List<VolumeRecord>         Volumes,
        Dictionary<string, string> PlatformNames,
        Dictionary<string, string> DatLineNames,
        Dictionary<string, string> StrategyNames);

    private AnalyticsData? _analyticsData;

    private void InitAnalytics() { /* view is populated on first activation */ }

    private void BuildAnalytics()
    {
        // ── Collect catalog-level data ────────────────────────────────────────
        var datLines   = _catalog.LoadDatLines();
        var volumes      = _catalog.GetVolumes();
        var volArtCounts = _catalog.GetArtifactCountsByVolume();
        var platNames  = _catalog.LoadPlatforms()
                                 .ToDictionary(p => p.Id, p => p.Name);
        var dlNames    = datLines.ToDictionary(dl => dl.Id, dl => dl.Name);
        var stratNames = _catalog.LoadStorageStrategies()
                                 .ToDictionary(s => s.Id, s => s.Name);
        // Merge transform names so file-extension-strategy artifacts display correctly.
        // For that strategy, storage_strategy_id stores the transform ID directly
        // (e.g. "chd_dvd_compression"). Transform IDs and storage strategy IDs are
        // disjoint, so no collision can occur.
        foreach (var t in _catalog.LoadTransforms())
            stratNames.TryAdd(t.Id, t.Name);

        // ── Aggregate across all per-DAT-line stores ──────────────────────────
        long totalSource  = 0;
        long totalDerived = 0;
        var  byStrategy   = new Dictionary<string, long>(StringComparer.Ordinal);
        var  extCounts    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int  relMissing = 0, relPending = 0, relOutdated = 0, relPresent = 0, relLost = 0;

        foreach (var dl in datLines)
        {
            if (dl.DataStorePath.Length == 0) continue;
            var dbPath = Path.Combine(_dataDir, dl.DataStorePath);
            if (!File.Exists(dbPath)) continue;
            var store   = new DatLineStore(dbPath);
            var summary = store.GetAnalyticsSummary();

            totalSource  += summary.TotalSourceBytes;
            totalDerived += summary.TotalDerivedBytes;
            foreach (var (sid, bytes) in summary.DerivedByStrategy)
                byStrategy[sid] = byStrategy.GetValueOrDefault(sid) + bytes;
            foreach (var (ext, cnt) in summary.ExtensionCounts)
                extCounts[ext] = extCounts.GetValueOrDefault(ext) + cnt;

            var (m, pe, o, pr, l) = store.GetAllStatusCounts();
            relMissing  += m;  relPending  += pe;
            relOutdated += o;  relPresent  += pr;  relLost += l;
        }

        long   savedBytes = Math.Max(0L, totalSource - totalDerived);
        double savedPct   = totalSource > 0 ? savedBytes * 100.0 / totalSource : 0.0;

        _analyticsData = new AnalyticsData(
            totalSource, totalDerived, savedBytes, savedPct,
            byStrategy, extCounts,
            relMissing, relPending, relOutdated, relPresent, relLost,
            volumes, platNames, dlNames, stratNames);

        // ── KPI strip ─────────────────────────────────────────────────────────
        AnalyticsKpiSourceSize.Text      = FormatBytes(totalSource);
        AnalyticsKpiDerivedSize.Text     = FormatBytes(totalDerived);
        AnalyticsKpiSavedPct.Text        = totalSource > 0 ? $"{savedPct:F1}%" : "—";
        AnalyticsKpiSavedAbs.Text        = totalSource > 0 ? FormatBytes(savedBytes) : "—";
        AnalyticsKpiVolumes.Text         = volumes.Count.ToString("N0");
        int critCount = volumes.Count(v => v.Health == "crit");
        AnalyticsKpiCritVolumes.Text     = critCount.ToString("N0");
        AnalyticsKpiStoredArtifacts.Text = _catalog.CountStoredArtifacts().ToString("N0");
        AnalyticsKpiArtifactTypes.Text   = extCounts.Count.ToString("N0");

        // Dynamic KPI emphasis
        AnalyticsKpiSavedPct.Foreground = new SolidColorBrush(
            totalSource > 0 ? Color.Parse("#4CAF50") : Color.Parse("#333344"));
        AnalyticsKpiSavedAbs.Foreground = new SolidColorBrush(
            totalSource > 0 ? Color.Parse("#7B68EE") : Color.Parse("#333344"));
        // AT RISK card — all three text elements + stripe respond to critCount
        bool hasCrit = critCount > 0;
        AnalyticsKpiCritVolumes.Foreground = new SolidColorBrush(
            hasCrit ? Color.Parse("#EF5350") : Color.Parse("#4CAF50"));
        AnalyticsCritLabel.Foreground = new SolidColorBrush(
            hasCrit ? Color.Parse("#7A2020") : Color.Parse("#2A6030"));
        AnalyticsCritHelper.Foreground = new SolidColorBrush(
            hasCrit ? Color.Parse("#5A1818") : Color.Parse("#1A4A22"));
        AnalyticsCritAccent.Background = new SolidColorBrush(
            hasCrit ? Color.Parse("#4A1010") : Color.Parse("#1A3020"));

        // ── Sections ──────────────────────────────────────────────────────────
        AnalyticsBuildSectionA(totalSource, totalDerived, savedBytes, savedPct);
        AnalyticsBuildSectionB(byStrategy, totalDerived, stratNames);
        AnalyticsBuildSectionC(extCounts);
        AnalyticsBuildSectionD(volumes, platNames, dlNames, volArtCounts);
        AnalyticsBuildSectionDisk();
        AnalyticsBuildSectionE(relMissing, relPending, relOutdated, relPresent, relLost, volumes);
    }

    /// <summary>
    /// Inline bar row: [label fixed] [bar stretch] [value fixed] — all on one line.
    /// All rows in the same section share the same column widths, so bars start/end at identical x.
    /// </summary>
    private static Grid MakeBarRow(
        string label, long value, long max, string valueText, Color barColor,
        int labelWidth = 120, int valueWidth = 130,
        int bottomMargin = 4, bool isFirst = false)
    {
        double fill = max > 0 ? Math.Clamp((double)value / max, 0.0, 1.0) : 0.0;

        var row = new Grid
        {
            Height              = 28,
            Margin              = new Avalonia.Thickness(0, 0, 0, bottomMargin),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        row.ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*,{valueWidth}");

        // Col 0: Label
        row.Children.Add(new TextBlock
        {
            Text              = label,
            FontSize          = 12,
            FontWeight        = isFirst ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground        = new SolidColorBrush(Color.Parse(isFirst ? "#DDDDEF" : "#AAAACC")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
        });

        // Col 1: Bar — wrapper Border approach: track is a standalone Border (HAlign=Stretch)
        // whose width comes from the parent column, never from internal column structure.
        // Integer star weights avoid Avalonia's decimal-star-as-fixed-px parsing bug.
        bool hasFill    = fill > 0.001;
        bool isFullFill = fill >= 0.9995;

        int fillW  = hasFill ? (int)Math.Round(fill * 10000) : 0;
        int emptyW = 10000 - fillW;
        var colStr = hasFill && !isFullFill ? $"{fillW}*,{emptyW}*" : "1*";

        var fillGrid = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        fillGrid.ColumnDefinitions = new ColumnDefinitions(colStr);

        if (hasFill)
        {
            fillGrid.Children.Add(new Border
            {
                Height              = 9,
                Background          = new SolidColorBrush(barColor),
                CornerRadius        = new Avalonia.CornerRadius(isFullFill ? 2 : 2, isFullFill ? 2 : 0,
                                                                 isFullFill ? 2 : 0, 2),
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            });
        }

        var trackBorder = new Border
        {
            Height              = 9,
            Background          = new SolidColorBrush(Color.Parse("#07071A")),
            CornerRadius        = new Avalonia.CornerRadius(2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            Margin              = new Avalonia.Thickness(10, 0),
            Child               = fillGrid,
        };
        Grid.SetColumn(trackBorder, 1);
        row.Children.Add(trackBorder);

        // Col 2: Value
        var valBlock = new TextBlock
        {
            Text                = valueText,
            FontSize            = 11,
            Foreground          = new SolidColorBrush(Color.Parse(isFirst ? "#9999BB" : "#7777AA")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(valBlock, 2);
        row.Children.Add(valBlock);

        return row;
    }

    /// <summary>Thin 3px bar, full-width proportional using star columns.</summary>
    private static Grid MakeSizeBar(double fill, Color fillColor, int bottomMargin = 4)
    {
        bool hasFill    = fill > 0.001;
        bool isFullFill = fill >= 0.9995;
        var g = new Grid { Height = 5, Margin = new Avalonia.Thickness(0, 2, 0, bottomMargin) };

        if (hasFill && !isFullFill)
            g.ColumnDefinitions = new ColumnDefinitions($"{fill * 100:F2}*,{(1 - fill) * 100:F2}*");
        else
            g.ColumnDefinitions = new ColumnDefinitions("1*");

        var track = new Border
        {
            Height = 5, CornerRadius = new Avalonia.CornerRadius(2),
            Background = new SolidColorBrush(Color.Parse("#07071A")),
        };
        if (hasFill && !isFullFill) Grid.SetColumnSpan(track, 2);
        g.Children.Add(track);

        if (hasFill)
        {
            g.Children.Add(new Border
            {
                Height = 5,
                CornerRadius = isFullFill
                    ? new Avalonia.CornerRadius(2)
                    : new Avalonia.CornerRadius(2, 0, 0, 2),
                Background = new SolidColorBrush(fillColor),
            });
        }
        return g;
    }

    // ── Section A: Storage &amp; Compression ──────────────────────────────────
    //
    // AnalyticsSectionAPanel is a Grid (not StackPanel).
    // A Grid parent gives children a FINITE width during Measure, so star columns
    // inside child Grids correctly resolve proportional fills.
    // RowDefinitions are built dynamically each time this method runs.

    private void AnalyticsBuildSectionA(long source, long derived, long saved, double savedPct)
    {
        var g = AnalyticsSectionAPanel;
        g.Children.Clear();
        g.RowDefinitions.Clear();

        if (source == 0)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var skel = MakeSkeletonBars("Run an ingestion to populate this section.");
            Grid.SetRow(skel, 0);
            g.Children.Add(skel);
            return;
        }

        double derivedRatio = source > 0 ? Math.Clamp((double)derived / source, 0.0, 1.0) : 0.0;
        double savedRatio   = Math.Max(0.0, 1.0 - derivedRatio);
        const int BAR_H     = 14;

        // ── Row management ───────────────────────────────────────────────────
        int nextRow = 0;

        void DefRow(GridLength h)
        {
            g.RowDefinitions.Add(new RowDefinition(h));
        }
        void Put(Control c)
        {
            Grid.SetRow(c, nextRow);
            g.Children.Add(c);
        }
        // Add an Auto-height content row, place c in it, advance row counter.
        void AutoRow(Control c)  { DefRow(GridLength.Auto); Put(c); nextRow++; }
        // Add an empty spacer row (no child needed — empty Grid row = whitespace).
        void Gap(int px)         { DefRow(new GridLength(px)); nextRow++; }

        // ── DIAGNOSTIC: MakeSection ──────────────────────────────────────────
        // Two root causes identified from debug values (bgW=1082, srcTrack=101, drvTrack=526):
        //   1. Decimal star strings like "100.00*" parse as fixed pixels in Avalonia,
        //      not as star proportions — Source's single column resolved to 100px.
        //   2. Grid.SetColumnSpan on a track Border placed inside the fill Grid was
        //      silently ignored — Derived track showed only column-0 width (48.6%).
        //
        // Fix strategy:
        //   • Track is a standalone Border (HAlign=Stretch) — its width comes from
        //     its own stretch against the parent, never from any column structure.
        //   • Fill segments live inside a fillGrid that is the track Border's Child.
        //     The fillGrid's width = trackBorder's content width = always full width.
        //   • Integer star weights (×10000) avoid all decimal parsing issues.
        //   • Each section is a combined Grid("*,Auto" × "Auto,4,14") so the
        //     bar row explicitly spans ColumnSpan=2 — no Auto column can clip it.
        Grid MakeSection(string labelText, string valueText,
                         bool fullFill, params (double ratio, Color color)[] segs)
        {
            const int FILL_H = 8;

            // ── Outer section grid ────────────────────────────────────────────
            var outer = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            outer.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            outer.RowDefinitions    = new RowDefinitions("Auto,4,14");

            // Row 0 col 0: label
            var lbl = new TextBlock
            {
                Text              = labelText,
                FontSize          = 12,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = new SolidColorBrush(Color.Parse("#DDDDEE")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetRow(lbl, 0); Grid.SetColumn(lbl, 0);
            outer.Children.Add(lbl);

            // Row 0 col 1: value
            var val = new TextBlock
            {
                Text                = valueText,
                FontSize            = 11,
                Foreground          = new SolidColorBrush(Color.Parse("#9999BB")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                Margin              = new Avalonia.Thickness(8, 0, 0, 0),
            };
            Grid.SetRow(val, 0); Grid.SetColumn(val, 1);
            outer.Children.Add(val);

            // ── Fill segments (inside fillGrid, inside trackBorder) ───────────
            double total  = Math.Clamp(segs.Sum(s => Math.Clamp(s.ratio, 0.0, 1.0)), 0.0, 1.0);
            double empty  = 1.0 - total;

            var colList = new List<string>();
            foreach (var (r, _) in segs)
            {
                int iw = (int)Math.Round(Math.Clamp(r, 0.0, 1.0) * 10000);
                if (iw > 0) colList.Add($"{iw}*");
            }
            int emptyIw = (int)Math.Round(empty * 10000);
            if (emptyIw > 0) colList.Add($"{emptyIw}*");
            if (colList.Count == 0) colList.Add("1*");

            var fillGrid = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            fillGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", colList));

            var fills = new List<Border>();
            int ci = 0;
            bool hasEmpty = emptyIw > 0;
            for (int i = 0; i < segs.Length; i++)
            {
                int iw = (int)Math.Round(Math.Clamp(segs[i].ratio, 0.0, 1.0) * 10000);
                if (iw <= 0) continue;
                bool isFirst = (ci == 0);
                bool isLast  = (i == segs.Length - 1 && !hasEmpty);
                var seg = new Border
                {
                    Background          = new SolidColorBrush(segs[i].color),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                };
                if (fullFill)
                {
                    seg.Height        = BAR_H;
                    seg.CornerRadius  = new Avalonia.CornerRadius(
                        isFirst ? 3 : 0, isLast ? 3 : 0,
                        isLast  ? 3 : 0, isFirst ? 3 : 0);
                    seg.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                }
                else
                {
                    seg.Height            = FILL_H;
                    seg.CornerRadius      = new Avalonia.CornerRadius(2);
                    seg.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                }
                Grid.SetColumn(seg, ci++);
                fills.Add(seg);
                fillGrid.Children.Add(seg);
            }

            // ── Track: standalone Border — width = HAlign.Stretch, never from columns
            // fillGrid is its Child so it inherits the same full content width.
            var trackBorder = new Border
            {
                Height              = BAR_H,
                Background          = new SolidColorBrush(Color.Parse("#07071A")),
                BorderBrush         = new SolidColorBrush(Color.Parse("#3A3A5A")),
                BorderThickness     = new Avalonia.Thickness(1),
                CornerRadius        = new Avalonia.CornerRadius(3),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Stretch,
                Child               = fillGrid,
            };
            Grid.SetRow(trackBorder, 2);
            Grid.SetColumnSpan(trackBorder, 2); // span * and Auto columns → full row width
            outer.Children.Add(trackBorder);

            return outer;
        }

        // ── SOURCE ────────────────────────────────────────────────────────────
        AutoRow(MakeSection("Source", FormatBytes(source),
            false, (1.0, Color.Parse("#29B6F6"))));

        // ── GAP ───────────────────────────────────────────────────────────────
        Gap(10);

        // ── DERIVED ───────────────────────────────────────────────────────────
        AutoRow(MakeSection("Derived", FormatBytes(derived),
            false, (derivedRatio, Color.Parse("#7B68EE"))));

        // ── SEPARATOR ─────────────────────────────────────────────────────────
        Gap(18);

        // ── SAVED RATIO ── same pill style as Source/Derived; stacked segments ─
        AutoRow(MakeSection("Saved Ratio", $"{savedPct:F1}%  ({FormatBytes(saved)})",
            false,
            (derivedRatio, Color.Parse("#7B68EE")),
            (savedRatio,   Color.Parse("#4CAF50"))));

        // ── LEGEND ────────────────────────────────────────────────────────────
        Gap(10);
        var legend = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16 };
        void AddLegend(Color color, string text)
        {
            var item = new StackPanel
            {
                Orientation       = Avalonia.Layout.Orientation.Horizontal,
                Spacing           = 6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            item.Children.Add(new Border
            {
                Width = 10, Height = 10,
                CornerRadius = new Avalonia.CornerRadius(2),
                Background   = new SolidColorBrush(color),
            });
            item.Children.Add(new TextBlock
            {
                Text       = text,
                FontSize   = 11,
                Foreground = new SolidColorBrush(Color.Parse("#777788")),
            });
            legend.Children.Add(item);
        }
        AddLegend(Color.Parse("#29B6F6"), "Source (100%)");
        AddLegend(Color.Parse("#7B68EE"), $"Derived ({derivedRatio * 100:F1}%)");
        if (savedRatio > 0.001)
            AddLegend(Color.Parse("#4CAF50"), $"Saved ({savedRatio * 100:F1}%)");
        AutoRow(legend);

        // ── BOTTOM SUMMARY ────────────────────────────────────────────────────
        Gap(14);
        var statGrid = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        statGrid.ColumnDefinitions = new ColumnDefinitions("*,*,*");
        void AddStat(int col, string lbl, string val, Color color)
        {
            var sp = new StackPanel
            {
                Spacing             = 2,
                HorizontalAlignment = col == 0 ? Avalonia.Layout.HorizontalAlignment.Left
                                    : col == 2 ? Avalonia.Layout.HorizontalAlignment.Right
                                               : Avalonia.Layout.HorizontalAlignment.Center,
            };
            sp.Children.Add(new TextBlock
            {
                Text          = lbl,
                FontSize      = 10,
                FontWeight    = FontWeight.SemiBold,
                LetterSpacing = 1.0,
                Foreground    = new SolidColorBrush(Color.Parse("#555566")),
            });
            sp.Children.Add(new TextBlock
            {
                Text       = val,
                FontSize   = 16,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(color),
            });
            Grid.SetColumn(sp, col);
            statGrid.Children.Add(sp);
        }
        AddStat(0, "SOURCE",  FormatBytes(source),  Color.Parse("#AAAACC"));
        AddStat(1, "DERIVED", FormatBytes(derived),  Color.Parse("#7B68EE"));
        AddStat(2, "SAVED",   FormatBytes(saved),    Color.Parse("#5AC870"));
        AutoRow(statGrid);
    }

    // ── Display label formatter ───────────────────────────────────────────────

    /// <summary>
    /// Derives a human-readable label from a transform/strategy ID.
    /// Used only as a fallback when no Name is found in the lookup dictionaries.
    /// Rules: split on "_", title-case each word, drop the trailing word
    /// "Compression" if more than one word remains.
    /// Examples: "chd_dvd_compression" → "CHD DVD"
    ///           "zip_compression"     → "ZIP"
    ///           "no_compression"      → "No Compression"  (single meaningful word kept)
    /// </summary>
    private static string FormatStrategyLabel(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;

        var words = id.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return id;

        // Title-case each word (all-caps for ≤3-char words like "CHD", "CD", "DVD", "ZIP")
        var parts = words.Select(w =>
            w.Length <= 3
                ? w.ToUpperInvariant()
                : char.ToUpper(w[0]) + w[1..].ToLowerInvariant()
        ).ToList();

        // Drop trailing "Compression" only when at least one other word remains
        if (parts.Count > 1 &&
            string.Equals(parts[^1], "Compression", StringComparison.OrdinalIgnoreCase))
            parts.RemoveAt(parts.Count - 1);

        return string.Join(" ", parts);
    }

    // ── Section B: Compression by Strategy ───────────────────────────────────

    private void AnalyticsBuildSectionB(
        Dictionary<string, long> byStrategy, long totalDerived,
        Dictionary<string, string> stratNames)
    {
        AnalyticsByStrategyPanel.Children.Clear();

        if (byStrategy.Count == 0)
        {
            AnalyticsByStrategyPanel.Children.Add(MakeSkeletonBars("No derived artifacts recorded yet."));
            return;
        }

        var palette = new[]
        {
            Color.Parse("#7B68EE"), Color.Parse("#4CAF50"), Color.Parse("#FF9800"),
            Color.Parse("#29B6F6"), Color.Parse("#EF5350"), Color.Parse("#AB47BC"),
        };

        var sorted    = byStrategy.OrderByDescending(kv => kv.Value).ToList();
        long maxBytes = sorted[0].Value;

        // ── Resolve display labels first so we can measure them ───────────────
        var rows = sorted.Select((kv, idx) =>
        {
            var label = stratNames.TryGetValue(kv.Key, out var n) && n.Length > 0
                        ? n
                        : FormatStrategyLabel(kv.Key);
            return (label, bytes: kv.Value, isFirst: idx == 0);
        }).ToList();

        // ── Measure widest label using FormattedText (matches actual render) ──
        // Font size = 12, SemiBold for first row, Normal for the rest.
        const int LABEL_PAD = 12;  // breathing room between label and bar
        const int LABEL_MIN = 90;
        const int LABEL_MAX = 220; // cap so one unusually long label can't break layout
        double maxLabelPx = rows.Max(r =>
            new FormattedText(
                r.label,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal,
                             r.isFirst ? FontWeight.SemiBold : FontWeight.Normal),
                12.0,
                null).Width);
        int labelWidth = Math.Clamp((int)Math.Ceiling(maxLabelPx) + LABEL_PAD, LABEL_MIN, LABEL_MAX);

        // ── Build rows with the shared computed label width ───────────────────
        int ci = 0;
        foreach (var (label, bytes, isFirst) in rows)
        {
            double pct = totalDerived > 0 ? bytes * 100.0 / totalDerived : 0.0;
            var color  = palette[ci++ % palette.Length];
            AnalyticsByStrategyPanel.Children.Add(
                MakeBarRow(label, bytes, maxBytes, $"{FormatBytes(bytes)} ({pct:F1}%)", color,
                           labelWidth: labelWidth, valueWidth: 140, isFirst: isFirst));
        }
    }

    // ── Section C: Artifact Type Distribution ─────────────────────────────────

    private void AnalyticsBuildSectionC(Dictionary<string, int> extCounts)
    {
        AnalyticsArtifactTypePanel.Children.Clear();

        if (extCounts.Count == 0)
        {
            AnalyticsArtifactTypePanel.Children.Add(MakeSkeletonBars("No derived artifacts recorded yet."));
            return;
        }

        var palette = new[]
        {
            Color.Parse("#29B6F6"), Color.Parse("#7B68EE"), Color.Parse("#4CAF50"),
            Color.Parse("#FF9800"), Color.Parse("#AB47BC"), Color.Parse("#EF5350"),
        };

        int total  = extCounts.Values.Sum();
        var sorted = extCounts.OrderByDescending(kv => kv.Value).ToList();
        int maxCnt = sorted[0].Value;
        int ci     = 0;

        foreach (var (ext, cnt) in sorted)
        {
            double pct = total > 0 ? cnt * 100.0 / total : 0.0;
            bool first = ci == 0;
            var color  = palette[ci++ % palette.Length];
            AnalyticsArtifactTypePanel.Children.Add(
                MakeBarRow(ext, cnt, maxCnt, $"{cnt:N0} ({pct:F1}%)", color,
                           labelWidth: 70, valueWidth: 110, isFirst: first));
        }
    }

    // ── Section D: Volume Heatmap ─────────────────────────────────────────────

    private void AnalyticsBuildSectionD(
        List<VolumeRecord> volumes,
        Dictionary<string, string> platNames,
        Dictionary<string, string> dlNames,
        Dictionary<string, int>    artCounts)
    {
        AnalyticsVolumeHeatmapPanel.Children.Clear();

        if (volumes.Count == 0)
        {
            AnalyticsVolumeHeatmapPanel.Children.Add(EmptyNote("No volumes created yet."));
            return;
        }

        // Header row (no STATUS column)
        AnalyticsVolumeHeatmapPanel.Children.Add(MakeHeatmapRow("LABEL", "PLATFORM", "HEALTH", "SIZE", isHeader: true));
        AnalyticsVolumeHeatmapPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1A1A2E")),
            Margin     = new Avalonia.Thickness(0, 2, 0, 4),
        });

        long maxActual = volumes.Max(v => v.ActualSizeBytes);
        if (maxActual == 0) maxActual = 1L;

        // Sort by occupancy descending — highest usage first, empty volumes last
        foreach (var vol in volumes.OrderByDescending(v => v.ActualSizeBytes))
        {
            var pn      = platNames.TryGetValue(vol.PlatformId, out var p) ? p : vol.PlatformId;
            int artCnt  = artCounts.GetValueOrDefault(vol.Id, 0);
            var secondary = artCnt > 0 ? $"{pn} · {artCnt:N0} files" : pn;
            bool isEmpty = vol.ActualSizeBytes == 0;
            double fill  = Math.Clamp((double)vol.ActualSizeBytes / maxActual, 0.0, 1.0);
            Color  color = vol.Health == "crit" ? Color.Parse("#EF5350") : Color.Parse("#4CAF50");
            AnalyticsVolumeHeatmapPanel.Children.Add(
                MakeHeatmapRow(vol.Label, secondary, vol.Health,
                               FormatBytes(vol.ActualSizeBytes),
                               sizeFill: fill, sizeBarColor: color, isEmpty: isEmpty));
        }
    }

    // ── Section D2: Disk Heatmap ──────────────────────────────────────────────

    private void AnalyticsBuildSectionDisk()
    {
        AnalyticsDiskHeatmapPanel.Children.Clear();

        var disks      = _catalog.GetDisks();
        var volCounts  = _catalog.GetVolumeCountsByDisk();

        if (disks.Count == 0)
        {
            AnalyticsDiskHeatmapPanel.Children.Add(EmptyNote("No disks created yet."));
            return;
        }

        // Compute occupancy per disk (used / declared); disks with no declared capacity sort last.
        var diskData = disks.Select(d =>
        {
            var (cap, used, _) = _catalog.GetDiskUsage(d.Id);
            int  volCount      = volCounts.GetValueOrDefault(d.Id, 0);
            long declared      = cap > 0 ? cap : d.DeclaredCapacityBytes;
            double occ         = declared > 0 ? Math.Clamp((double)used / declared, 0.0, 1.0) : 0.0;
            bool   isEmpty     = used == 0 || volCount == 0;
            string health      = d.Status == "lost" ? "crit"
                               : occ >= 0.90        ? "warning"
                               :                      "ok";
            return (disk: d, used, declared, volCount, occ, isEmpty, health);
        }).OrderByDescending(x => x.occ).ToList();

        // Header row
        AnalyticsDiskHeatmapPanel.Children.Add(
            MakeDiskHeatmapRow("LABEL", "VOLUMES", "HEALTH", "USED / TOTAL", isHeader: true));
        AnalyticsDiskHeatmapPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#1A1A2E")),
            Margin     = new Avalonia.Thickness(0, 2, 0, 4),
        });

        foreach (var (disk, used, declared, volCount, occ, isEmpty, health) in diskData)
        {
            string secondary = volCount switch
            {
                0 => "",
                1 => "1 volume",
                _ => $"{volCount} volumes",
            };
            string sizeText = declared > 0
                ? $"{FormatBytes(used)} / {FormatBytes(declared)}"
                : $"{FormatBytes(used)} / unknown";

            AnalyticsDiskHeatmapPanel.Children.Add(
                MakeDiskHeatmapRow(disk.Label, secondary, health, sizeText,
                                   occFill: occ, isEmpty: isEmpty));
        }
    }

    private static Grid MakeDiskHeatmapRow(
        string label, string secondary, string health, string size,
        bool isHeader = false, double occFill = 0.0, bool isEmpty = false)
    {
        // Columns: Label(120) | Secondary(140) | OCC(*) | Health(70) | Size(100)
        var row = new Grid
        {
            Margin              = new Avalonia.Thickness(0, 0, 0, 2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        row.ColumnDefinitions = new ColumnDefinitions("120,120,*,70,140");

        var headerFg = new SolidColorBrush(Color.Parse("#555566"));

        Control MakeCell(int col, string text,
            Avalonia.Layout.HorizontalAlignment align = Avalonia.Layout.HorizontalAlignment.Left,
            SolidColorBrush? fg     = null,
            FontWeight?      fwt    = null,
            int fontSize            = 12,
            bool noTrim             = false)
        {
            var tb = new TextBlock
            {
                Text                = text,
                FontSize            = fontSize,
                FontWeight          = fwt ?? FontWeight.Normal,
                Foreground          = fg ?? new SolidColorBrush(Color.Parse("#AAAACC")),
                Padding             = new Avalonia.Thickness(2, 4),
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = align,
                TextTrimming        = noTrim
                                      ? Avalonia.Media.TextTrimming.None
                                      : Avalonia.Media.TextTrimming.CharacterEllipsis,
                TextWrapping        = Avalonia.Media.TextWrapping.NoWrap,
            };
            Grid.SetColumn(tb, col);
            return tb;
        }

        if (isHeader)
        {
            row.Children.Add(MakeCell(0, label,     fg: headerFg, fwt: FontWeight.SemiBold, fontSize: 10));
            row.Children.Add(MakeCell(1, secondary, fg: headerFg, fwt: FontWeight.SemiBold, fontSize: 10));
            row.Children.Add(MakeCell(2, "OCC",
                align: Avalonia.Layout.HorizontalAlignment.Left,
                fg: headerFg, fwt: FontWeight.SemiBold, fontSize: 10));
            row.Children.Add(MakeCell(3, health,
                align: Avalonia.Layout.HorizontalAlignment.Center,
                fg: headerFg, fwt: FontWeight.SemiBold, fontSize: 10));
            row.Children.Add(MakeCell(4, size,
                align: Avalonia.Layout.HorizontalAlignment.Right,
                fg: headerFg, fwt: FontWeight.SemiBold, fontSize: 10));
        }
        else
        {
            string labelHex = isEmpty ? "#555566" : "#DDDDEE";
            string secHex   = isEmpty ? "#333344" : "#555566";
            string sizeHex  = isEmpty ? "#333344" : "#AAAACC";

            // Col 0: Disk label (bold)
            row.Children.Add(MakeCell(0, label,
                fg:  new SolidColorBrush(Color.Parse(labelHex)),
                fwt: FontWeight.SemiBold));

            // Col 1: Volume count secondary text (blank when 0)
            if (secondary.Length > 0)
                row.Children.Add(MakeCell(1, secondary,
                    fg: new SolidColorBrush(Color.Parse(secHex))));

            // Col 2: Occupancy bar — same pattern as Section A / Volume Heatmap
            {
                const int OCC_H   = 9;
                bool hasFill      = occFill > 0.001;
                bool isFullFill   = occFill >= 0.9995;

                int fillW  = hasFill ? (int)Math.Round(occFill * 10000) : 0;
                int emptyW = 10000 - fillW;
                var colStr = hasFill && !isFullFill
                    ? $"{fillW}*,{emptyW}*"
                    : "1*";

                var fillGrid = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                fillGrid.ColumnDefinitions = new ColumnDefinitions(colStr);

                if (hasFill)
                {
                    fillGrid.Children.Add(new Border
                    {
                        Height              = OCC_H,
                        Background          = new SolidColorBrush(Color.Parse("#4CAF50")),
                        CornerRadius        = new Avalonia.CornerRadius(isFullFill ? 2 : 2, isFullFill ? 2 : 0,
                                                                         isFullFill ? 2 : 0, 2),
                        VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    });
                }

                var occTrack = new Border
                {
                    Height              = OCC_H,
                    Background          = new SolidColorBrush(Color.Parse("#07071A")),
                    BorderBrush         = new SolidColorBrush(Color.Parse("#3A3A5A")),
                    BorderThickness     = new Avalonia.Thickness(1),
                    CornerRadius        = new Avalonia.CornerRadius(2),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Margin              = new Avalonia.Thickness(0, 0, 8, 0),
                    Child               = fillGrid,
                };
                Grid.SetColumn(occTrack, 2);
                row.Children.Add(occTrack);
            }

            // Col 3: Health badge — lost=crit, >=90%=warning, else ok
            {
                var (healthColor, healthBg) = health switch
                {
                    "crit"    => (Color.Parse("#FF5252"), Color.Parse("#3A0A0A")),
                    "warning" => (Color.Parse("#FF9800"), Color.Parse("#1E1400")),
                    _         => (Color.Parse("#4CAF50"), Color.Parse("#0A200A")),
                };
                var hBadge = new Border
                {
                    Background          = new SolidColorBrush(healthBg),
                    CornerRadius        = new Avalonia.CornerRadius(3),
                    Padding             = new Avalonia.Thickness(6, 2),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Margin              = new Avalonia.Thickness(2),
                    Child               = new TextBlock
                    {
                        Text       = health,
                        FontSize   = 11,
                        Foreground = new SolidColorBrush(healthColor),
                    },
                };
                Grid.SetColumn(hBadge, 3);
                row.Children.Add(hBadge);
            }

            // Col 4: "used / total" — right-aligned, never truncated
            row.Children.Add(MakeCell(4, size,
                align:  Avalonia.Layout.HorizontalAlignment.Right,
                fg:     new SolidColorBrush(Color.Parse(sizeHex)),
                noTrim: true));
        }

        return row;
    }

    private static Grid MakeHeatmapRow(
        string label, string platform, string health, string size,
        bool isHeader = false, double sizeFill = 0.0, Color sizeBarColor = default,
        bool isEmpty = false)
    {
        // Columns: Label(120) | Platform(200) | OCC(*) | Health(70) | Size(80)
        // OCC is the star column so it dominates the row width.
        var row = new Grid
        {
            Margin              = new Avalonia.Thickness(0, 0, 0, 2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        row.ColumnDefinitions = new ColumnDefinitions("120,200,*,70,80");

        var headerFg = new SolidColorBrush(Color.Parse("#555566"));
        int hFsize   = 10;

        Control MakeCell(int col, string text,
            Avalonia.Layout.HorizontalAlignment align = Avalonia.Layout.HorizontalAlignment.Left,
            SolidColorBrush? fg  = null,
            FontWeight?      fwt = null,
            int fontSize         = 12)
        {
            var tb = new TextBlock
            {
                Text                = text,
                FontSize            = fontSize,
                FontWeight          = fwt ?? FontWeight.Normal,
                Foreground          = fg ?? new SolidColorBrush(Color.Parse("#AAAACC")),
                Padding             = new Avalonia.Thickness(2, 4),
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = align,
                TextTrimming        = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(tb, col);
            return tb;
        }

        if (isHeader)
        {
            row.Children.Add(MakeCell(0, label,    fg: headerFg, fwt: FontWeight.SemiBold, fontSize: hFsize));
            row.Children.Add(MakeCell(1, platform, fg: headerFg, fwt: FontWeight.SemiBold, fontSize: hFsize));
            row.Children.Add(MakeCell(2, "OCC",
                align: Avalonia.Layout.HorizontalAlignment.Left,
                fg: headerFg, fwt: FontWeight.SemiBold, fontSize: hFsize));
            row.Children.Add(MakeCell(3, health,
                align: Avalonia.Layout.HorizontalAlignment.Center,
                fg: headerFg, fwt: FontWeight.SemiBold, fontSize: hFsize));
            row.Children.Add(MakeCell(4, size,
                align: Avalonia.Layout.HorizontalAlignment.Right,
                fg: headerFg, fwt: FontWeight.SemiBold, fontSize: hFsize));
        }
        else
        {
            // Dim colours for empty (zero-size) volumes
            string labelHex = isEmpty ? "#555566" : "#DDDDEE";
            string platHex  = isEmpty ? "#333344" : "#555566";

            // Col 0: Volume label
            row.Children.Add(MakeCell(0, label,
                fg:  new SolidColorBrush(Color.Parse(labelHex)),
                fwt: FontWeight.SemiBold));

            // Col 1: Platform · files
            row.Children.Add(MakeCell(1, platform,
                fg: new SolidColorBrush(Color.Parse(platHex))));

            // Col 2: Occupancy bar — wrapper Border (full-width) + proportional fillGrid inside
            // Uses the same layout approach as Section A: track is a standalone Border whose
            // width comes from HAlign.Stretch, not from column structure. Fill segments live
            // inside it via a proportional-star fillGrid.
            {
                const int OCC_H   = 9;
                bool hasFill      = sizeFill > 0.001;
                bool isFullFill   = sizeFill >= 0.9995;

                // Proportional columns using integer star weights (avoids decimal-parse issues)
                int fillW  = hasFill ? (int)Math.Round(sizeFill * 10000) : 0;
                int emptyW = 10000 - fillW;
                var colStr = hasFill && !isFullFill
                    ? $"{fillW}*,{emptyW}*"
                    : "1*";

                var fillGrid = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                fillGrid.ColumnDefinitions = new ColumnDefinitions(colStr);

                if (hasFill)
                {
                    bool roundRight = isFullFill;
                    fillGrid.Children.Add(new Border
                    {
                        Height              = OCC_H,
                        Background          = new SolidColorBrush(sizeBarColor),
                        CornerRadius        = new Avalonia.CornerRadius(roundRight ? 2 : 2, roundRight ? 2 : 0,
                                                                         roundRight ? 2 : 0, 2),
                        VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    });
                }

                var occTrack = new Border
                {
                    Height              = OCC_H,
                    Background          = new SolidColorBrush(Color.Parse("#07071A")),
                    BorderBrush         = new SolidColorBrush(Color.Parse("#3A3A5A")),
                    BorderThickness     = new Avalonia.Thickness(1),
                    CornerRadius        = new Avalonia.CornerRadius(2),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Margin              = new Avalonia.Thickness(0, 0, 8, 0),
                    Child               = fillGrid,
                };
                Grid.SetColumn(occTrack, 2);
                row.Children.Add(occTrack);
            }

            // Col 3: Health badge — ok=green, warning=orange, crit=red
            {
                var (healthColor, healthBg) = health switch
                {
                    "crit"    => (Color.Parse("#FF5252"), Color.Parse("#3A0A0A")),
                    "warning" => (Color.Parse("#FF9800"), Color.Parse("#1E1400")),
                    _         => (Color.Parse("#4CAF50"), Color.Parse("#0A200A")), // ok
                };
                var hBadge = new Border
                {
                    Background          = new SolidColorBrush(healthBg),
                    CornerRadius        = new Avalonia.CornerRadius(3),
                    Padding             = new Avalonia.Thickness(6, 2),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Margin              = new Avalonia.Thickness(2),
                    Child               = new TextBlock
                    {
                        Text       = health,
                        FontSize   = 11,
                        Foreground = new SolidColorBrush(healthColor),
                    },
                };
                Grid.SetColumn(hBadge, 3);
                row.Children.Add(hBadge);
            }

            // Col 4: Size — right-aligned
            row.Children.Add(MakeCell(4, size,
                align: Avalonia.Layout.HorizontalAlignment.Right,
                fg:    new SolidColorBrush(isEmpty ? Color.Parse("#333344") : Color.Parse("#AAAACC"))));
        }

        return row;
    }

    // ── Section E: Archive State Overview ─────────────────────────────────────

    private void AnalyticsBuildSectionE(
        int missing, int pending, int outdated, int present, int lost,
        List<VolumeRecord> volumes)
    {
        // Release status
        AnalyticsReleaseStatusPanel.Children.Clear();
        int relTotal = missing + pending + outdated + present + lost;
        var relRows  = new (string Label, int Count, string Hex)[]
        {
            ("Present",  present,  "#4CAF50"),  // green  — same as Volume Health OK / Section A saved
            ("Missing",  missing,  "#EF5350"),  // red    — same as Volume Health Critical
            ("Outdated", outdated, "#FF9800"),  // orange — existing warning tone
            ("Pending",  pending,  "#29B6F6"),  // cyan   — same as Section A Source
            ("Lost",     lost,     "#FF5252"),  // red+   — same as health badge crit; distinguishes from Missing
        };
        foreach (var (lbl, cnt, hex) in relRows)
        {
            double pct = relTotal > 0 ? cnt * 100.0 / relTotal : 0.0;
            AnalyticsReleaseStatusPanel.Children.Add(
                MakeBarRow(lbl, cnt, Math.Max(relTotal, 1), $"{cnt:N0} ({pct:F1}%)", Color.Parse(hex),
                           labelWidth: 75, valueWidth: 110));
        }

        // Volume health
        AnalyticsVolumeHealthPanel.Children.Clear();
        int volOk   = volumes.Count(v => v.Status != "lost" && v.Health == "ok");
        int volCrit = volumes.Count(v => v.Health == "crit");
        int volLost = volumes.Count(v => v.Status == "lost");
        int volTotal = volumes.Count;
        var volRows  = new (string Label, int Count, string Hex)[]
        {
            ("OK",       volOk,   "#4CAF50"),
            ("Critical", volCrit, "#EF5350"),
            ("Lost",     volLost, "#666677"),
        };
        foreach (var (lbl, cnt, hex) in volRows)
        {
            double pct = volTotal > 0 ? cnt * 100.0 / volTotal : 0.0;
            AnalyticsVolumeHealthPanel.Children.Add(
                MakeBarRow(lbl, cnt, Math.Max(volTotal, 1), $"{cnt:N0} ({pct:F1}%)", Color.Parse(hex),
                           labelWidth: 75, valueWidth: 110));
        }
    }

    // ── Shared helper ─────────────────────────────────────────────────────────

    private static TextBlock EmptyNote(string text) => new()
    {
        Text       = text,
        FontSize   = 12,
        Foreground = new SolidColorBrush(Color.Parse("#555566")),
    };

    /// <summary>
    /// Structured skeleton with 3 dim ghost bars + hint text.
    /// Used as a premium empty state for sections B, C, and A.
    /// </summary>
    private static StackPanel MakeSkeletonBars(string hint)
    {
        var panel = new StackPanel { Spacing = 8 };
        double[] widths  = [0.65, 0.40, 0.25];
        double[] opacity = [0.17, 0.11, 0.06];
        for (int i = 0; i < 3; i++)
        {
            var ghost = new StackPanel { Spacing = 4, Opacity = opacity[i] };
            ghost.Children.Add(new Border
            {
                Height              = 10,
                Width               = 90 * widths[i],
                Background          = new SolidColorBrush(Color.Parse("#5555AA")),
                CornerRadius        = new Avalonia.CornerRadius(2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            });
            ghost.Children.Add(new Border
            {
                Height     = 9,
                Background = new SolidColorBrush(Color.Parse("#333366")),
                CornerRadius = new Avalonia.CornerRadius(2),
            });
            panel.Children.Add(ghost);
        }
        panel.Children.Add(new TextBlock
        {
            Text       = hint,
            FontSize   = 11,
            Foreground = new SolidColorBrush(Color.Parse("#555566")),
            Margin     = new Avalonia.Thickness(0, 4, 0, 0),
        });
        return panel;
    }

    // ── Analytics event handlers ──────────────────────────────────────────────

    private void OnAnalyticsRefresh(object? sender, RoutedEventArgs e) => BuildAnalytics();

    private async void OnAnalyticsGenerateReports(object? sender, RoutedEventArgs e)
    {
        if (_analyticsData is null)
        {
            await new InfoDialog("No Data",
                "Refresh first to collect analytics data before generating reports.")
                .ShowDialog(this);
            return;
        }

        try
        {
            var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports", "analytics");
            Directory.CreateDirectory(reportsDir);
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            File.WriteAllText(
                Path.Combine(reportsDir, $"{ts}-analytics-compression-gain.html"),
                AnalyticsHtmlCompressionGain(_analyticsData), Encoding.UTF8);

            File.WriteAllText(
                Path.Combine(reportsDir, $"{ts}-analytics-artifact-types.html"),
                AnalyticsHtmlArtifactTypes(_analyticsData), Encoding.UTF8);

            File.WriteAllText(
                Path.Combine(reportsDir, $"{ts}-analytics-volume-heatmap.html"),
                AnalyticsHtmlVolumeHeatmap(_analyticsData), Encoding.UTF8);

            await new InfoDialog("Reports Generated",
                $"3 HTML reports written to:\n{reportsDir}").ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Report Error", ex.Message).ShowDialog(this);
        }
    }

    private void OnAnalyticsOpenFolder(object? sender, RoutedEventArgs e)
    {
        var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports", "analytics");
        Directory.CreateDirectory(reportsDir);
        try { Process.Start(new ProcessStartInfo(reportsDir) { UseShellExecute = true }); }
        catch { }
    }

    // ── HTML report builders ──────────────────────────────────────────────────

    private static string AnalyticsHtmlHeader(string title) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8"/>
        <title>Arkadia Analytics — {{title}}</title>
        <style>
        *{box-sizing:border-box;margin:0;padding:0;}
        body{font-family:'Segoe UI',sans-serif;background:#0A0A15;color:#CCCCDD;padding:36px;line-height:1.6;}
        h1{font-size:22px;font-weight:700;color:#7B68EE;margin-bottom:6px;}
        .sub{font-size:12px;color:#444455;margin-bottom:32px;}
        h2{font-size:11px;font-weight:600;letter-spacing:1.8px;color:#666677;text-transform:uppercase;margin:28px 0 14px;}
        .kpis{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin-bottom:32px;}
        .kpi{background:#0D0D1A;border:1px solid #1A1A2E;border-radius:8px;padding:16px;}
        .kpi-label{font-size:10px;font-weight:600;letter-spacing:1.5px;color:#444455;text-transform:uppercase;margin-bottom:4px;}
        .kpi-value{font-size:24px;font-weight:700;color:#CCCCDD;}
        table{width:100%;border-collapse:collapse;font-size:13px;}
        th{text-align:left;font-size:10px;font-weight:600;letter-spacing:1px;color:#555566;padding:6px 10px;border-bottom:1px solid #1A1A2E;}
        td{padding:8px 10px;border-bottom:1px solid #0F0F1E;}
        tr:hover td{background:#0D0D1A;}
        .bar-wrap{background:#111118;border-radius:3px;height:8px;}
        .bar{height:8px;border-radius:3px;display:block;}
        .badge{display:inline-block;padding:2px 8px;border-radius:3px;font-size:11px;font-weight:500;}
        .ok{background:#081A08;color:#4CAF50;}.crit{background:#1F0A0A;color:#EF5350;}
        .lost{background:#111118;color:#666677;}.present{background:#0A0D1F;color:#AAAACC;}
        </style>
        </head>
        <body>
        <h1>Arkadia Analytics — {{title}}</h1>
        <div class="sub">Generated {{DateTime.Now:yyyy-MM-dd HH:mm:ss}}</div>
        """;

    private static string AnalyticsHtmlFooter() => "\n</body>\n</html>";

    private static string AnalyticsHtmlCompressionGain(AnalyticsData d)
    {
        var sb = new StringBuilder();
        sb.Append(AnalyticsHtmlHeader("Compression Gain"));

        // KPI row
        sb.Append("<div class=\"kpis\">");
        void Kpi(string lbl, string val) =>
            sb.Append($"<div class=\"kpi\"><div class=\"kpi-label\">{lbl}</div><div class=\"kpi-value\">{val}</div></div>");
        Kpi("Source Size",    FormatBytes(d.TotalSourceBytes));
        Kpi("Derived Size",   FormatBytes(d.TotalDerivedBytes));
        Kpi("Space Saved",    d.TotalSourceBytes > 0 ? $"{d.SavedPct:F1}%" : "—");
        Kpi("Saved Absolute", d.TotalSourceBytes > 0 ? FormatBytes(d.SavedBytes) : "—");
        sb.Append("</div>");

        sb.Append("<h2>Compression by Strategy</h2>");
        sb.Append("<table><thead><tr><th>Strategy</th><th>Size</th><th>% of Derived</th><th style='width:220px'>Bar</th></tr></thead><tbody>");
        long maxB = d.DerivedByStrategy.Values.DefaultIfEmpty(0L).Max();
        foreach (var (sid, bytes) in d.DerivedByStrategy.OrderByDescending(kv => kv.Value))
        {
            double pct = d.TotalDerivedBytes > 0 ? bytes * 100.0 / d.TotalDerivedBytes : 0.0;
            var    nm  = d.StrategyNames.TryGetValue(sid, out var n) ? n : sid;
            int    bw  = maxB > 0 ? (int)(bytes * 200.0 / maxB) : 0;
            sb.Append($"<tr><td>{nm}</td><td>{FormatBytes(bytes)}</td><td>{pct:F1}%</td>" +
                      $"<td><div class=\"bar-wrap\"><div class=\"bar\" style=\"width:{bw}px;background:#7B68EE\"></div></div></td></tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append(AnalyticsHtmlFooter());
        return sb.ToString();
    }

    private static string AnalyticsHtmlArtifactTypes(AnalyticsData d)
    {
        var sb = new StringBuilder();
        sb.Append(AnalyticsHtmlHeader("Artifact Type Distribution"));

        int total = d.ExtensionCounts.Values.Sum();
        sb.Append($"<p style='color:#888899;margin-bottom:24px;font-size:13px;'>{total:N0} total artifacts across {d.ExtensionCounts.Count} distinct file types.</p>");
        sb.Append("<table><thead><tr><th>Extension</th><th>Count</th><th>% of Total</th><th style='width:220px'>Bar</th></tr></thead><tbody>");
        int maxC = d.ExtensionCounts.Values.DefaultIfEmpty(0).Max();
        foreach (var (ext, cnt) in d.ExtensionCounts.OrderByDescending(kv => kv.Value))
        {
            double pct = total > 0 ? cnt * 100.0 / total : 0.0;
            int    bw  = maxC > 0 ? (int)(cnt * 200.0 / maxC) : 0;
            sb.Append($"<tr><td>{ext}</td><td>{cnt:N0}</td><td>{pct:F1}%</td>" +
                      $"<td><div class=\"bar-wrap\"><div class=\"bar\" style=\"width:{bw}px;background:#29B6F6\"></div></div></td></tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append(AnalyticsHtmlFooter());
        return sb.ToString();
    }

    private static string AnalyticsHtmlVolumeHeatmap(AnalyticsData d)
    {
        var sb = new StringBuilder();
        sb.Append(AnalyticsHtmlHeader("Volume Heatmap"));

        sb.Append($"<p style='color:#888899;margin-bottom:24px;font-size:13px;'>{d.Volumes.Count:N0} volumes tracked.</p>");
        sb.Append("<table><thead><tr><th>Label</th><th>Platform</th><th>Status</th><th>Health</th><th>Size</th></tr></thead><tbody>");
        foreach (var vol in d.Volumes.OrderBy(v => v.Label))
        {
            var pn = d.PlatformNames.TryGetValue(vol.PlatformId, out var p) ? p : vol.PlatformId;
            var sc = vol.Status == "lost" ? "lost"    : "present";
            var hc = vol.Health == "crit" ? "crit"   : "ok";
            sb.Append($"<tr><td>{vol.Label}</td><td>{pn}</td>" +
                      $"<td><span class=\"badge {sc}\">{vol.Status}</span></td>" +
                      $"<td><span class=\"badge {hc}\">{vol.Health}</span></td>" +
                      $"<td>{FormatBytes(vol.ActualSizeBytes)}</td></tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append(AnalyticsHtmlFooter());
        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Arkadia.Data;
using Arkadia.Ingestion;
using Arkadia.Library;
using Arkadia.Providers;
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
using LibVLCSharp.Shared;

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
    private IReadOnlyList<Arkadia.Data.MetadataValueMappingRecord> _metadataMappings = [];
    private readonly ObservableCollection<MappingRowVm> _mappingRows = [];
    private readonly Arkadia.Providers.ScreenScraperImportService _scrapeImport = new(_dataDir);
    private Arkadia.Providers.ScreenScraperCacheImportService? _cacheImport;
    private Arkadia.Providers.AmpLocalPackageImportService?   _ampImport;
    private bool _showDebugArtifactInfo;
    private bool _arkBusy;

    public MainWindow()
    {
        InitializeComponent();
        ArkadiaFolders.EnsureCreated(AppContext.BaseDirectory);
        SizeChanged += (_, _) => UpdateCatalogResponsiveLayout();
        _metadataMappings      = _catalog.LoadMetadataValueMappings();
        _showDebugArtifactInfo = _catalog.GetBoolSetting("show_debug_artifact_info");

        _navButtons.AddRange([
            NavDashboard, NavAnalytics,
            NavProviders, NavSystems, NavOperations,
            NavLibrary, NavCatalog, NavStaging, NavPending,
            NavVolumes, NavDisks,
            NavLogs, NavBackups, NavSettings,
        ]);

        _views = new()
        {
            [NavDashboard]  = ViewDashboard,
            [NavSystems]    = ViewSystems,
            [NavPending]    = ViewPending,
            [NavStaging]    = ViewStaging,
            [NavLibrary]    = ViewLibrary,
            [NavCatalog]    = ViewCatalog,
            [NavDisks]      = ViewDisks,
            [NavVolumes]    = ViewVolumes,
            [NavOperations] = ViewOperations,
            [NavAnalytics]  = ViewAnalytics,
            [NavProviders]  = ViewProviders,
            [NavLogs]       = ViewLogs,
            [NavBackups]    = ViewBackups,
            [NavSettings]   = ViewSettings,
        };

        InitSystems();
        InitPending();
        InitStaging();
        InitLibrary();
        InitCatalog();
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
    private Dictionary<string, string> _authorityNameMap  = [];

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
        _hardwareTypeMap  = _catalog.LoadHardwareTypes()
            .ToDictionary(h => h.Id, h => h.Name);
        _strategyNameMap  = _catalog.LoadStorageStrategies()
            .ToDictionary(s => s.Id, s => s.Name);
        _authorityNameMap = _catalog.LoadAuthorities()
            .ToDictionary(a => a.Id, a => a.Name);

        var allDatLines = _catalog.LoadDatLines();

        _systemsPlatforms = _catalog.LoadPlatforms()
            .Select(platform =>
            {
                var platformDatLines = allDatLines.Where(dl => dl.HardwareFamilyId == platform.Id).ToList();
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

        SysActEditPlatform.IsEnabled   = hasPlatform;
        SysActDeletePlatform.IsEnabled = hasPlatform;
        SysActConfigureDat.IsEnabled   = datHasStore;
        SysActUpdateDat.IsEnabled     = hasDat;
        SysActVerifyAll.IsEnabled     = datHasStore;
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
                Text         = "No systems. Click 'Create System' to add one.",
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

        var logoImg = LoadSystemImage(p.Id, "logo", Systems.PlatformImageSizes.Logo);

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
            "release_shape"  => "Per release shape",
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
        var path = Path.Combine(_dataDir, "authorityimages", $"{authority}.png");
        if (!File.Exists(path)) return null;
        try { return new Bitmap(path); } catch { return null; }
    }

    private string GetAuthorityName(string id)
    {
        if (id.Length == 0) return id;
        return _authorityNameMap.TryGetValue(id, out var name) ? name : id;
    }

    private string FormatDatLineName(string authorityId, string mediaTypeId)
        => $"{GetAuthorityName(authorityId)} · {mediaTypeId.ToUpperInvariant()}";

    private Bitmap? LoadSystemImage(string platformId, string role, (int Width, int Height) size)
    {
        var imageDir = Path.Combine(_dataDir, "systemimages");

        var cachedName = Systems.PlatformImageCache.CachedFileName(platformId, role, size.Width, size.Height);
        var cachedPath = Path.Combine(imageDir, cachedName);
        if (File.Exists(cachedPath))
            try { return new Bitmap(cachedPath); } catch { }

        var sourcePath = Path.Combine(imageDir, Systems.PlatformImageCache.SourceFileName(platformId, role));
        if (File.Exists(sourcePath))
            try { return new Bitmap(sourcePath); } catch { }

        return null;
    }

    private Bitmap? LoadSystemImageW(string platformId, string role, int width)
    {
        var imageDir = Path.Combine(_dataDir, "systemimages");

        var cachedPath = Path.Combine(imageDir, Systems.PlatformImageCache.CachedWidthFileName(platformId, role, width));
        if (File.Exists(cachedPath))
            try { return new Bitmap(cachedPath); } catch { }

        var sourcePath = Path.Combine(imageDir, Systems.PlatformImageCache.SourceFileName(platformId, role));
        if (File.Exists(sourcePath))
            try { return new Bitmap(sourcePath); } catch { }

        return null;
    }

    private void DeletePlatformImageFiles(string imageDir, string platformId, string role)
    {
        void TryDel(string path) { if (File.Exists(path)) File.Delete(path); }

        TryDel(Path.Combine(imageDir, $"{platformId}-{role}.png"));
        TryDel(Path.Combine(imageDir, Systems.PlatformImageCache.SourceFileName(platformId, role)));
        foreach (var (w, h) in Systems.PlatformImageSizes.All)
            TryDel(Path.Combine(imageDir, Systems.PlatformImageCache.CachedFileName(platformId, role, w, h)));
        foreach (var w in Systems.PlatformImageSizes.AllWidthConstrained)
            TryDel(Path.Combine(imageDir, Systems.PlatformImageCache.CachedWidthFileName(platformId, role, w)));
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

        // Platform logo (width-constrained, above main image)
        var logoW300 = LoadSystemImageW(p.Id, "logo", Systems.PlatformImageSizes.DetailLogoWidth);
        if (logoW300 is not null)
            panel.Children.Add(new Image
            {
                Source              = logoW300,
                Width               = 300,
                Stretch             = Avalonia.Media.Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin              = new Avalonia.Thickness(0, 0, 0, 10),
            });

        // Platform image
        var img = LoadSystemImage(p.Id, "details", Systems.PlatformImageSizes.Detail)
               ?? LoadSystemImage(p.Id, "logo",    Systems.PlatformImageSizes.Detail);
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

        // Compute status counts and unassigned artifact stats from store
        int  present         = 0;
        int  reconciliation  = 0;  // = 'lost' releases only
        int  unassignedCount = 0;
        long unassignedBytes = 0;
        if (d.DataStorePath.Length > 0)
        {
            var absPath = Path.Combine(_dataDir, d.DataStorePath);
            if (File.Exists(absPath))
            {
                var datStore   = new DatLineStore(absPath);
                var counts     = datStore.GetAllStatusCounts();
                present        = counts.Present;
                reconciliation = counts.Lost;

                var assignedIds = d.CatalogId is not null
                    ? _catalog.GetAssignedDerivedIdsByDatLine(d.CatalogId)
                    : new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                (unassignedCount, unassignedBytes) = datStore.GetUnassignedPresentStats(assignedIds);
            }
        }
        var coverage = d.Releases > 0
            ? $"{(double)present / d.Releases:P1}"
            : "—";

        // ── Header ───────────────────────────────────────────────────────────

        // Platform logo
        if (d.CatalogPlatformId is not null)
        {
            var platformImg = LoadSystemImageW(d.CatalogPlatformId, "logo", Systems.PlatformImageSizes.DetailLogoWidth);
            if (platformImg is not null)
                panel.Children.Add(new Image
                {
                    Source              = platformImg,
                    MaxWidth            = Systems.PlatformImageSizes.DetailLogoWidth,
                    Stretch             = Avalonia.Media.Stretch.Uniform,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Margin              = new Avalonia.Thickness(0, 0, 0, 12),
                });
        }

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
        AddStatusRow("Unassigned",     $"{unassignedCount:N0}");
        AddStatusRow("Unassigned Size", FormatBytes(unassignedBytes));

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
        else if (d.TransformStrategyType == "release_shape")
        {
            panel.Children.Add(new TextBlock
            {
                Text       = "Per release shape",
                FontSize   = 12,
                FontWeight = FontWeight.Medium,
                Foreground = text,
                Margin     = new Avalonia.Thickness(0, 0, 0, 4),
            });
            panel.Children.Add(new TextBlock
            {
                Text       = ".iso → CHD DVD Compression   ·   .cue+.bin → CHD CD Compression",
                FontSize   = 11,
                Foreground = dim,
            });
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
            .Where(dl => dl.HardwareFamilyId == platformId)
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
                    Name:                  FormatDatLineName(dl.Authority, dl.MediaTypeId),
                    Releases:              dl.ReleaseCount,
                    Outdated:              outdated,
                    LastImport:            dl.ImportedAtUtc.ToString("yyyy-MM-dd"),
                    StorageStrategy:       _strategyNameMap.TryGetValue(dl.StorageStrategyId, out var sn) ? sn : "",
                    Authority:             dl.Authority,
                    MediaTypeId:           dl.MediaTypeId,
                    DataStorePath:         dl.DataStorePath,
                    CatalogId:             dl.Id,
                    CatalogPlatformId:     dl.HardwareFamilyId,
                    TransformStrategyType: dl.TransformStrategyType,
                    FolderTransformId:     dl.FolderTransformId,
                    FileHandling:          dl.FileHandling);
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

    private void OnSysVerifyAll(object? sender, RoutedEventArgs e)
    {
        if (_selectedDatLine is null) return;
        _ = OnVerifyAllDatLine(_selectedDatLine);
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

    private void OnOpenDatDownloader(object? sender, RoutedEventArgs e) => SetActive(NavProviders);

    // ── Providers view — category tabs ────────────────────────────────────────

    private void OnProviderCategoryRomDats(object? sender, RoutedEventArgs e)
    {
        ProvidersTabRomDats.Classes.Set("active", true);
        ProvidersTabRomScrapers.Classes.Set("active", false);
        ProvidersTabMediaPacks.Classes.Set("active", false);
        ProvidersRomDatsPanel.IsVisible     = true;
        ProvidersRomScrapersPanel.IsVisible = false;
        ProvidersAmpPanel.IsVisible         = false;
    }

    private void OnProviderCategoryRomScrapers(object? sender, RoutedEventArgs e)
    {
        ProvidersTabRomDats.Classes.Set("active", false);
        ProvidersTabRomScrapers.Classes.Set("active", true);
        ProvidersTabMediaPacks.Classes.Set("active", false);
        ProvidersRomDatsPanel.IsVisible     = false;
        ProvidersRomScrapersPanel.IsVisible = true;
        ProvidersAmpPanel.IsVisible         = false;
        LoadScraperSettings();
    }

    private void OnProviderCategoryMediaPacks(object? sender, RoutedEventArgs e)
    {
        ProvidersTabRomDats.Classes.Set("active", false);
        ProvidersTabRomScrapers.Classes.Set("active", false);
        ProvidersTabMediaPacks.Classes.Set("active", true);
        ProvidersRomDatsPanel.IsVisible     = false;
        ProvidersRomScrapersPanel.IsVisible = false;
        ProvidersAmpPanel.IsVisible         = true;
    }

    private void LoadScraperSettings()
    {
        ScraperSSUsername.Text    = _catalog.GetSetting("screenscraper_username");
        ScraperSSPassword.Text    = _catalog.GetSetting("screenscraper_password");
        ScraperSSDevId.Text       = _catalog.GetSetting("screenscraper_dev_id");
        ScraperSSDevPassword.Text = _catalog.GetSetting("screenscraper_dev_password");
        ScraperSSSoftname.Text = _catalog.GetSetting("screenscraper_softname");
        ScraperTestStatus.IsVisible = false;
    }

    private void OnSaveScraperSettings(object? sender, RoutedEventArgs e)
    {
        _catalog.SetSetting("screenscraper_username",     ScraperSSUsername.Text?.Trim()    ?? "");
        _catalog.SetSetting("screenscraper_password",     ScraperSSPassword.Text?.Trim()    ?? "");
        _catalog.SetSetting("screenscraper_dev_id",       ScraperSSDevId.Text?.Trim()       ?? "");
        _catalog.SetSetting("screenscraper_dev_password", ScraperSSDevPassword.Text?.Trim() ?? "");
        var rawSoftname = ScraperSSSoftname.Text?.Trim() ?? "";
        if (rawSoftname.Length == 0)
        {
            ScraperTestStatus.Text      = "ScreenScraper Softname is required.";
            ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#EF5350"));
            ScraperTestStatus.IsVisible  = true;
            return;
        }
        _catalog.SetSetting("screenscraper_softname", rawSoftname);
        ScraperTestStatus.Text      = "Saved.";
        ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#4CAF50"));
        ScraperTestStatus.IsVisible  = true;
    }

    private async void OnTestScraperConnection(object? sender, RoutedEventArgs e)
    {
        var username    = ScraperSSUsername.Text?.Trim()    ?? "";
        var password    = ScraperSSPassword.Text?.Trim()    ?? "";
        var devId       = ScraperSSDevId.Text?.Trim()       ?? "";
        var devPassword = ScraperSSDevPassword.Text?.Trim() ?? "";
        var softName    = ScraperSSSoftname.Text?.Trim() ?? "";

        if (username.Length == 0 || password.Length == 0 || devId.Length == 0 || devPassword.Length == 0)
        {
            ScraperTestStatus.Text      = "ScreenScraper API requires both user credentials and API developer credentials.";
            ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#FFD54F"));
            ScraperTestStatus.IsVisible  = true;
            return;
        }
        if (softName.Length == 0)
        {
            ScraperTestStatus.Text      = "ScreenScraper Softname is required.";
            ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#EF5350"));
            ScraperTestStatus.IsVisible  = true;
            return;
        }

        ScraperTestBtn.IsEnabled    = false;
        ScraperTestStatus.Text      = "Testing…";
        ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#888899"));
        ScraperTestStatus.IsVisible  = true;

        try
        {
            var display = await Arkadia.Providers.ScreenScraperClient.TestConnectionAsync(
                devId, devPassword, username, password, softName: softName);
            ScraperTestStatus.Text      = $"Connected — logged in as {display}.";
            ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#4CAF50"));
        }
        catch (Arkadia.Providers.ScreenScraperRateLimitException)
        {
            ScraperTestStatus.Text      = "Rate limited. Wait a moment and try again.";
            ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#EF5350"));
        }
        catch (Exception ex)
        {
            ScraperTestStatus.Text      = $"Failed: {ex.Message}";
            ScraperTestStatus.Foreground = new SolidColorBrush(Color.Parse("#EF5350"));
        }
        finally
        {
            ScraperTestBtn.IsEnabled = true;
        }
    }

    // ── Providers view — provider CTA buttons ─────────────────────────────────

    private async void OnProviderRedumpOpen(object? sender, RoutedEventArgs e)
    {
        var win = new RedumpProviderWindow();
        await win.ShowDialog(this);
    }

    private async void OnProviderTosecOpen(object? sender, RoutedEventArgs e)
    {
        var win = new TosecProviderWindow();
        await win.ShowDialog(this);
    }

    private async void OnProviderNoIntroOpen(object? sender, RoutedEventArgs e)
    {
        var win = new NoIntroProviderWindow();
        await win.ShowDialog(this);
    }

    private async void OnProviderEggmansworldOpen(object? sender, RoutedEventArgs e)
    {
        var win = new EggmansworldProviderWindow();
        await win.ShowDialog(this);
    }

    private async void OnProviderMameOpen(object? sender, RoutedEventArgs e)
    {
        var win = new MameProviderWindow(_catalog);
        await win.ShowDialog(this);
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

    /// <summary>
    /// When navigating to Library, automatically select the platform and DAT line that
    /// was last active in Systems view — if they still exist in the Library datasets.
    /// Falls back to platform-only, then to current Library selection.
    /// No-op when there is no active Systems selection.
    /// </summary>
    private void ApplySystemsContextToLibrary()
    {
        if (_selectedPlatformId is null) return;

        var platform = _systemsPlatforms.FirstOrDefault(p => p.Id == _selectedPlatformId)?.Name;
        if (platform is null) return;

        // Check the platform exists in Library datasets.
        if (!_activeLibraryDatasets.Any(d => d.Platform == platform)) return;

        if (_selectedDatLine is not null)
        {
            var datLine = _selectedDatLine.Name;
            // Prefer: exact platform + DAT line match.
            if (_activeLibraryDatasets.Any(d => d.Platform == platform && d.DatLine == datLine))
            {
                NavigateToLibraryInternal(platform, datLine);
                return;
            }
        }

        // Fallback: platform exists but DAT line not found — select first DAT for that platform.
        NavigateToLibraryInternal(platform, datLine: null);
    }

    /// <summary>
    /// Sets Library selectors to <paramref name="platform"/> and optionally <paramref name="datLine"/>
    /// without switching the active view (called from within view-switch logic).
    /// </summary>
    private void NavigateToLibraryInternal(string platform, string? datLine)
    {
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

        string? resolvedDatLine;
        if (datLine is not null)
        {
            var dIdx = datLines.IndexOf(datLine);
            LibraryContextDatLine.SelectedIndex = dIdx >= 0 ? dIdx : 0;
            resolvedDatLine = dIdx >= 0 ? datLine : (datLines.Count > 0 ? datLines[0] : null);
        }
        else
        {
            LibraryContextDatLine.SelectedIndex = datLines.Count > 0 ? 0 : -1;
            resolvedDatLine = datLines.Count > 0 ? datLines[0] : null;
        }

        LibraryContextPlatform.SelectionChanged += OnLibraryContextPlatformChanged;
        LibraryContextDatLine.SelectionChanged  += OnLibraryContextDatLineChanged;

        LoadActiveDataset(platform, resolvedDatLine);
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
                var datLineName = allDatLines.TryGetValue(datLineId, out var dl) ? FormatDatLineName(dl.Authority, dl.MediaTypeId) : datLineId;

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
        StagingDetailPlatform.Text = _catalog.GetHardwareFamily(release.PlatformId)?.Name ?? release.PlatformId;
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

    // Single source of truth for Library selection — tracks by ReleaseId, not list index.
    private readonly LibrarySelectionState _librarySelection = new();

    // ── Disks ─────────────────────────────────────────────────────────────────

    private List<DiskEntry> _allDiskEntries  = [];
    private List<DiskEntry> _filteredDisks   = [];
    private DiskEntry?      _selectedDiskEntry;

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
                Family                = d.Family,
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

        _selectedDiskEntry = null;
        DisksCountText.Text = _filteredDisks.Count == _allDiskEntries.Count
            ? $"{_allDiskEntries.Count} disks"
            : $"{_filteredDisks.Count} of {_allDiskEntries.Count} disks";
        RenderDisksPanel();
        UpdateDiskDetailPanel(null);
    }

    private void OnDisksSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        => ApplyDisksFilter();

    private static readonly string[] DiskFamilyOrder = ["core", "extras", "books"];

    private void RenderDisksPanel()
    {
        DisksPanel.Children.Clear();
        if (_filteredDisks.Count == 0) return;

        var groups = DiskFamilyOrder
            .Select(f => (Family: f, Items: _filteredDisks.Where(d => d.Family == f).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();

        var showHeaders = groups.Count > 1;

        foreach (var (family, items) in groups)
        {
            if (showHeaders)
                DisksPanel.Children.Add(MakeGroupHeader(family));

            foreach (var entry in items)
                DisksPanel.Children.Add(MakeDiskRow(entry));
        }
    }

    private Border MakeDiskRow(DiskEntry entry)
    {
        bool isSelected    = _selectedDiskEntry?.Id == entry.Id;
        var  textPrimary   = new SolidColorBrush(isSelected ? Color.Parse("#E8E8FF") : Color.Parse("#CCCCDD"));
        var  textSecondary = new SolidColorBrush(Color.Parse("#888899"));
        var  fillRatio     = entry.UsageRatio;  // already clamped 0..1 in DiskEntry

        var grid = new Grid { Margin = new Avalonia.Thickness(20, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(52)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(200)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(100)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(130)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(130)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        void Add(int col, Control ctrl) { Grid.SetColumn(ctrl, col); grid.Children.Add(ctrl); }

        Add(0, new TextBlock
        {
            Text                = entry.StatusLabel,
            FontSize            = 11,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = entry.StatusBrush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
        });
        Add(1, new TextBlock
        {
            Text             = entry.Label,
            FontSize         = 13,
            Foreground       = textPrimary,
            TextTrimming     = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        Add(2, new TextBlock
        {
            Text                = entry.CapacityLabel,
            FontSize            = 12,
            Foreground          = textSecondary,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
        });
        Add(3, new TextBlock
        {
            Text                = entry.UsedLabel,
            FontSize            = 12,
            Foreground          = textSecondary,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
        });
        Add(4, new TextBlock
        {
            Text                = entry.FreeLabel,
            FontSize            = 12,
            Foreground          = textSecondary,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
        });
        Add(5, new TextBlock
        {
            Text                = entry.FilesystemLabel,
            FontSize            = 12,
            Foreground          = textSecondary,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
        });
        Add(6, new TextBlock
        {
            Text             = entry.ModelLabel,
            FontSize         = 12,
            Foreground       = textSecondary,
            TextTrimming     = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        Add(7, new TextBlock
        {
            Text             = entry.SerialLabel,
            FontSize         = 12,
            Foreground       = textSecondary,
            TextTrimming     = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        // Proportional bar: fill column = fillRatio*, empty column = (1-fillRatio)*.
        // Adapts to the Star column's actual pixel width — no magic constant needed.
        var barInner = new Grid();
        barInner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(fillRatio, GridUnitType.Star)));
        barInner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.0 - fillRatio, GridUnitType.Star)));
        var barFill = new Border
        {
            Height       = 8,
            CornerRadius = new Avalonia.CornerRadius(1),
            Background   = new SolidColorBrush(Color.Parse("#4CAF50")),
        };
        Grid.SetColumn(barFill, 0);
        barInner.Children.Add(barFill);

        Add(8, new Border
        {
            Height            = 8,
            CornerRadius      = new Avalonia.CornerRadius(1),
            Background        = new SolidColorBrush(Color.Parse("#2A2A3E")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin            = new Avalonia.Thickness(8, 0),
            Child             = barInner,
        });

        var row = new Border
        {
            Background = new SolidColorBrush(isSelected ? Color.Parse("#1E1E2E") : Colors.Transparent),
            Height     = 36,
            Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child      = grid,
        };
        var diskId = entry.Id;
        row.PointerPressed += (_, _) => SelectDisk(diskId);
        return row;
    }

    private void SelectDisk(string diskId)
    {
        _selectedDiskEntry = _filteredDisks.FirstOrDefault(d => d.Id == diskId);
        RenderDisksPanel();
        UpdateDiskDetailPanel(_selectedDiskEntry);
    }

    private async void OnAddDisk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new CreateDiskDialog();
        var ok     = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.Result is null || dialog.SelectedDrive is null) return;

        var mountpoint = dialog.SelectedDrive.Mountpoint;

        try
        {
            // ── Commit label sequence ─────────────────────────────────────────
            var confirmedLabel = _catalog.NextDiskLabel(dialog.Result.Family);

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
                Family                = raw.Family,
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
        var entry = _selectedDiskEntry;
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
                SelectDisk(updated.Id);

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
        var entry = _selectedDiskEntry;
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
                SelectDisk(updated.Id);

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
        var entry = _selectedDiskEntry;
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

    private async void OnDeleteDisk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = _selectedDiskEntry;
        if (entry is null) return;

        if (entry.Status != "lost")
        {
            await new InfoDialog(
                "Cannot Delete Disk",
                "Before deleting the disk you must mark it as Lost with the Mark Lost button.")
                .ShowDialog(this);
            return;
        }

        if (_catalog.HasActiveDiskVolumes(entry.Id))
        {
            await new InfoDialog(
                "Cannot Delete Disk",
                $"Disk \"{entry.Label}\" still has volumes assigned to it that are not marked LOST.\n\n" +
                "Mark all volumes on this disk as LOST before deleting the disk.")
                .ShowDialog(this);
            return;
        }

        var confirmed = await new ConfirmDialog(
            "Delete Disk",
            $"Permanently remove disk \"{entry.Label}\" from the catalog?\n\n" +
            "Volume location history for this disk will be deleted.\n" +
            "Volumes previously on this disk will remain in the catalog.\n\n" +
            "This action cannot be undone.")
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        _catalog.DeleteDisk(entry.Id);

        _selectedDiskEntry = null;
        RefreshDisks();
        RefreshVolumes();
        RefreshAnalyticsIfBuilt();
        UpdateDiskDetailPanel(null);
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

        // Only list volumes that have actual data — matches what appears in the bar.
        var trackedVolumes  = volumes.Where(v => v.ActualSizeBytes > 0).ToList();
        long trackedBytes   = trackedVolumes.Sum(v => v.ActualSizeBytes);
        long untrackedBytes = Math.Max(0L, entry.UsedBytes - trackedBytes);

        foreach (var v in trackedVolumes)
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

        // Untracked usage: disk.UsedBytes exceeds the sum of tracked volume sizes.
        // This covers filesystem overhead, partition tables, non-volume files, etc.
        if (untrackedBytes > 0)
        {
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
                        Background        = new SolidColorBrush(Color.Parse("#3A3A52")),
                        BorderBrush       = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                        BorderThickness   = new Avalonia.Thickness(1),
                        Margin            = new Avalonia.Thickness(0, 0, 9, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1,
                        Text         = "Other disk usage",
                        FontSize     = 12,
                        Foreground   = new SolidColorBrush(Color.Parse("#888899")),
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 2,
                        Text       = FormatBytes(untrackedBytes),
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

        long trackedBytes = 0;
        foreach (var v in volumes)
        {
            if (v.ActualSizeBytes <= 0) continue;
            trackedBytes += v.ActualSizeBytes;
            var ratio = Math.Clamp((double)v.ActualSizeBytes / disk.DeclaredCapacityBytes, 0, 1);
            var segW  = Math.Max(2, ratio * BarWidth);
            panel.Children.Add(new Border
            {
                Width      = segW,
                Height     = 17,
                Background = new SolidColorBrush(GetVolumeColor(v.Id)),
            });
        }

        // Untracked used space (filesystem overhead, non-volume files, etc.)
        long untrackedBytes = Math.Max(0L, disk.UsedBytes - trackedBytes);
        if (untrackedBytes > 0)
        {
            var untrackedRatio = Math.Clamp((double)untrackedBytes / disk.DeclaredCapacityBytes, 0, 1);
            var untrackedW     = Math.Max(2, untrackedRatio * BarWidth);
            panel.Children.Add(new Border
            {
                Width      = untrackedW,
                Height     = 17,
                Background = new SolidColorBrush(Color.Parse("#3A3A52")),
            });
        }

        // Free space segment — based on disk.UsedBytes so it always fills the remainder exactly
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
        "#42A5F5", "#AB47BC", "#26A69A", "#5C6BC0", "#3949AB",
        "#8D6E63", "#78909C", "#7E57C2", "#EC407A", "#0288D1",
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

    /// <summary>
    /// Rebuilds Analytics in-place if it has been opened at least once.
    /// Safe no-op on first launch before the user visits the Analytics tab.
    /// </summary>
    private void RefreshAnalyticsIfBuilt()
    {
        if (_analyticsData is not null)
            BuildAnalytics();
    }

    /// <summary>
    /// Refreshes disk data and restores the previously selected disk in Disk Details.
    /// Use instead of a bare RefreshDisks() whenever the detail panel must stay current
    /// after a volume-changing operation.
    /// </summary>
    private void RefreshDiskDetailIfSelected()
    {
        var prevId = _selectedDiskEntry?.Id;
        RefreshDisks();   // rebuilds _allDiskEntries; clears panel
        if (prevId is null) return;
        var restored = _filteredDisks.FirstOrDefault(d => d.Id == prevId);
        if (restored is null) return;
        SelectDisk(restored.Id);
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
                ArtifactCount    = _catalog.GetVolumeArtifacts(v.Id).Count,
            };
        }).ToList();

        ApplyVolumesFilter();
    }

    private void ApplyVolumesFilter()
    {
        _filteredVolumes        = _allVolumeEntries;
        VolumesList.ItemsSource = _filteredVolumes;
        UpdateVolumeDetailPanel(null);
    }

    private void OnVolumeSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => UpdateVolumeDetailPanel(VolumesList.SelectedItem as VolumeEntry);

    private async void OnCreateVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var platforms      = _catalog.LoadPlatforms();
        var datLines       = _catalog.LoadDatLines();
        var existingLabels = _catalog.GetVolumes().Select(v => v.Label).ToList();
        var dialog = new CreateVolumeDialog(platforms, datLines, existingLabels, _catalog, _dataDir);
        dialog.FinishInit();
        var ok = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.Result is null) return;

        _catalog.SaveVolume(dialog.Result);
        RefreshVolumes();
    }

    private void UpdateVolumeDetailPanel(VolumeEntry? entry)
    {
        bool hasVol      = entry is not null;
        bool isPhysical  = hasVol && entry!.Status == "present";
        bool isNotLost   = hasVol && entry!.Status != "lost";

        VolActMake.IsEnabled     = isNotLost;    // plan available for init + present; disabled for lost
        VolActMove.IsEnabled     = isPhysical;   // requires physical source
        VolActResize.IsEnabled   = isNotLost;
        VolActAppend.IsEnabled   = isPhysical;   // requires files on disk/workspace
        VolActReabsorb.IsEnabled = isPhysical;   // requires physical source
        VolActMarkLost.IsEnabled    = isNotLost;  // already-lost volumes are no-ops
        VolActDeleteVolume.IsEnabled = hasVol;    // enforcement is at click time

        VolActVerifyVolume.IsEnabled = hasVol
            && entry!.ArtifactCount > 0
            && entry.DbPath.Length > 0 && File.Exists(entry.DbPath);

        VolActRepair.IsEnabled = hasVol
            && entry!.StatusLabel is "WARNING" or "LOST"
            && entry.DbPath.Length > 0 && File.Exists(entry.DbPath);

        VolActArtifacts.IsEnabled = hasVol
            && entry!.ArtifactCount > 0
            && entry.DbPath.Length > 0 && File.Exists(entry.DbPath);

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

    /// <summary>
    /// Resolves the physical root directory for a volume — workspace first, then mounted disk.
    /// Returns null when the volume is not currently accessible.
    /// </summary>
    private string? ResolveVolumeRoot(Volumes.VolumeEntry entry, string appRoot)
    {
        var wsRoot = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));
        if (Directory.Exists(wsRoot)) return wsRoot;

        if (entry.DiskId is not null)
        {
            var runtimeDisks = Data.DiskDiscoveryService.DiscoverAll()
                .Where(d => d.DiskId.Length > 0)
                .ToDictionary(d => d.DiskId, StringComparer.Ordinal);
            if (runtimeDisks.TryGetValue(entry.DiskId, out var rt))
            {
                var diskRoot = Path.Combine(rt.Mountpoint, SafeFileName(entry.Label));
                if (Directory.Exists(diskRoot)) return diskRoot;
            }
        }

        return null;
    }

    private async void OnVerifyVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as Volumes.VolumeEntry;
        if (entry is null) return;
        if (entry.DbPath.Length == 0 || !File.Exists(entry.DbPath)) return;

        var appRoot = AppContext.BaseDirectory;

        // ── Resolve volume root (with one retry) ──────────────────────────
        string? volumeRoot = ResolveVolumeRoot(entry, appRoot);
        if (volumeRoot is null)
        {
            var retry = await new ConfirmDialog("Volume Not Accessible",
                $"Volume \"{entry.Label}\" could not be found in the workspace or on a mounted disk.\n\n" +
                "Mount the disk containing this volume, then click OK to retry, or Cancel to abort.")
                .ShowDialog<bool>(this);
            if (!retry) return;

            volumeRoot = ResolveVolumeRoot(entry, appRoot);
            if (volumeRoot is null)
            {
                await new InfoDialog("Still Not Accessible",
                    $"Volume \"{entry.Label}\" is still not accessible.\n\n" +
                    "Verify aborted — no changes were made.")
                    .ShowDialog(this);
                return;
            }
        }

        // ── Load artifact list ─────────────────────────────────────────────
        var store       = new Data.DatLineStore(entry.DbPath);
        var vaIds       = _catalog.GetVolumeArtifacts(entry.Id)
                                  .Select(va => va.DerivedArtifactId).ToList();
        var verifyInfos = store.GetArtifactVerifyInfos(vaIds);

        if (verifyInfos.Count == 0)
        {
            await new InfoDialog("Nothing to Verify",
                $"Volume \"{entry.Label}\" has no assigned artifacts.")
                .ShowDialog(this);
            return;
        }

        // ── Prepare log ───────────────────────────────────────────────────
        bool logEnabled = _catalog.GetBoolSetting("log_on_copy", true);
        var  log        = logEnabled ? new System.Text.StringBuilder() : null;
        var  startTime  = DateTime.UtcNow;
        var  volSlug    = SafeFileName(entry.Label);
        bool wasLost    = entry.Status == "lost";

        if (log is not null)
        {
            log.AppendLine("Volume Verify");
            log.AppendLine($"Started:   {startTime:o}");
            log.AppendLine($"Volume:    {entry.Label}  (status={entry.Status})");
            log.AppendLine($"Root:      {volumeRoot}");
            log.AppendLine($"Artifacts: {verifyInfos.Count}");
            log.AppendLine();
        }

        // ── Show dialog ───────────────────────────────────────────────────
        var dlg     = new DatLineVerifyDialog(entry.DatLineId, entry.PlatformId);
        var dlgTask = dlg.ShowDialog(this);

        int okCount = 0, missingCount = 0, mismatchCount = 0;
        var presentDaIds = new List<string>();
        var badDaIds     = new List<string>();
        string? errorMessage = null;

        try
        {
            await Task.Run(async () =>
            {
                int processed = 0;
                foreach (var vi in verifyInfos)
                {
                    var absPath = Path.Combine(volumeRoot, vi.FileName);
                    string result, detail;

                    if (!File.Exists(absPath))
                    {
                        missingCount++;
                        badDaIds.Add(vi.DerivedArtifactId);
                        result = "MISSING";
                        detail = "";
                        log?.AppendLine($"MISSING  {vi.FileName}  ({vi.ReleaseName})");
                    }
                    else if (vi.Sha1.Length > 0)
                    {
                        var actual = ComputeFileSha1(absPath);
                        if (string.Equals(actual, vi.Sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            okCount++;
                            presentDaIds.Add(vi.DerivedArtifactId);
                            result = "OK";
                            detail = $"sha1={actual}";
                            log?.AppendLine($"OK       {vi.FileName}  sha1={actual}");
                        }
                        else
                        {
                            mismatchCount++;
                            badDaIds.Add(vi.DerivedArtifactId);
                            result = "MISMATCH";
                            detail = $"expected={vi.Sha1}  actual={actual}";
                            log?.AppendLine($"MISMATCH {vi.FileName}  expected={vi.Sha1}  actual={actual}");
                        }
                    }
                    else
                    {
                        okCount++;
                        presentDaIds.Add(vi.DerivedArtifactId);
                        result = "OK";
                        detail = "present (no hash)";
                        log?.AppendLine($"OK       {vi.FileName}  (no hash recorded)");
                    }

                    processed++;
                    int snap_ok = okCount, snap_miss = missingCount, snap_mismatch = mismatchCount;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        dlg.AppendRow(entry.Label, result, vi.FileName, detail);
                        dlg.UpdateStats(1, 0, 0, verifyInfos.Count, snap_ok, snap_miss, snap_mismatch);
                        dlg.SetStatus($"Verifying {processed}/{verifyInfos.Count}...");
                    });
                }
            });
        }
        catch (Exception ex) { errorMessage = ex.Message; }

        // ── Apply state updates (Verify is authoritative) ─────────────────
        if (errorMessage is null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dlg.SetStatus("Applying state updates..."));

            bool allPresent = badDaIds.Count == 0 && presentDaIds.Count == verifyInfos.Count;
            string newHealth = allPresent ? "ok" : "crit";

            // Update artifact statuses to match filesystem truth.
            if (presentDaIds.Count > 0)
                store.BatchUpdateDerivedArtifactStatus(presentDaIds, "present");
            if (badDaIds.Count > 0)
                store.BatchUpdateDerivedArtifactStatus(badDaIds, "missing");

            var allChanged = new List<string>(presentDaIds.Count + badDaIds.Count);
            allChanged.AddRange(presentDaIds);
            allChanged.AddRange(badDaIds);
            if (allChanged.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(allChanged);

            log?.AppendLine();
            log?.AppendLine($"State updates: present={presentDaIds.Count}  bad={badDaIds.Count}");

            // LOST → restore if every artifact is verified present.
            if (wasLost && allPresent)
            {
                _catalog.UpdateVolumeStatus(entry.Id, "present");
                _catalog.UpdateVolumeHealth(entry.Id, "ok");

                var wsRoot       = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));
                bool isWorkspace = volumeRoot.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase);
                _catalog.SetCurrentLocation(new Data.VolumeLocationRecord
                {
                    Id           = Guid.NewGuid().ToString("N"),
                    VolumeId     = entry.Id,
                    LocationType = isWorkspace ? "workspace" : "disk",
                    DiskId       = isWorkspace ? null : entry.DiskId,
                    Path         = volumeRoot,
                    IsCurrent    = true,
                    CreatedAt    = DateTime.UtcNow,
                });
                log?.AppendLine("Volume RESTORED: lost -> present");
            }
            else
            {
                _catalog.UpdateVolumeHealth(entry.Id, newHealth);
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                RebuildLibraryDatasets();
                RefreshVolumes();
                RefreshAnalyticsIfBuilt();
                RefreshDiskDetailIfSelected();
            });
        }

        // ── Write log ─────────────────────────────────────────────────────
        if (log is not null && _catalog.GetBoolSetting("auto_export_verify_logs", defaultValue: true))
        {
            var endTime = DateTime.UtcNow;
            log.AppendLine();
            log.AppendLine($"Summary:   OK={okCount}  MISSING={missingCount}  MISMATCH={mismatchCount}");
            log.AppendLine($"Completed: {endTime:o}");
            log.AppendLine($"Duration:  {(endTime - startTime).TotalSeconds:F1}s");
            if (errorMessage is not null)
                log.AppendLine($"Error:     {errorMessage}");
            try
            {
                var logDir  = Path.Combine(appRoot, "logs", "volume-verify");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir,
                    $"{startTime:yyyyMMdd-HHmmss}-volume-verify-{volSlug}.log");
                File.WriteAllText(logFile, log.ToString());
            }
            catch { /* non-fatal */ }
        }

        if (errorMessage is not null)
            dlg.SetFailed(errorMessage);
        else
        {
            dlg.UpdateStats(1, 1, 0, verifyInfos.Count, okCount, missingCount, mismatchCount);
            bool allPresent = badDaIds.Count == 0;
            string statusLine = wasLost && allPresent
                ? "Volume RESTORED from LOST."
                : wasLost
                    ? $"Volume remains LOST ({missingCount + mismatchCount} artifact(s) not valid)."
                    : allPresent ? "All artifacts verified — volume is healthy."
                                 : $"Volume health updated to CRIT ({missingCount + mismatchCount} issue(s)).";
            dlg.SetCompleted(
                $"OK: {okCount}   Missing: {missingCount}   Mismatch: {mismatchCount}\n{statusLine}");
        }

        await dlgTask;
    }

    private async void OnRepairVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as Volumes.VolumeEntry;
        if (entry is null || entry.StatusLabel is "LOCAL" or "ON DISK") return;
        if (entry.DbPath.Length == 0 || !File.Exists(entry.DbPath)) return;

        var appRoot      = AppContext.BaseDirectory;
        var platformId   = entry.PlatformId;
        var rawDatLineId = entry.RawDatLineId;

        // ── Resolve volume root ────────────────────────────────────────────
        string? volumeRoot = ResolveVolumeRoot(entry, appRoot);
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

        // ── Scan incoming-repair/<platform>/ for derived SHA1 matches ─────
        // Only scan for targets that are not already available locally.
        // incoming-repair is the dedicated drop zone for repair content.
        // Files matched here are deleted after successful copy+verify.
        var sha1ToTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vi in repairTargets)
        {
            if (preAvailable.ContainsKey(vi.DerivedArtifactId)) continue;
            if (vi.Sha1.Length > 0)
                sha1ToTarget[vi.Sha1] = vi.DerivedArtifactId;
        }

        var repairIncomingDir = Path.Combine(appRoot, "incoming-repair", platformId);
        Directory.CreateDirectory(repairIncomingDir);

        var incomingMatches = new Dictionary<string, string>(StringComparer.Ordinal); // daId → file path
        if (sha1ToTarget.Count > 0 && Directory.Exists(repairIncomingDir))
        {
            foreach (var f in Directory.EnumerateFiles(repairIncomingDir, "*", SearchOption.AllDirectories))
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

        // ── Pass B: Source SHA1 matching for Tier A targets ───────────────
        // For targets not yet resolved by preAvailable or Pass A (direct derived match),
        // check whether incoming-repair holds source files whose SHA1 matches a Tier A
        // content identity. These can be rebuilt via the normal ingestion pipeline.
        var sourceSha1sForTierA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int passBCount = 0;
        {
            var unresolvedForPassB = repairTargets
                .Where(vi => !preAvailable.ContainsKey(vi.DerivedArtifactId)
                          && !incomingMatches.ContainsKey(vi.DerivedArtifactId))
                .Select(vi => vi.DerivedArtifactId)
                .ToList();

            if (unresolvedForPassB.Count > 0)
            {
                var derivedMap = store.GetDerivedArtifacts()
                    .ToDictionary(da => da.Id, StringComparer.Ordinal);

                var tierAContentKeys = unresolvedForPassB
                    .Where(id => derivedMap.TryGetValue(id, out var da) && da.ArchiveTier == "A")
                    .Select(id => derivedMap[id].ContentIdentityKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (tierAContentKeys.Count > 0)
                {
                    sourceSha1sForTierA = store.GetSourceSha1sForContentKeys(tierAContentKeys);

                    if (sourceSha1sForTierA.Count > 0 && Directory.Exists(repairIncomingDir))
                    {
                        var alreadyMatchedPaths = new HashSet<string>(
                            incomingMatches.Values, StringComparer.OrdinalIgnoreCase);

                        foreach (var f in Directory.EnumerateFiles(
                            repairIncomingDir, "*", SearchOption.AllDirectories))
                        {
                            if (alreadyMatchedPaths.Contains(f)) continue;
                            try
                            {
                                if (sourceSha1sForTierA.Contains(ComputeFileSha1(f)))
                                    passBCount++;
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        // ── Preview counts ─────────────────────────────────────────────────
        int totalTargets       = repairTargets.Count;
        int preAvailableCount  = preAvailable.Count;
        int incomingCount      = incomingMatches.Count;
        int recoverableNow     = preAvailableCount + incomingCount + passBCount;
        int stillMissing       = totalTargets - recoverableNow;

        if (recoverableNow == 0)
        {
            await new InfoDialog("Nothing Recoverable",
                $"Volume \"{entry.Label}\" has {totalTargets} repair target(s), " +
                "but no matching files were found in the archive, source, or incoming-repair.\n\n" +
                $"Place the missing files in:\n  incoming-repair/{platformId}/\n\nThen try Repair again.")
                .ShowDialog(this);
            return;
        }

        // ── Preview confirmation ───────────────────────────────────────────
        var wsRootPreview = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));
        var locationType  = volumeRoot.StartsWith(wsRootPreview, StringComparison.OrdinalIgnoreCase)
            ? "Local Archive"
            : entry.DiskId is not null ? $"Disk ({entry.DiskId})" : "External";

        var previewLines = new System.Text.StringBuilder();
        previewLines.AppendLine($"  Volume:               {entry.Label}");
        previewLines.AppendLine($"  Status:               {entry.StatusLabel}");
        previewLines.AppendLine($"  Location:             {locationType}");
        previewLines.AppendLine();
        previewLines.AppendLine("  Missing/Invalid:        " + totalTargets);
        previewLines.AppendLine("  Already in archive:     " + preAvailableCount);
        previewLines.AppendLine("  Found in incoming (Pass A): " + incomingCount);
        if (passBCount > 0)
            previewLines.AppendLine("  Source rebuild (Pass B, Tier A): " + passBCount);
        previewLines.AppendLine("  Recoverable now:        " + recoverableNow);
        previewLines.AppendLine("  Still unrecoverable:    " + stillMissing);
        if (stillMissing > 0)
        {
            previewLines.AppendLine();
            previewLines.AppendLine($"  {stillMissing} file(s) cannot be recovered in this pass.");
            previewLines.AppendLine($"  Add them to incoming-repair/{platformId}/ and run Repair again.");
        }
        previewLines.AppendLine();
        previewLines.Append("Cancel to abort - no changes will be made.");

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
            hdr.Text = $"Volume Repair  —  System: {platformId}  —  Volume: {entry.Label}";

        var dlgTask = repairDialog.ShowDialog(this);

        await System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await RunVolumeRepairAsync(repairDialog, entry, store, volumeRoot,
                    platformId, rawDatLineId, storageStrategyId,
                    repairTargets, preAvailable, incomingMatches, neededSha1s,
                    sourceSha1sForTierA);
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
        Dictionary<string, string>         preAvailable,        // daId → archive or source path
        Dictionary<string, string>         incomingMatches,     // daId → incoming file path (Pass A)
        HashSet<string>                    neededSha1s,         // derived SHA1s of all repair targets
        HashSet<string>                    sourceSha1sForTierA) // Pass B: source SHA1s for Tier A rebuild
    {
        var appRoot        = AppContext.BaseDirectory;
        bool exportRepairLog = _catalog.GetBoolSetting("auto_export_repair_logs", defaultValue: true);
        var log            = new System.Text.StringBuilder();

        log.AppendLine($"Volume Repair — {entry.Label}");
        log.AppendLine($"Started:   {DateTime.UtcNow:o}");
        log.AppendLine($"System:    {platformId}");
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
        // Pass A: derived files in incoming-repair are ingested when their SHA1 matches
        //   a known derived SHA1 (neededSha1s). For Tier A no_compression artifacts the
        //   derived SHA1 equals the source SHA1, so they also satisfy the sha1Index match
        //   and are fully processed. Tier B/C derived files do not match the sha1Index and
        //   are handled by the direct reintegration phase instead.
        // Pass B: source files whose SHA1 matches a Tier A content identity
        //   (sourceSha1sForTierA) are ingested through the normal transform pipeline to
        //   rebuild the derived artifact. Only Tier A targets are eligible.
        // Both passes share a single RunIngestionWork call with a combined shouldIngest
        // predicate so that Pass B source files are not prematurely moved to incoming-skip
        // by the Phase 8 unmatched-file handler.
        var repairIncomingDir = Path.Combine(appRoot, "incoming-repair", platformId);
        bool hasIngestWork = incomingMatches.Count > 0 || sourceSha1sForTierA.Count > 0;
        if (hasIngestWork)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                dialog.SetStatus($"Ingesting matched content from incoming-repair/{platformId}...");
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

            // Combined filter: Pass A accepts derived SHA1 matches (neededSha1s);
            // Pass B additionally accepts source SHA1s for Tier A rebuild targets.
            // Files matching neither filter are moved to incoming-skip by ingestion Phase 8.
            var repairFileHandling = _catalog.LoadDatLines()
                .FirstOrDefault(dl => dl.Id == rawDatLineId)?.FileHandling ?? "archives_pre_extraction";
            var ingestResult = RunIngestionWork(
                platformId, rawDatLineId, entry.DbPath, storageStrategyId, ingestProgress,
                shouldIngest: sha1 => neededSha1s.Contains(sha1) || sourceSha1sForTierA.Contains(sha1),
                incomingDirOverride: repairIncomingDir,
                fileHandling: repairFileHandling);

            log.AppendLine("── Ingest Summary (Pass A + Pass B) ─────────────────────────");
            log.AppendLine($"  Scanned:              {ingestResult.FilesScanned}");
            log.AppendLine($"  Matched:              {ingestResult.FilesMatched}");
            log.AppendLine($"  Releases ingested:    {ingestResult.ReleasesPresent}");
            log.AppendLine($"  Pass A (derived SHA1s):  {neededSha1s.Count} targets");
            log.AppendLine($"  Pass B (source SHA1s):   {sourceSha1sForTierA.Count} Tier A SHA1s eligible");
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

            // Delete from incoming-repair after successful copy+verify.
            // Files from archive/source are permanent stores — never deleted.
            if (incomingMatches.TryGetValue(vi.DerivedArtifactId, out var incomingSrc))
            {
                try
                {
                    File.Delete(incomingSrc);
                    log.AppendLine($"  DELETED incoming-repair source: {Path.GetFileName(incomingSrc)}");
                }
                catch (Exception delEx)
                {
                    log.AppendLine($"  DELETE FAILED: {Path.GetFileName(incomingSrc)}  ({delEx.Message})");
                }
            }
        }

        // ── STATE UPDATES ──────────────────────────────────────────────────
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            dialog.SetStatus("Applying state updates…"));

        var missedDaIds     = new List<string>(skippedDaIds.Count + failedDaIds.Count);
        missedDaIds.AddRange(skippedDaIds);
        missedDaIds.AddRange(failedDaIds);

        int  remainingIssues = missedDaIds.Count;
        bool wasLost         = entry.Status == "lost";
        bool fullSuccess     = remainingIssues == 0;

        int    artPresent = 0, artMissing = 0, relUpdated = 0;
        string newHealth  = "crit";

        if (wasLost && !fullSuccess)
        {
            // CASE B: LOST volume, partial repair — volume stays LOST.
            // Invariant: artifacts cannot be "present" on a lost volume.
            // Leave all artifact and release status unchanged.
        }
        else
        {
            // CASE A: full repair (any origin) OR non-lost partial repair.
            // Volume is present or about to be restored — artifact updates are valid.
            artPresent = store.BatchUpdateDerivedArtifactStatus(verifiedDaIds, "present");
            artMissing = store.BatchUpdateDerivedArtifactStatus(missedDaIds,   "missing");

            var allChanged = new List<string>(verifiedDaIds.Count + missedDaIds.Count);
            allChanged.AddRange(verifiedDaIds);
            allChanged.AddRange(missedDaIds);
            relUpdated = store.RecalculateReleaseStatusForArtifacts(allChanged);

            newHealth = fullSuccess ? "ok" : "crit";
        }

        // Restore LOST volume status BEFORE library rebuild so RefreshVolumes picks it up.
        if (wasLost && fullSuccess)
        {
            _catalog.UpdateVolumeStatus(entry.Id, "present");
            _catalog.UpdateVolumeHealth(entry.Id, "ok");

            var wsRoot       = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));
            bool isWorkspace = volumeRoot.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase);
            _catalog.SetCurrentLocation(new Data.VolumeLocationRecord
            {
                Id           = Guid.NewGuid().ToString("N"),
                VolumeId     = entry.Id,
                LocationType = isWorkspace ? "workspace" : "disk",
                DiskId       = isWorkspace ? null : entry.DiskId,
                Path         = volumeRoot,
                IsCurrent    = true,
                CreatedAt    = DateTime.UtcNow,
            });
        }
        else
        {
            _catalog.UpdateVolumeHealth(entry.Id, newHealth);
        }

        log.AppendLine();
        log.AppendLine("── Apply Summary ────────────────────────────────────────────");
        log.AppendLine($"  Artifacts -> present:   {artPresent}");
        log.AppendLine($"  Artifacts -> missing:   {artMissing}");
        log.AppendLine($"  Releases recalculated:  {relUpdated}");
        if (wasLost && !fullSuccess)
            log.AppendLine("  Note: artifact status unchanged — volume remains LOST (invariant preserved).");
        log.AppendLine();
        log.AppendLine("── Final Volume Status ──────────────────────────────────────");
        log.AppendLine($"  Volume:  {entry.Label}");
        log.AppendLine($"  Status:  {(wasLost && fullSuccess ? "RESTORED (lost -> present)" : wasLost ? "LOST (unchanged)" : "present")}");
        log.AppendLine($"  Health:  {newHealth.ToUpper()}");
        if (wasLost && fullSuccess)
            log.AppendLine("  Result:  Repair complete -- volume restored from LOST. All targets recovered.");
        else if (wasLost)
            log.AppendLine($"  Result:  Repair incomplete -- volume remains LOST. {remainingIssues} target(s) still unrecoverable.");
        else if (fullSuccess)
            log.AppendLine("  Result:  Repair complete -- all targets recovered and verified.");
        else
            log.AppendLine($"  Result:  Repair incomplete -- {remainingIssues} target(s) still missing or invalid.");
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
            RefreshAnalyticsIfBuilt();
            RefreshDiskDetailIfSelected();
            dialog.UpdateStats(
                totalTargets, verifiedDaIds.Count, skippedDaIds.Count,
                totalTargets, verifiedDaIds.Count, missedDaIds.Count);
        });

        // ── FINAL STATUS ───────────────────────────────────────────────────
        string summary;
        if (wasLost && fullSuccess)
        {
            summary =
                $"Volume Repair complete - {entry.Label}\n" +
                $"Volume restored:       LOST -> present\n" +
                $"Targets requested:     {totalTargets}\n" +
                $"Files reintegrated:    {reintegratedDaIds.Count}\n" +
                $"Files verified:        {verifiedDaIds.Count}\n" +
                $"Artifacts -> present:  {artPresent}  |  Releases recalculated: {relUpdated}\n" +
                $"Volume status:         RESTORED - Volume is now present.";
        }
        else if (wasLost)
        {
            summary =
                $"Volume Repair incomplete - {entry.Label}\n" +
                $"Volume status:         Still LOST ({remainingIssues} target(s) unrecoverable)\n" +
                $"Targets requested:     {totalTargets}\n" +
                $"Files verified:        {verifiedDaIds.Count}\n" +
                $"Artifact status:       Unchanged - cannot be present on a lost volume.\n" +
                $"Add missing files to incoming-roms/{platformId}/ and run Repair again.";
        }
        else if (fullSuccess)
        {
            summary =
                $"Volume Repair complete - {entry.Label}\n" +
                $"Targets requested:     {totalTargets}\n" +
                $"Files reintegrated:    {reintegratedDaIds.Count}\n" +
                $"Files verified:        {verifiedDaIds.Count}\n" +
                $"Artifacts -> present:  {artPresent}  |  Releases recalculated: {relUpdated}\n" +
                $"Volume health:         OK - Volume is now healthy.";
        }
        else
        {
            summary =
                $"Volume Repair complete (partial) - {entry.Label}\n" +
                $"Targets requested:     {totalTargets}\n" +
                $"Recovered from incoming: {incomingMatches.Count}\n" +
                $"Derived available:     {availableCount} of {totalTargets}\n" +
                $"Files reintegrated:    {reintegratedDaIds.Count}\n" +
                $"Files verified:        {verifiedDaIds.Count}\n" +
                $"Artifacts -> present:  {artPresent}  |  Still missing: {artMissing}  |  Releases recalculated: {relUpdated}\n" +
                $"Volume health:         CRIT - {remainingIssues} target(s) still missing or invalid.";
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            string statusText = wasLost && fullSuccess ? "RESTORED"
                              : wasLost               ? "LOST"
                              :                         newHealth.ToUpper();
            dialog.SetStatus(
                $"Done  Targets: {totalTargets}  Reintegrated: {reintegratedDaIds.Count}  " +
                $"Verified: {verifiedDaIds.Count}  Remaining: {remainingIssues}  " +
                $"Status: {statusText}");
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

        // Promote init → present after successful materialization
        if (entry.Status == "init")
            _catalog.UpdateVolumeStatus(entry.Id, "present");

        // Realign DA + release state for artifacts just moved out of archive
        var movedDaIds = notBuilt.Select(x => x.Info.DerivedArtifactId).ToList();
        if (movedDaIds.Count > 0)
        {
            store.BatchUpdateDerivedArtifactStatus(movedDaIds, "present");
            store.RecalculateReleaseStatusForArtifacts(movedDaIds);
        }

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
                $"Plan Volume — {entry.Label}";
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
                "Plan Volume — Complete",
                $"Volume:         {entry.Label}\n" +
                $"Releases:       {releaseCount}\n" +
                $"Artifacts:      {linkedCount}\n" +
                buildNote + "\n" +
                $"Destination:    {volumeFolder}")
                .ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Plan Volume Error", ex.Message).ShowDialog(this);
        }
    }

    private async void OnMoveVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;
        if (entry.StatusLabel is not ("LOCAL" or "ON DISK")) return;

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
            RebuildLibraryDatasets();
            RefreshAnalyticsIfBuilt();
            RefreshDiskDetailIfSelected();

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

    // ── Volume Resize ─────────────────────────────────────────────────────────

    private async void OnResizeVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;
        if (entry.Status == "lost") return;

        var dlg       = new ResizeVolumeDialog(entry.Label, entry.PlannedSizeBytes,
                            entry.ActualSizeBytes, entry.RawDatLineId, entry.DbPath, _catalog);
        var confirmed = await dlg.ShowDialog<bool>(this);
        if (!confirmed) return;

        var newBytes = dlg.ResultBytes;

        // Rule A — occupied-size lower bound (always)
        if (newBytes < entry.ActualSizeBytes)
        {
            var diff = entry.ActualSizeBytes - newBytes;
            await new InfoDialog(
                "Cannot Resize Volume",
                "The requested size is smaller than the space already occupied by artifacts in this volume.\n\n" +
                $"Requested size:             {FormatBytes(newBytes)}\n" +
                $"Current occupied size:      {FormatBytes(entry.ActualSizeBytes)}\n" +
                $"Additional capacity required: {FormatBytes(diff)}")
                .ShowDialog(this);
            return;
        }

        // Rule B — disk capacity upper bound (disk-backed volumes only)
        if (entry.DiskId is not null)
        {
            var disk = _catalog.GetDisks().FirstOrDefault(d => d.Id == entry.DiskId);
            if (disk is not null)
            {
                var otherPlanned      = _catalog.GetDiskPlannedUsageExcluding(disk.Id, entry.Id);
                var availableForThis  = disk.DeclaredCapacityBytes - otherPlanned;
                if (newBytes > availableForThis)
                {
                    var diff = newBytes - availableForThis;
                    await new InfoDialog(
                        "Cannot Resize Volume",
                        "The requested size cannot be applied because the target disk does not have enough allocatable capacity.\n\n" +
                        $"Requested size:          {FormatBytes(newBytes)}\n" +
                        $"Available for this volume: {FormatBytes(Math.Max(0, availableForThis))}\n" +
                        $"Additional space needed: {FormatBytes(diff)}\n\n" +
                        "This disk already has other planned volumes assigned to it.")
                        .ShowDialog(this);
                    return;
                }
            }
        }

        var vol = _catalog.GetVolumes().FirstOrDefault(v => v.Id == entry.Id);
        if (vol is null) return;

        _catalog.SaveVolume(new Data.VolumeRecord
        {
            Id               = vol.Id,
            Label            = vol.Label,
            PlatformId       = vol.PlatformId,
            DatLineId        = vol.DatLineId,
            Status           = vol.Status,
            Health           = vol.Health,
            PlannedSizeBytes = newBytes,
            ActualSizeBytes  = vol.ActualSizeBytes,
            CreatedAt        = vol.CreatedAt,
            VerifiedAt       = vol.VerifiedAt,
        });

        try
        {
            var logDir  = Path.Combine(AppContext.BaseDirectory, "logs", "volume-resize");
            Directory.CreateDirectory(logDir);
            var logTs   = DateTime.Now;
            var logFile = Path.Combine(logDir,
                $"{logTs:yyyyMMdd-HHmmss}-volume-resize-{SafeFileName(entry.Label)}.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Volume Resize");
            sb.AppendLine();
            sb.AppendLine($"Volume:    {entry.Label}");
            sb.AppendLine($"Previous:  {FormatBytes(entry.PlannedSizeBytes)}");
            sb.AppendLine($"New:       {FormatBytes(newBytes)}");
            sb.AppendLine($"Timestamp: {logTs:o}");
            File.WriteAllText(logFile, sb.ToString());
        }
        catch { /* non-fatal */ }

        RefreshVolumes();
        RefreshAnalyticsIfBuilt();
        RefreshDiskDetailIfSelected();
        var updatedResize = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updatedResize is not null)
        {
            VolumesList.SelectedItem = updatedResize;
            UpdateVolumeDetailPanel(updatedResize);
        }
    }

    // ── Volume Append ─────────────────────────────────────────────────────────

    private async void OnAppendVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;

        if (entry.Status == "lost")
        {
            await new InfoDialog("Volume Lost",
                $"Volume \"{entry.Label}\" is marked as lost and cannot be appended to.")
                .ShowDialog(this);
            return;
        }

        if (entry.DbPath.Length == 0 || !File.Exists(entry.DbPath)) return;

        var appRoot = AppContext.BaseDirectory;

        // ── Resolve volume root ────────────────────────────────────────────
        var workspaceRoot = Path.Combine(appRoot, "volumes", SafeFileName(entry.Label));
        string? volumeRoot = null;

        if (Directory.Exists(workspaceRoot))
        {
            volumeRoot = workspaceRoot;
        }
        else if (entry.DiskId is not null)
        {
            var rtDisks = Data.DiskDiscoveryService.DiscoverAll();
            var disk    = rtDisks.FirstOrDefault(d =>
                d.HasMarker &&
                string.Equals(d.DiskId, entry.DiskId, StringComparison.Ordinal));
            if (disk is not null)
            {
                var diskRoot = Path.Combine(disk.Mountpoint, SafeFileName(entry.Label));
                if (Directory.Exists(diskRoot))
                    volumeRoot = diskRoot;
            }
        }

        if (volumeRoot is null)
        {
            await new InfoDialog("Volume Not Accessible",
                $"Volume \"{entry.Label}\" could not be found in the workspace or on a mounted disk.\n\n" +
                "Mount the disk containing this volume, then try Append again.")
                .ShowDialog(this);
            return;
        }

        // ── Find artifacts to append ───────────────────────────────────────
        var store       = new DatLineStore(entry.DbPath);
        var assignments = _catalog.GetVolumeArtifacts(entry.Id);
        if (assignments.Count == 0) return;

        var daIds      = assignments.Select(va => va.DerivedArtifactId).ToList();
        var buildInfos = store.GetArtifactBuildInfos(daIds);

        var toAppend = buildInfos
            .Select(info =>
            {
                var src = Path.Combine(appRoot,
                    info.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                // Flat layout: artifacts written directly into volume root, no release sub-folder.
                var dst = Path.Combine(volumeRoot, info.FileName);
                return (Info: info, Src: src, Dst: dst);
            })
            .Where(x => File.Exists(x.Src) && !File.Exists(x.Dst))
            .ToList();

        if (toAppend.Count == 0)
        {
            await new InfoDialog("Nothing to Append",
                $"No new archive files found to append to volume \"{entry.Label}\".\n\n" +
                "Ingest new content for this DAT line first.")
                .ShowDialog(this);
            return;
        }

        long totalBytes = toAppend.Sum(x => x.Info.SizeBytes);

        // ── Free space check ───────────────────────────────────────────────
        try
        {
            var dstDrive = new DriveInfo(Path.GetPathRoot(volumeRoot)!);
            if (totalBytes > dstDrive.AvailableFreeSpace)
            {
                await new InfoDialog("Insufficient Space",
                    $"Required: {FormatBytes(totalBytes)}\n" +
                    $"Available: {FormatBytes(dstDrive.AvailableFreeSpace)}\n\n" +
                    "Free up space on the volume disk and try again.")
                    .ShowDialog(this);
                return;
            }
        }
        catch { /* non-fatal */ }

        // ── Confirm ────────────────────────────────────────────────────────
        var appendMsg =
            $"Volume:       {entry.Label}\n" +
            $"Files:        {toAppend.Count}\n" +
            $"Size:         {FormatBytes(totalBytes)}\n" +
            $"Destination:  {volumeRoot}\n\n" +
            "Files will be copied to the volume, verified, then removed from the local archive.";

        var appendConfirmed = await new ConfirmDialog("Append to Volume", appendMsg)
            .ShowDialog<bool>(this);
        if (!appendConfirmed) return;

        // ── Run copy → verify → delete on background thread ───────────────
        bool logEnabled = _catalog.GetBoolSetting("log_on_copy", true);
        System.Text.StringBuilder? log = logEnabled ? new System.Text.StringBuilder() : null;
        var startTime = DateTime.UtcNow;
        var volSlug   = SafeFileName(entry.Label);

        if (log is not null)
        {
            log.AppendLine("Volume Append");
            log.AppendLine($"Started:     {startTime:o}");
            log.AppendLine($"Volume:      {entry.Label}");
            log.AppendLine($"Destination: {volumeRoot}");
            log.AppendLine($"Files:       {toAppend.Count}");
            log.AppendLine($"Bytes:       {totalBytes}");
            log.AppendLine();
        }

        var appendHeader = $"Append to Volume  —  {entry.Label}  —  {toAppend.Count} file(s)";
        var progDialog   = new WriteVolumeToDiskDialog(appendHeader, totalBytes, toAppend.Count);
        var dlgTask      = progDialog.ShowDialog<bool>(this);

        string? abortReason = null;
        var succeededSrcs   = new List<string>();
        long copiedBytes = 0, verifiedBytes = 0;
        int  filesProcessed = 0;

        try
        {
            await Task.Run(async () =>
            {
                foreach (var (info, src, dst) in toAppend)
                {
                    var sizeLabel = FormatBytes(info.SizeBytes);

                    // ── Copy ───────────────────────────────────────────────
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    log?.AppendLine($"COPY-START  {info.FileName}  ({sizeLabel})");
                    File.Copy(src, dst, overwrite: false);
                    copiedBytes += info.SizeBytes;
                    filesProcessed++;
                    var elapsed = DateTime.UtcNow - startTime;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progDialog.AppendRow("copy", info.FileName, sizeLabel);
                        progDialog.UpdateStats(copiedBytes, verifiedBytes, filesProcessed, elapsed);
                    });

                    // ── Verify destination against DB expected hash ────────
                    log?.AppendLine($"COPY-OK  {info.FileName}");

                    var verifyOk = AppendVerifier.VerifyDestination(
                        dst, info.SizeBytes, info.ExpectedSha1,
                        out var failReason, out var verifyLog);
                    log?.Append(verifyLog);

                    if (!verifyOk)
                    {
                        // Remove bad destination so retry finds a clean state
                        try { File.Delete(dst); }
                        catch (Exception ex)
                        {
                            log?.AppendLine($"DELETE-FAILED-DESTINATION  {dst}  {ex.Message}");
                        }
                        abortReason = $"Verify failed — {failReason}";
                        log?.AppendLine($"APPEND-ABORTED  {abortReason}");
                        break;
                    }

                    // Verified — track this source for archive deletion
                    succeededSrcs.Add(src);
                    verifiedBytes += info.SizeBytes;
                    elapsed = DateTime.UtcNow - startTime;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progDialog.AppendRow("verify", info.FileName, sizeLabel);
                        progDialog.UpdateStats(copiedBytes, verifiedBytes, filesProcessed, elapsed);
                    });
                }
            });
        }
        catch (Exception ex) { abortReason ??= ex.Message; }

        // ── Delete from archive — only successfully verified sources ───────
        if (abortReason is null)
        {
            log?.AppendLine();
            log?.AppendLine("Delete from archive:");
            foreach (var src in succeededSrcs)
            {
                try
                {
                    File.Delete(src);
                    log?.AppendLine($"DELETE-OK  {src}");
                    var dir = Path.GetDirectoryName(src)!;
                    if (Directory.Exists(dir) &&
                        !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception ex)
                {
                    log?.AppendLine($"DELETE-FAIL  {src}  {ex.Message}");
                }
            }
            log?.AppendLine("APPEND-COMPLETE");

            // Realign DA + release state immediately after archive files removed
            var appendedDaIds = toAppend.Select(x => x.Info.DerivedArtifactId).ToList();
            store.BatchUpdateDerivedArtifactStatus(appendedDaIds, "present");
            store.RecalculateReleaseStatusForArtifacts(appendedDaIds);
        }

        // ── Write log ──────────────────────────────────────────────────────
        if (log is not null)
        {
            var endTime = DateTime.UtcNow;
            log.AppendLine();
            log.AppendLine($"Completed:   {endTime:o}");
            log.AppendLine($"Duration:    {(endTime - startTime).TotalSeconds:F1}s");
            log.AppendLine($"Result:      {(abortReason is null ? "OK" : "FAILED")}");
            if (abortReason is not null)
                log.AppendLine($"Error:       {abortReason}");
            try
            {
                var logDir  = Path.Combine(appRoot, "logs", "volume-append");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir,
                    $"{startTime:yyyyMMdd-HHmmss}-volume-append-{volSlug}.log");
                File.WriteAllText(logFile, log.ToString());
            }
            catch { /* non-fatal */ }
        }

        if (abortReason is null)
            progDialog.SetCompleted(succeededSrcs.Count, copiedBytes, volumeRoot,
                $"Completed — {succeededSrcs.Count} file(s) copied, verified, and removed from archive.");
        else
            progDialog.SetFailed(abortReason);

        await dlgTask;

        if (abortReason is not null) return;

        RefreshVolumes();
        RefreshAnalyticsIfBuilt();
        RefreshDiskDetailIfSelected();
        var updatedAppend = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updatedAppend is not null)
        {
            VolumesList.SelectedItem = updatedAppend;
            UpdateVolumeDetailPanel(updatedAppend);
        }
    }

    // ── Volume Reabsorb ───────────────────────────────────────────────────────

    private async void OnReabsorbVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;
        if (entry.StatusLabel is not ("LOCAL" or "ON DISK")) return;
        if (entry.DbPath.Length == 0 || !File.Exists(entry.DbPath)) return;

        var appRoot = AppContext.BaseDirectory;

        // ── STEP 1A: Resolve volume root ───────────────────────────────────
        var volumeRoot = ResolveVolumeRoot(entry, appRoot);
        if (volumeRoot is null)
        {
            await new InfoDialog("Volume Not Accessible",
                $"Volume \"{entry.Label}\" could not be found in the workspace or on a mounted disk.\n\n" +
                "Mount the disk containing this volume, then try Reabsorb again.")
                .ShowDialog(this);
            return;
        }

        // ── STEP 1B: Load assignments — filter LOST ────────────────────────
        var store           = new DatLineStore(entry.DbPath);
        var assignments     = _catalog.GetVolumeArtifacts(entry.Id);
        if (assignments.Count == 0) return;

        var lostAssignments   = assignments.Where(va => va.Status == "lost").ToList();
        var activeAssignments = assignments.Where(va => va.Status != "lost").ToList();

        var daIds      = activeAssignments.Select(va => va.DerivedArtifactId).ToList();
        var buildInfos = store.GetArtifactBuildInfos(daIds);
        var infoById   = buildInfos.ToDictionary(b => b.DerivedArtifactId, StringComparer.Ordinal);

        // Build candidate list: only artifacts physically present on the volume
        var candidates = activeAssignments
            .Where(va => infoById.ContainsKey(va.DerivedArtifactId))
            .Select(va =>
            {
                var info = infoById[va.DerivedArtifactId];
                var src  = Path.Combine(volumeRoot,
                    SafeFileName(info.ReleaseName), info.FileName);
                var dst  = Path.Combine(appRoot,
                    info.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                return (Info: info, Src: src, Dst: dst);
            })
            .Where(x => File.Exists(x.Src))
            .ToList();

        if (candidates.Count == 0)
        {
            await new InfoDialog("Nothing to Reabsorb",
                $"No files found on volume \"{entry.Label}\".\n\n" +
                "The volume may be empty or not yet built.")
                .ShowDialog(this);
            return;
        }

        // ── STEP 1C: Pre-categorize CASE A (already local) vs CASE B (copy needed)
        // CASE A: dst already exists in Local Archive — verify only, no copy
        // CASE B: dst absent — must copy from volume
        var caseACount = candidates.Count(x => File.Exists(x.Dst));
        var caseBList  = candidates.Where(x => !File.Exists(x.Dst)).ToList();
        long caseBBytes = caseBList.Sum(x => x.Info.SizeBytes);

        // ── STEP 1D: Free space check (CASE B bytes only) ──────────────────
        if (caseBBytes > 0)
        {
            try
            {
                var dstDrive = new DriveInfo(Path.GetPathRoot(appRoot)!);
                if (caseBBytes > dstDrive.AvailableFreeSpace)
                {
                    await new InfoDialog("Insufficient Space",
                        $"Required:  {FormatBytes(caseBBytes)}\n" +
                        $"Available: {FormatBytes(dstDrive.AvailableFreeSpace)}\n\n" +
                        "Free up space in the local archive and try again.")
                        .ShowDialog(this);
                    return;
                }
            }
            catch { /* non-fatal — copy will fail naturally if truly out of space */ }
        }

        // ── Confirm ────────────────────────────────────────────────────────
        var confirmMsg = new System.Text.StringBuilder();
        confirmMsg.AppendLine($"Volume:  {entry.Label}");
        confirmMsg.AppendLine($"Source:  {volumeRoot}");
        confirmMsg.AppendLine();
        if (caseBList.Count > 0)
            confirmMsg.AppendLine($"To copy:          {caseBList.Count} file(s)  ({FormatBytes(caseBBytes)})");
        if (caseACount > 0)
            confirmMsg.AppendLine($"Already local:    {caseACount} file(s)  (verify only)");
        if (lostAssignments.Count > 0)
            confirmMsg.AppendLine($"Skipped (LOST):   {lostAssignments.Count} file(s)");
        confirmMsg.AppendLine();
        confirmMsg.Append("Files will be verified and removed from the volume. " +
            "The volume record is deleted only if all artifacts are successfully reabsorbed.");

        var confirmed = await new ConfirmDialog("Reabsorb Volume", confirmMsg.ToString())
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        // ── Run per-artifact transfer on background thread ─────────────────
        bool logEnabled = _catalog.GetBoolSetting("log_on_copy", true);
        var  sb         = logEnabled ? new System.Text.StringBuilder() : null;
        var  startTime  = DateTime.UtcNow;
        var  volSlug    = SafeFileName(entry.Label);

        if (sb is not null)
        {
            sb.AppendLine("Volume Reabsorb");
            sb.AppendLine($"Started:         {startTime:o}");
            sb.AppendLine($"Volume:          {entry.Label}");
            sb.AppendLine($"Source:          {volumeRoot}");
            sb.AppendLine($"To copy:         {caseBList.Count}");
            sb.AppendLine($"Already local:   {caseACount}");
            sb.AppendLine($"Skipped (LOST):  {lostAssignments.Count}");
            sb.AppendLine();
            foreach (var va in lostAssignments)
                sb.AppendLine($"skipped-lost  {va.DerivedArtifactId}");
            if (lostAssignments.Count > 0) sb.AppendLine();
        }

        var header     = $"Reabsorb Volume  —  {entry.Label}  —  {candidates.Count} file(s)";
        var progDialog = new WriteVolumeToDiskDialog(header, caseBBytes, candidates.Count);
        var dlgTask    = progDialog.ShowDialog<bool>(this);

        string? abortReason    = null;
        bool    cleanupFailed  = false;
        long    copiedBytes    = 0, verifiedBytes = 0;
        int     filesProcessed = 0;
        var     recoveredDaIds = new List<string>();

        try
        {
            await Task.Run(async () =>
            {
                // ── Per-artifact: copy → verify → delete (strict per-file order) ──
                foreach (var (info, src, dst) in candidates)
                {
                    var sizeLabel = FormatBytes(info.SizeBytes);
                    bool dstExists = File.Exists(dst);

                    if (dstExists)
                    {
                        // ── CASE A: artifact already present in Local Archive ──
                        sb?.AppendLine($"already-present-local  {info.FileName}  ({sizeLabel})");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            progDialog.AppendRow("already-local", info.FileName, sizeLabel));

                        var sha1Dst = ComputeFileSha1(dst);
                        var sha1Src = ComputeFileSha1(src);

                        if (string.Equals(sha1Dst, sha1Src, StringComparison.OrdinalIgnoreCase))
                        {
                            // Local copy valid — mark recovered, then delete volume copy
                            sb?.AppendLine($"verify-ok  {info.FileName}  sha1={sha1Dst}");
                            recoveredDaIds.Add(info.DerivedArtifactId);
                            try
                            {
                                File.Delete(src);
                                var srcDir = Path.GetDirectoryName(src)!;
                                if (Directory.Exists(srcDir) &&
                                    !Directory.EnumerateFileSystemEntries(srcDir).Any())
                                    Directory.Delete(srcDir);
                                sb?.AppendLine($"delete-from-volume  {info.FileName}");
                            }
                            catch (Exception ex)
                            {
                                sb?.AppendLine($"delete-from-volume-failed  {info.FileName}  {ex.Message}");
                                cleanupFailed = true;
                                return;
                            }
                            verifiedBytes += info.SizeBytes;
                            filesProcessed++;
                            var el1 = DateTime.UtcNow - startTime;
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                progDialog.AppendRow("verify-ok", info.FileName, sizeLabel);
                                progDialog.UpdateStats(copiedBytes, verifiedBytes, filesProcessed, el1);
                            });
                            continue;
                        }

                        // Local copy invalid — fall through to CASE B (overwrite)
                        sb?.AppendLine($"local-invalid  {info.FileName}  " +
                            $"sha1-local={sha1Dst}  sha1-vol={sha1Src}  — re-copying");
                    }

                    // ── CASE B: copy from volume → Local Archive ───────────
                    sb?.AppendLine($"copy-start  {info.FileName}  ({sizeLabel})");
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        progDialog.AppendRow("copy-start", info.FileName, sizeLabel));

                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: true);
                    copiedBytes += info.SizeBytes;
                    filesProcessed++;
                    var elCopy = DateTime.UtcNow - startTime;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        progDialog.UpdateStats(copiedBytes, verifiedBytes, filesProcessed, elCopy));

                    // Verify copy
                    var sha1S = ComputeFileSha1(src);
                    var sha1D = ComputeFileSha1(dst);

                    if (!string.Equals(sha1S, sha1D, StringComparison.OrdinalIgnoreCase))
                    {
                        // Verification failed — abort, leave src intact
                        sb?.AppendLine($"verify-failed  {info.FileName}  " +
                            $"sha1-src={sha1S}  sha1-dst={sha1D}");
                        abortReason = $"SHA1 mismatch: {info.FileName}";
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            progDialog.AppendRow("verify-failed", info.FileName, sizeLabel));
                        return;
                    }

                    // Copy verified — mark recovered, then delete volume copy
                    sb?.AppendLine($"verify-ok  {info.FileName}  sha1={sha1D}");
                    verifiedBytes += info.SizeBytes;
                    recoveredDaIds.Add(info.DerivedArtifactId);
                    try
                    {
                        File.Delete(src);
                        var srcDirB = Path.GetDirectoryName(src)!;
                        if (Directory.Exists(srcDirB) &&
                            !Directory.EnumerateFileSystemEntries(srcDirB).Any())
                            Directory.Delete(srcDirB);
                        sb?.AppendLine($"delete-from-volume  {info.FileName}");
                    }
                    catch (Exception ex)
                    {
                        sb?.AppendLine($"delete-from-volume-failed  {info.FileName}  {ex.Message}");
                        cleanupFailed = true;
                        return;
                    }

                    var elVerify = DateTime.UtcNow - startTime;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progDialog.AppendRow("verify-ok", info.FileName, sizeLabel);
                        progDialog.UpdateStats(copiedBytes, verifiedBytes, filesProcessed, elVerify);
                    });
                }
            });
        }
        catch (Exception ex) { abortReason = ex.Message; }

        bool fullSuccess = abortReason is null && !cleanupFailed;

        // ── Write log ──────────────────────────────────────────────────────
        if (sb is not null)
        {
            var endTime = DateTime.UtcNow;
            sb.AppendLine();
            sb.AppendLine(
                fullSuccess   ? "reabsorb-complete" :
                cleanupFailed ? "reabsorb-cleanup-failed" :
                                $"reabsorb-aborted  {abortReason}");
            sb.AppendLine($"Completed:   {endTime:o}");
            sb.AppendLine($"Duration:    {(endTime - startTime).TotalSeconds:F1}s");
            sb.AppendLine($"Transferred: {recoveredDaIds.Count} / {candidates.Count}");
            try
            {
                var logDir  = Path.Combine(appRoot, "logs", "volume-reabsorb");
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir,
                    $"{startTime:yyyyMMdd-HHmmss}-volume-reabsorb-{volSlug}.log");
                File.WriteAllText(logFile, sb.ToString());
            }
            catch { /* non-fatal */ }
        }

        // ── DB updates — apply for all verified artifacts regardless of delete outcome ─
        if (recoveredDaIds.Count > 0)
        {
            store.BatchUpdateDerivedArtifactStatus(recoveredDaIds, "present");
            store.RecalculateReleaseStatusForArtifacts(recoveredDaIds);
        }

        if (fullSuccess)
        {
            // ── Full success: delete volume directory + DB record ──────────
            try
            {
                if (Directory.Exists(volumeRoot))
                {
                    foreach (var dir in Directory.GetDirectories(
                                 volumeRoot, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    }
                    if (!Directory.EnumerateFileSystemEntries(volumeRoot).Any())
                        Directory.Delete(volumeRoot);
                }
            }
            catch { /* non-fatal — dir cleanup failed; DB cleanup proceeds */ }

            _catalog.DeleteVolume(entry.Id);

            progDialog.SetCompleted(recoveredDaIds.Count, copiedBytes, appRoot,
                $"reabsorb-complete — {recoveredDaIds.Count} file(s) transferred. Volume deleted.");
        }
        else
        {
            // Partial: remove transferred artifact mappings from volume; retain volume record
            if (recoveredDaIds.Count > 0)
                _catalog.RemoveVolumeArtifacts(entry.Id, recoveredDaIds);

            var failReason = cleanupFailed
                ? "cleanup failure — source file(s) could not be deleted from volume"
                : abortReason ?? "unknown error";
            progDialog.SetFailed($"reabsorb-aborted — {failReason}\n" +
                $"{recoveredDaIds.Count}/{candidates.Count} file(s) transferred. Volume retained.");
        }

        await dlgTask;

        RebuildLibraryDatasets();
        RefreshVolumes();
        RefreshAnalyticsIfBuilt();
        RefreshDiskDetailIfSelected();

        var updated = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updated is not null)
        {
            VolumesList.SelectedItem = updated;
            UpdateVolumeDetailPanel(updated);
        }
        else
        {
            UpdateVolumeDetailPanel(null);
        }
    }

    // ── Mark Volume Lost ──────────────────────────────────────────────────────

    private async void OnMarkVolumeLost(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;

        if (entry.Status == "lost")
        {
            await new InfoDialog("Already Lost",
                $"Volume \"{entry.Label}\" is already marked as lost.")
                .ShowDialog(this);
            return;
        }

        var lostMsg =
            $"Volume:  {entry.Label}\n" +
            $"Status:  {entry.StatusLabel}\n\n" +
            "This will mark the volume as permanently lost. All catalog entries are preserved.\n\n" +
            "This action cannot be undone.";

        var lostConfirmed = await new ConfirmDialog("Mark Volume Lost", lostMsg)
            .ShowDialog<bool>(this);
        if (!lostConfirmed) return;

        var vol = _catalog.GetVolumes().FirstOrDefault(v => v.Id == entry.Id);
        if (vol is null) return;

        _catalog.SaveVolume(new Data.VolumeRecord
        {
            Id               = vol.Id,
            Label            = vol.Label,
            PlatformId       = vol.PlatformId,
            DatLineId        = vol.DatLineId,
            Status           = "lost",
            Health           = vol.Health,
            PlannedSizeBytes = vol.PlannedSizeBytes,
            ActualSizeBytes  = vol.ActualSizeBytes,
            CreatedAt        = vol.CreatedAt,
            VerifiedAt       = vol.VerifiedAt,
        });

        // Propagate loss to derived artifacts that are exclusively on this volume,
        // then recalculate release presence so the library reflects the loss.
        var (propagated, skippedDatLines) = PropagateVolumeLossToReleases(vol.Id);
        RebuildLibraryDatasets();

        try
        {
            var logDir  = Path.Combine(AppContext.BaseDirectory, "logs", "volume-lost");
            Directory.CreateDirectory(logDir);
            var logTs   = DateTime.Now;
            var logFile = Path.Combine(logDir,
                $"{logTs:yyyyMMdd-HHmmss}-volume-lost-{SafeFileName(entry.Label)}.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Volume Marked Lost");
            sb.AppendLine();
            sb.AppendLine($"Volume:    {entry.Label}");
            sb.AppendLine($"System:           {entry.PlatformId}");
            sb.AppendLine($"DAT Line:  {entry.DatLineId}");
            sb.AppendLine($"Timestamp: {logTs:o}");
            sb.AppendLine($"Result:    {(skippedDatLines.Count == 0 ? "ok" : "partial")}");
            sb.AppendLine($"Propagated dat-lines: {propagated}");
            if (skippedDatLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Skipped dat-lines (release states not recalculated):");
                foreach (var (dlId, reason) in skippedDatLines)
                    sb.AppendLine($"  {reason}  dat-line={dlId}  volume={vol.Id}");
                sb.AppendLine();
                sb.AppendLine("lost-propagation-partial");
            }
            File.WriteAllText(logFile, sb.ToString());
        }
        catch { /* non-fatal */ }

        if (skippedDatLines.Count > 0)
        {
            await new InfoDialog("Mark Lost — Partial",
                $"Volume \"{entry.Label}\" has been marked as lost.\n\n" +
                $"Warning: {skippedDatLines.Count} DAT-line database(s) were unavailable " +
                "and could not be reconciled. Release states for those DAT lines may be stale.\n\n" +
                "Details have been written to the operation log.")
                .ShowDialog(this);
        }

        RefreshVolumes();
        RefreshAnalyticsIfBuilt();
        RefreshDiskDetailIfSelected();
        var updatedLost = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updatedLost is not null)
        {
            VolumesList.SelectedItem = updatedLost;
            UpdateVolumeDetailPanel(updatedLost);
        }
    }

    // ── Delete Volume ─────────────────────────────────────────────────────────

    private async void OnDeleteVolume(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;

        if (entry.Status != "lost")
        {
            await new InfoDialog(
                "Cannot Delete Volume",
                "Delete Volume is only available for volumes already marked LOST.\n\n" +
                "Mark the volume as LOST first if you want to remove it permanently from the catalog.")
                .ShowDialog(this);
            return;
        }

        var confirmed = await new ConfirmDialog(
            "Delete Volume",
            "This will permanently remove the lost volume from the catalog.\n" +
            "Volume metadata and all volume-artifact mappings will be deleted.\n\n" +
            "This action cannot be undone.")
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        _catalog.DeleteVolume(entry.Id);

        RebuildLibraryDatasets();
        RefreshVolumes();
        RefreshAnalyticsIfBuilt();
        RefreshDiskDetailIfSelected();
        UpdateVolumeDetailPanel(null);
    }

    /// <summary>
    /// For every derived artifact that is exclusively on the given (now-lost) volume,
    /// sets its status to "lost" in the DAT-line database and recalculates the
    /// release status for affected releases.
    /// </summary>
    /// <returns>
    /// A tuple of (processed, skipped) where <c>processed</c> is the number of dat-lines
    /// successfully updated and <c>skipped</c> lists each dat-line that could not be
    /// reconciled along with the reason event name.
    /// </returns>
    private (int Processed, List<(string DatLineId, string Reason)> Skipped)
        PropagateVolumeLossToReleases(string volumeId)
    {
        var datLinesById = _catalog.LoadDatLines().ToDictionary(dl => dl.Id);
        var exclusive    = _catalog.GetDerivedArtifactsExclusiveToVolume(volumeId);
        int processed    = 0;
        var skipped      = new List<(string DatLineId, string Reason)>();

        foreach (var (datLineId, daIds) in exclusive)
        {
            if (!datLinesById.TryGetValue(datLineId, out var dlRecord))
            {
                skipped.Add((datLineId, "lost-propagation-skipped-missing-datline-record"));
                continue;
            }
            if (dlRecord.DataStorePath.Length == 0)
            {
                skipped.Add((datLineId, "lost-propagation-skipped-empty-datastorepath"));
                continue;
            }
            var absPath = Path.Combine(_dataDir, dlRecord.DataStorePath);
            if (!File.Exists(absPath))
            {
                skipped.Add((datLineId, "lost-propagation-skipped-missing-db"));
                continue;
            }
            var store = new Data.DatLineStore(absPath);
            store.BatchUpdateDerivedArtifactStatus(daIds, "lost");
            store.RecalculateReleaseStatusForArtifacts(daIds);
            processed++;
        }

        return (processed, skipped);
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
        LibraryStatusFilter.ItemsSource   = new[] { "All Statuses", "Present", "Outdated", "Pending", "Missing", "Lost", "Unwanted", "New", "Hidden" };
        LibraryStatusFilter.SelectedIndex = 0;
    }

    private void RebuildLibraryDatasets()
    {
        var allPlatforms = _catalog.LoadPlatforms();
        var allDatLines  = _catalog.LoadDatLines();

        var merged = new List<LibraryDataset>();

        foreach (var dl in allDatLines)
        {
            if (!dl.CatalogEnabled) continue;
            if (dl.DataStorePath.Length == 0) continue;

            var platformName = allPlatforms.FirstOrDefault(p => p.Id == dl.HardwareFamilyId)?.Name
                               ?? dl.HardwareFamilyId;
            var absPath  = Path.Combine(_dataDir, dl.DataStorePath);
            if (!File.Exists(absPath)) continue;

            var store        = new DatLineStore(absPath);
            var filesByRelId = store.LoadAllReleaseFiles();
            var metadata     = store.LoadReleaseMetadata();
            var titleMode    = dl.LibraryTitleMode;

            MediaStore.EnsureMediaFolders(_dataDir, dl.HardwareFamilyId, dl.Id);

            var releases = store.LoadReleases()
                .Select(r =>
                {
                    filesByRelId.TryGetValue(r.Id, out var romFiles);
                    metadata.TryGetValue(r.Id, out var meta);
                    return new LibraryEntry
                    {
                        Name                  = r.Name,
                        DisplayName           = LibraryTitleResolver.Resolve(r.Name, titleMode, meta?.Title),
                        HardwareFamilyId      = dl.HardwareFamilyId,
                        Authority             = dl.Authority,
                        Metadata              = meta,
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
                        ShowInCatalog         = r.ShowInCatalog,
                    };
                })
                .ToList();

            if (releases.Count > 0)
                merged.Add(new LibraryDataset(platformName, FormatDatLineName(dl.Authority, dl.MediaTypeId), releases));
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
        // Dataset changed — a ReleaseId from the old dataset must not accidentally
        // re-match an entry in the new one (IDs are scoped per DAT-line store).
        _librarySelection.Clear();
        ApplyLibraryFilter();
    }

    private void OnLibrarySearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        => ApplyLibraryFilter();

    private void OnLibraryFilterChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => ApplyLibraryFilter();

    private void OnLibrarySelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        var entry = LibraryList.SelectedItem as LibraryEntry;
        _librarySelection.Select(entry);      // record the new selection by ReleaseId
        UpdateDetailPanel(entry);
    }

    private void ApplyLibraryFilter()
    {
        var search = LibrarySearchBox.Text?.Trim() ?? string.Empty;
        var status = LibraryStatusFilter.SelectedItem as string ?? "All Statuses";

        var filtered = LibraryFilterService.Apply(_activeDatasetEntries, search, status);

        // Suppress SelectionChanged while rebuilding ItemsSource so that Avalonia's
        // internal selection-model update (which may auto-select by index or fire
        // spuriously) cannot race with the explicit selection restoration below.
        LibraryList.SelectionChanged -= OnLibrarySelectionChanged;
        LibraryList.ItemsSource = filtered;

        // Restore the selection by ReleaseId (single source of truth).
        // If the previously selected release is no longer in the filtered list, clear both
        // the list selection and the detail pane — never leave a stale highlight visible.
        var selectedEntry = _librarySelection.ResolveAfterFilter(filtered);
        LibraryList.SelectedItem = selectedEntry;   // null = clear visual selection

        LibraryList.SelectionChanged += OnLibrarySelectionChanged;
        UpdateDetailPanel(selectedEntry);           // always driven by the resolved entry

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

        DetailName.Text             = entry.DisplayName.Length > 0 ? entry.DisplayName : entry.Name;
        var showDatName             = entry.DisplayName.Length > 0 && entry.DisplayName != entry.Name;
        DetailDatNameRow.IsVisible  = showDatName;
        DetailDatName.Text          = entry.Name;
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

            // For release-level strategies (release_shape, release_folder) the derived
            // artifact is keyed by "release:{id}", not per-file hashes — the per-file
            // loop above yields no rows.  Fall back to a release-level query.
            if (DetailDerivedFiles.Children.Count == 0
                && (entry.TransformStrategyType == "release_shape"
                    || entry.TransformStrategyType == "release_folder")
                && entry.ReleaseId.Length > 0
                && fileStore is not null)
            {
                var relDerived = fileStore.GetDerivedArtifactsByReleaseId(entry.ReleaseId);
                var relSrcKey  = $"release:{entry.ReleaseId}";
                var relSrc     = fileStore.GetSourceByContentKey(relSrcKey);
                bool relSrcOk  = relSrc is not null;
                int  relSrcVerified = relSrcOk ? 1 : 0;
                foreach (var dst in relDerived)
                {
                    bool hasDerivedHash = dst.HashedDerivedSha1.Length > 0;
                    bool derivedOk      = relSrcOk && hasDerivedHash;
                    var  xformName      = transformNames.TryGetValue(dst.StorageStrategyId, out var xn) ? xn : dst.StorageStrategyId;
                    DetailDerivedFiles.Children.Add(MakeDerivedFileRow(dst, xformName, relSrcVerified, 1, derivedOk, CopyAndToast));
                }
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

        // ACTIONS — Purge / Mark Unwanted / Restore Wanted / Catalog Visibility
        DetailActionsPanel.Children.Clear();
        BuildDetailActions(entry);
        bool showActions = DetailActionsPanel.Children.Count > 0;
        DetailActionsDivider.IsVisible = showActions;
        DetailActionsTitle.IsVisible   = showActions;
        DetailActionsPanel.IsVisible   = showActions;

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

    // ── Detail pane action buttons ────────────────────────────────────────────

    private void BuildDetailActions(Library.LibraryEntry entry)
    {
        var appRoot = AppContext.BaseDirectory;
        var status  = entry.Status;

        if (status == "Unwanted")
        {
            // Restore Wanted
            var restoreBtn = MakeActionButton("Restore Wanted", "#4CAF50");
            restoreBtn.Click += async (_, _) =>
            {
                var relName1 = entry.DisplayName.Length > 0 ? entry.DisplayName : entry.Name;
                var ok = await new ConfirmDialog("Restore Wanted",
                    $"Mark \"{relName1}\" as wanted again?\n\n" +
                    "Status will be set to Missing. No files are restored.")
                    .ShowDialog<bool>(this);
                if (!ok) return;

                if (entry.DbPath.Length > 0 && File.Exists(entry.DbPath))
                {
                    var store = new Data.DatLineStore(entry.DbPath);
                    store.UpdateReleaseStatus(entry.ReleaseId, "missing");
                    store.SetShowInCatalog(entry.ReleaseId, true);
                }
                RebuildLibraryDatasets();
                BuildAnalytics();
                ApplyLibraryFilter();
            };
            DetailActionsPanel.Children.Add(restoreBtn);

            // Show / Hide in catalog
            AddCatalogVisibilityButton(entry);
            return;
        }

        // Show / Hide in catalog — available for all statuses
        AddCatalogVisibilityButton(entry);

        bool hasArtifacts = entry.RomFiles.Count > 0 && entry.DbPath.Length > 0 &&
                            File.Exists(entry.DbPath) &&
                            new Data.DatLineStore(entry.DbPath)
                                .GetDerivedArtifactIdsByRelease(entry.ReleaseId).Count > 0;

        if (hasArtifacts)
        {
            // Purge button — only when artifacts exist
            var purgeBtn = MakeActionButton("Purge…", "#EF5350");
            purgeBtn.Click += async (_, _) => await OnPurgeRelease(entry, appRoot);
            DetailActionsPanel.Children.Add(purgeBtn);
        }
        else if (status != "Present")
        {
            // Mark as Unwanted — only when no physical artifacts
            var unwantedBtn = MakeActionButton("Mark as Unwanted", "#9E9E9E");
            unwantedBtn.Click += async (_, _) =>
            {
                var relName2 = entry.DisplayName.Length > 0 ? entry.DisplayName : entry.Name;
                var ok = await new ConfirmDialog("Mark as Unwanted",
                    $"Mark \"{relName2}\" as Unwanted?\n\n" +
                    "This release will be excluded from the wanted set and hidden from the catalog.")
                    .ShowDialog<bool>(this);
                if (!ok) return;

                if (entry.DbPath.Length > 0 && File.Exists(entry.DbPath))
                {
                    var store = new Data.DatLineStore(entry.DbPath);
                    store.UpdateReleaseStatus(entry.ReleaseId, "unwanted");
                    store.SetShowInCatalog(entry.ReleaseId, false);
                }
                RebuildLibraryDatasets();
                BuildAnalytics();
                ApplyLibraryFilter();
            };
            DetailActionsPanel.Children.Add(unwantedBtn);
        }
    }

    private void AddCatalogVisibilityButton(Library.LibraryEntry entry)
    {
        var label = entry.ShowInCatalog ? "Hide from Catalog" : "Show in Catalog";
        var btn   = MakeActionButton(label, "#7B68EE");
        btn.Click += async (_, _) =>
        {
            if (entry.DbPath.Length > 0 && File.Exists(entry.DbPath))
            {
                var store = new Data.DatLineStore(entry.DbPath);
                store.SetShowInCatalog(entry.ReleaseId, !entry.ShowInCatalog);
            }
            await System.Threading.Tasks.Task.CompletedTask; // satisfy async
            RebuildLibraryDatasets();
            ApplyLibraryFilter();
        };
        DetailActionsPanel.Children.Add(btn);
    }

    private async System.Threading.Tasks.Task OnPurgeRelease(Library.LibraryEntry entry, string appRoot)
    {
        var planner = new Purge.PurgeReleasePlanner(appRoot, _catalog);
        var plan    = planner.Plan(
            entry.ReleaseId,
            entry.DisplayName.Length > 0 ? entry.DisplayName : entry.Name,
            entry.Status,
            entry.DatLineId,
            entry.DbPath);

        // Build human-readable plan summary
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Release: {plan.ReleaseName}");
        sb.AppendLine($"Status:  {plan.CurrentStatus}");
        sb.AppendLine();

        if (plan.LocalArtifacts.Count > 0)
        {
            sb.AppendLine($"Local archive files ({plan.LocalArtifacts.Count}):");
            foreach (var la in plan.LocalArtifacts)
                sb.AppendLine($"  {la.FileName}  {FormatBytes(la.Bytes)}" +
                              (la.FileExists ? "" : "  [already absent]"));
            sb.AppendLine($"  → {FormatBytes(plan.TotalLocalBytes)} freed from archive");
            sb.AppendLine();
        }

        if (plan.VolumeArtifacts.Count > 0)
        {
            sb.AppendLine($"Volume copies ({plan.VolumeArtifacts.Count}):");
            foreach (var va in plan.VolumeArtifacts)
                sb.AppendLine($"  {va.VolumeLabel} / {va.FileName}  {FormatBytes(va.Bytes)}" +
                              (va.DiskMounted ? "" : $"  [disk {va.DiskLabel} offline]"));
            sb.AppendLine($"  → {FormatBytes(plan.TotalVolumeBytes)} freed from volumes");
            sb.AppendLine();
        }

        if (plan.OfflineDiskLabels.Count > 0)
            sb.AppendLine($"⚠ Required disks offline: {string.Join(", ", plan.OfflineDiskLabels)}");

        foreach (var w in plan.Warnings) sb.AppendLine($"⚠ {w}");
        foreach (var i in plan.Issues)   sb.AppendLine($"✗ {i}");

        if (!plan.CanExecute)
        {
            await new InfoDialog("Purge Blocked", sb.ToString()).ShowDialog<bool>(this);
            return;
        }

        sb.AppendLine();
        sb.AppendLine("This action is irreversible. Type your confirmation below.");

        var ok = await new ConfirmDialog("Confirm Purge", sb.ToString()).ShowDialog<bool>(this);
        if (!ok) return;

        var svc    = new Purge.PurgeReleaseService(appRoot, _catalog);
        var result = svc.Execute(plan);

        if (!result.Success)
        {
            await new InfoDialog("Purge Failed", result.ErrorMessage ?? "Unknown error.").ShowDialog<bool>(this);
            return;
        }

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"Release marked UNWANTED.");
        summary.AppendLine($"Files deleted: {result.FilesDeleted}");
        if (result.LocalBytesFreed > 0)
            summary.AppendLine($"Archive freed: {FormatBytes(result.LocalBytesFreed)}");
        if (result.VolumeBytesFreed > 0)
            summary.AppendLine($"Volume freed:  {FormatBytes(result.VolumeBytesFreed)}");
        if (result.RefreshedVolumeLabels.Count > 0)
            summary.AppendLine($"Volumes refreshed: {string.Join(", ", result.RefreshedVolumeLabels)}");

        await new InfoDialog("Purge Complete", summary.ToString()).ShowDialog<bool>(this);

        RebuildLibraryDatasets();
        BuildAnalytics();
        ApplyLibraryFilter();
        RefreshDiskDetailIfSelected();
    }

    private static Button MakeActionButton(string label, string hexColor)
    {
        var btn = new Button
        {
            Content             = label,
            FontSize            = 12,
            Padding             = new Avalonia.Thickness(10, 5),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Background          = new SolidColorBrush(Color.Parse("#1A1A2E")),
            Foreground          = new SolidColorBrush(Color.Parse(hexColor)),
            BorderBrush         = new SolidColorBrush(Color.Parse(hexColor)),
            BorderThickness     = new Avalonia.Thickness(1),
            CornerRadius        = new Avalonia.CornerRadius(4),
        };
        return btn;
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
        var platforms       = _catalog.LoadPlatforms();
        var datLines        = _catalog.LoadDatLines();
        var volumes         = _catalog.GetVolumes();
        var disks           = _catalog.GetDisks();
        var artifactsStored = _catalog.CountStoredArtifacts();

        DashPlatformsCount.Text = platforms.Count.ToString("N0");
        DashDatLinesCount.Text  = datLines.Count.ToString("N0");
        DashReleasesCount.Text  = datLines.Sum(dl => dl.ReleaseCount).ToString("N0");
        DashArtifactsCount.Text = artifactsStored.ToString("N0");
        DashVolumesCount.Text   = volumes.Count.ToString("N0");
        DashDisksCount.Text     = disks.Count.ToString("N0");

        // ── Integrity & Attention ────────────────────────────────────────────
        DashVolOk.Text = volumes.Count(v => v.Status != "lost" && v.Health == "ok").ToString("N0");
        SetAccentVal(DashVolCrit,  volumes.Count(v => v.Health == "crit"));
        SetAccentVal(DashVolLost,  volumes.Count(v => v.Status == "lost"));
        SetAccentVal(DashDiskLost, disks.Count(d => d.Status == "lost"));

        int relMissing = 0, relPending = 0, relOutdated = 0, relPresent = 0;
        foreach (var dl in datLines)
        {
            if (dl.DataStorePath.Length == 0) continue;
            var dbPath = Path.Combine(_dataDir, dl.DataStorePath);
            if (!File.Exists(dbPath)) continue;
            var (missing, pending, outdated, present, _, _) = new DatLineStore(dbPath).GetAllStatusCounts();
            relMissing  += missing;
            relPending  += pending;
            relOutdated += outdated;
            relPresent  += present;
        }
        SetAccentVal(DashRelMissing, relMissing);
        DashRelPending.Text  = relPending.ToString("N0");
        DashRelOutdated.Text = relOutdated.ToString("N0");

        var totalReleases = datLines.Sum(dl => dl.ReleaseCount);
        var coveragePct   = totalReleases > 0 ? relPresent * 100 / totalReleases : 0;
        DashCoverage.Text = $"Coverage: {coveragePct}%";

        // ── Tools ────────────────────────────────────────────────────────────
        var tools       = _catalog.LoadTools();
        var appRoot     = AppContext.BaseDirectory;
        int toolBundled = 0, toolPresent = 0, toolMissing = 0;
        foreach (var tool in tools)
        {
            if (tool.IsBundled) toolBundled++;
            var exePath = Path.Combine(appRoot, "tools", tool.FolderName, tool.ExecutableName);
            if (File.Exists(exePath)) toolPresent++;
            else                      toolMissing++;
        }
        DashToolsBuiltIn.Text = toolBundled.ToString("N0");
        DashToolsPresent.Text = toolPresent.ToString("N0");
        SetAccentVal(DashToolsMissing, toolMissing);

        // ── Pipeline ─────────────────────────────────────────────────────────
        static int CountFiles(string dir) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length : 0;

        DashPipelineIncoming.Text = CountFiles(Path.Combine(appRoot, "incoming-roms")).ToString("N0");
        DashPipelineStaging.Text  = CountFiles(Path.Combine(appRoot, "staging")).ToString("N0");
        DashPipelineSource.Text   = CountFiles(Path.Combine(appRoot, "source")).ToString("N0");
        DashPipelineArchive.Text  = artifactsStored.ToString("N0");

        // ── Scrape Staging ────────────────────────────────────────────────────
        var stagingService = new Arkadia.Data.ScreenScraperStagingService(appRoot);
        var topStaging     = stagingService.LoadTopBySize(5);
        DashStagingPanel.Children.Clear();
        DashStagingEmpty.IsVisible = topStaging.Count == 0;
        foreach (var rec in topStaging)
        {
            var row = new Grid
            {
                ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto,Auto"),
                Margin            = new Avalonia.Thickness(0, 0, 0, 3),
            };
            var name = new TextBlock
            {
                Text       = rec.PackageName,
                FontSize   = 11,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CCCCDD")),
            };
            var size = new TextBlock
            {
                Text       = rec.SizeDisplay,
                FontSize   = 11,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#888899")),
                Margin     = new Avalonia.Thickness(12, 0, 0, 0),
            };
            var pct = new TextBlock
            {
                Text       = rec.CompletionDisplay,
                FontSize   = 11,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#666680")),
                Margin     = new Avalonia.Thickness(12, 0, 0, 0),
            };
            Avalonia.Controls.Grid.SetColumn(name, 0);
            Avalonia.Controls.Grid.SetColumn(size, 1);
            Avalonia.Controls.Grid.SetColumn(pct,  2);
            row.Children.Add(name);
            row.Children.Add(size);
            row.Children.Add(pct);
            DashStagingPanel.Children.Add(row);
        }

        LoadLatestLogs();
    }

    private static void SetAccentVal(TextBlock tb, int value)
    {
        tb.Text       = value.ToString("N0");
        tb.Foreground = value > 0
            ? new SolidColorBrush(Avalonia.Media.Color.Parse("#E07040"))
            : new SolidColorBrush(Avalonia.Media.Color.Parse("#F0F0F0"));
    }

    private void OnDashboardRefresh(object? sender, RoutedEventArgs e) => InitDashboard();

    private void LoadLatestLogs()
    {
        DashLatestLogsPanel.Children.Clear();

        var logsRoot = Path.Combine(AppContext.BaseDirectory, "logs");
        var folderTypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ingest"]           = "Ingest",
            ["verify"]           = "Verify",
            ["repair"]           = "Repair",
            ["volume-move"]      = "Volume Move",
            ["volume-resize"]    = "Volume Resize",
            ["volume-append"]    = "Volume Append",
            ["volume-reabsorb"]  = "Volume Reabsorb",
            ["volume-lost"]      = "Volume Lost",
            ["volume-verify"]    = "Volume Verify",
            ["unexpected"]       = "Unexpected",
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

        // Column header  — Open | Type | Timestamp | File
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("55,110,150,*"), Margin = new Avalonia.Thickness(0, 0, 0, 8) };
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
        AddHeader(1, "TYPE");
        AddHeader(2, "TIMESTAMP");
        AddHeader(3, "FILE");
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
            ["Verify"]           = "#7B68EE",
            ["Repair"]           = "#4A90D9",
            ["Volume Move"]      = "#E07040",
            ["Volume Resize"]    = "#9C7AC9",
            ["Volume Append"]    = "#4CAF50",
            ["Volume Reabsorb"]  = "#4A90D9",
            ["Volume Lost"]      = "#EF5350",
            ["Volume Verify"]    = "#26C6DA",
            ["Unexpected"]       = "#FFA726",
        };

        foreach (var (type, ts, fileName, fullPath) in recent)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("55,110,150,*"),
                Margin = new Avalonia.Thickness(0, 0, 0, 6),
            };

            var typeColor = typeColors.GetValueOrDefault(type, "#888899");

            var openBtn = new Button
            {
                Content             = "Open",
                Tag                 = fullPath,
                Classes             = { "view-toggle" },
                Padding             = new Avalonia.Thickness(8, 2),
                FontSize            = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            };
            openBtn.Click += OnDashLogOpen;

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

            Grid.SetColumn(openBtn,   0);
            Grid.SetColumn(typeBlock, 1);
            Grid.SetColumn(timeBlock, 2);
            Grid.SetColumn(nameBlock, 3);
            row.Children.Add(openBtn);
            row.Children.Add(typeBlock);
            row.Children.Add(timeBlock);
            row.Children.Add(nameBlock);
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

    // ── Verify ALL ────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task OnVerifyAllDatLine(Systems.DatLineInfo info)
    {
        if (info.CatalogId is null || info.DataStorePath.Length == 0)
        {
            await new InfoDialog("Cannot Verify ALL",
                "This DAT line has no data store path. Import the DAT line first.")
                .ShowDialog(this);
            return;
        }

        var dbPath = Path.Combine(_dataDir, info.DataStorePath);
        if (!File.Exists(dbPath))
        {
            await new InfoDialog("Cannot Verify ALL",
                $"DAT line database not found at:\n{dbPath}")
                .ShowDialog(this);
            return;
        }

        var volumes = _catalog.GetVolumes()
            .Where(v => v.DatLineId == info.CatalogId)
            .ToList();

        var platform     = info.CatalogPlatformId is not null
                               ? _catalog.GetPlatform(info.CatalogPlatformId) : null;
        var platformDesc = platform is not null
                               ? $"{platform.Manufacturer} {platform.Name}".Trim()
                               : (info.CatalogPlatformId ?? "Unknown System");

        // ── Pre-flight confirmation ────────────────────────────────────────────
        var preflight = new System.Text.StringBuilder();
        preflight.AppendLine($"DAT Line:  {info.Name}");
        preflight.AppendLine($"System:    {platformDesc}");
        preflight.AppendLine($"Volumes:   {volumes.Count}");
        preflight.AppendLine();
        preflight.AppendLine("This operation will:");
        preflight.AppendLine("  • Verify all artifacts in the Local Archive for this DAT line");
        preflight.AppendLine("  • Verify all assigned volumes for this DAT line");
        preflight.AppendLine("  • Update artifact, release, and volume health state incrementally");
        preflight.AppendLine("  • Optionally quarantine mismatched/unexpected files into incoming-skip");
        if (volumes.Any(v => v.Status == "lost"))
            preflight.AppendLine("  • Attempt to restore LOST volumes if fully verified");
        preflight.AppendLine();
        preflight.AppendLine("Cancel at any time — completed work up to that point is preserved.");
        preflight.Append("Proceed?");

        var confirmed = await new ConfirmDialog("Verify ALL", preflight.ToString())
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        var dialog  = new DatLineVerifyDialog(info.Name, platformDesc);
        var dlgTask = dialog.ShowDialog(this);

        await System.Threading.Tasks.Task.Run(async () =>
        {
            try { await RunVerifyAllDatLine(dialog, info, dbPath, volumes); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => dialog.SetFailed(ex.Message));
            }
        });

        await dlgTask;

        // Final UI refresh — incremental DB applies already happened during the operation.
        RebuildLibraryDatasets();
        RefreshVolumes();
        RefreshAnalyticsIfBuilt();
        RefreshDiskDetailIfSelected();
    }

    private async System.Threading.Tasks.Task RunVerifyAllDatLine(
        DatLineVerifyDialog         dialog,
        Systems.DatLineInfo         info,
        string                      dbPath,
        List<Data.VolumeRecord>     volumes)
    {
        var appRoot  = AppContext.BaseDirectory;
        var store    = new Data.DatLineStore(dbPath);
        var log      = new System.Text.StringBuilder();
        var startTs  = DateTime.UtcNow;
        bool cancelled = false;

        bool quarantineMismatch   = _catalog.GetBoolSetting("quarantine_mismatch_on_verify",  defaultValue: true);
        bool quarantineUnexpected = _catalog.GetBoolSetting("quarantine_unexpected_on_verify", defaultValue: false);
        var quarantineBaseDir = Path.Combine(appRoot, "incoming-skip",
            SafeFileName(info.CatalogPlatformId ?? "unknown"),
            SafeFileName(info.Name));

        log.AppendLine($"Verify ALL — {info.Name}");
        log.AppendLine($"Started:     {startTs:o}");
        log.AppendLine($"Volumes:     {volumes.Count}");
        log.AppendLine();

        // ── PHASE 1: Build scope ───────────────────────────────────────────────
        var allVolumeAssigned      = new HashSet<string>(StringComparer.Ordinal);
        var volumeAssignmentsByVol = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var vol in volumes)
        {
            var vas = _catalog.GetVolumeArtifacts(vol.Id);
            var ids = vas.Where(va => va.Status != "lost")
                         .Select(va => va.DerivedArtifactId)
                         .ToList();
            allVolumeAssigned.UnionWith(ids);
            volumeAssignmentsByVol[vol.Id] = ids;
        }

        var allDaStatuses     = store.GetAllDerivedArtifactStatuses();
        var localArchiveDaIds = allDaStatuses
            .Where(x => x.Status != "lost" && !allVolumeAssigned.Contains(x.Id))
            .Select(x => x.Id)
            .ToList();

        log.AppendLine($"── Phase 1: Scope ──────────────────────────────────────────────────────");
        log.AppendLine($"  Total derived artifacts:    {allDaStatuses.Count}");
        log.AppendLine($"  Volume-assigned (non-lost): {allVolumeAssigned.Count}");
        log.AppendLine($"  Local archive targets:      {localArchiveDaIds.Count}");
        log.AppendLine();

        // ── PHASE 2: Verify Local Archive ─────────────────────────────────────
        int archiveVerified = 0, archiveMissing = 0, archiveMismatch = 0, archiveUnexpected = 0;
        long archiveVerifiedBytes = 0;

        log.AppendLine($"── Phase 2: Local Archive ({localArchiveDaIds.Count} artifact(s)) ──────────────────");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            dialog.SetStatus($"Phase 2: Verifying Local Archive — {localArchiveDaIds.Count} artifact(s)…"));

        if (localArchiveDaIds.Count > 0)
        {
            var archiveInfos      = store.GetLocalArchiveVerifyInfos(localArchiveDaIds);
            var archiveChangedIds = new List<string>();

            // Build expected relative path set for unexpected-file detection
            var expectedRelPaths = new HashSet<string>(
                archiveInfos.Select(ai =>
                    ai.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);

            // Derive archive base dir from the first artifact's relative path (segments 0-2)
            string? archiveBaseDir = null;
            if (archiveInfos.Count > 0)
            {
                var seg = archiveInfos[0].RelativePath.Split('/');
                if (seg.Length >= 3)
                    archiveBaseDir = Path.Combine(appRoot, seg[0], seg[1], seg[2]);
            }

            foreach (var ai in archiveInfos)
            {
                var absPath   = Path.Combine(appRoot,
                    ai.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var dispPath  = ai.RelativePath;
                var sizeLabel = FormatBytes(ai.SizeBytes);

                if (!File.Exists(absPath))
                {
                    archiveMissing++;
                    log.AppendLine($"  MISSING   {dispPath}");
                    store.BatchUpdateDerivedArtifactStatus(new[] { ai.DerivedArtifactId }, "missing");
                    archiveChangedIds.Add(ai.DerivedArtifactId);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow("Local Archive", "MISSING", dispPath, ""));
                    continue;
                }

                var actualSize = new FileInfo(absPath).Length;

                if (ai.Sha1.Length > 0)
                {
                    var actualSha1 = ComputeFileSha1(absPath);
                    if (string.Equals(actualSha1, ai.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        archiveVerified++;
                        archiveVerifiedBytes += actualSize;
                        log.AppendLine($"  VERIFIED  {dispPath}  sha1={actualSha1}");
                        store.BatchUpdateDerivedArtifactStatus(new[] { ai.DerivedArtifactId }, "present");
                        archiveChangedIds.Add(ai.DerivedArtifactId);
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow("Local Archive", "VERIFIED", dispPath, sizeLabel));
                    }
                    else
                    {
                        archiveMismatch++;
                        var hashDetail = $"exp:{ai.Sha1[..8]}… got:{actualSha1[..8]}…";
                        log.AppendLine($"  MISMATCH  {dispPath}  expected={ai.Sha1}  actual={actualSha1}");
                        if (quarantineMismatch)
                        {
                            bool moved = TryQuarantineFile(absPath, ai.FileName,
                                quarantineBaseDir, out var moveErr);
                            if (moved)
                            {
                                log.AppendLine($"  QUARANTINED  {dispPath}  → incoming-skip");
                                store.BatchUpdateDerivedArtifactStatus(
                                    new[] { ai.DerivedArtifactId }, "missing");
                                archiveChangedIds.Add(ai.DerivedArtifactId);
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                    dialog.AppendRow("Local Archive", "QUARANTINED",
                                        dispPath, "moved to incoming-skip"));
                            }
                            else
                            {
                                log.AppendLine($"  QUARANTINE-FAILED  {dispPath}  {moveErr}");
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                    dialog.AppendRow("Local Archive", "MISMATCH", dispPath, hashDetail));
                            }
                        }
                        else
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                dialog.AppendRow("Local Archive", "MISMATCH", dispPath, hashDetail));
                        }
                    }
                }
                else
                {
                    // No SHA1 recorded: existence + size check only
                    bool sizeOk = ai.SizeBytes <= 0 || actualSize == ai.SizeBytes;
                    if (sizeOk)
                    {
                        archiveVerified++;
                        archiveVerifiedBytes += actualSize;
                        log.AppendLine($"  VERIFIED  {dispPath}  (no sha1, size ok)");
                        store.BatchUpdateDerivedArtifactStatus(
                            new[] { ai.DerivedArtifactId }, "present");
                        archiveChangedIds.Add(ai.DerivedArtifactId);
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow("Local Archive", "VERIFIED", dispPath,
                                $"{sizeLabel} (no sha1)"));
                    }
                    else
                    {
                        archiveMismatch++;
                        var detail = $"size:{actualSize}≠{ai.SizeBytes}";
                        log.AppendLine($"  MISMATCH  {dispPath}  {detail}");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow("Local Archive", "MISMATCH", dispPath, detail));
                    }
                }
            }

            // Batch release recalculation after archive phase
            if (archiveChangedIds.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(archiveChangedIds);

            // Unexpected files in the DAT-line's archive directory
            if (archiveBaseDir is not null && Directory.Exists(archiveBaseDir))
            {
                foreach (var file in Directory.EnumerateFiles(
                             archiveBaseDir, "*", SearchOption.AllDirectories))
                {
                    var relToAppRoot = Path.GetRelativePath(appRoot, file);
                    if (!expectedRelPaths.Contains(relToAppRoot))
                    {
                        archiveUnexpected++;
                        log.AppendLine($"  UNEXPECTED  {relToAppRoot}");
                        if (quarantineUnexpected)
                        {
                            var uDir  = Path.Combine(appRoot, "incoming-skip", "unexpected");
                            bool moved = TryQuarantineFile(file, Path.GetFileName(file),
                                uDir, out var uErr);
                            log.AppendLine(moved
                                ? $"  QUARANTINED  {relToAppRoot}  → incoming-skip/unexpected"
                                : $"  QUARANTINE-FAILED  {relToAppRoot}  {uErr}");
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                dialog.AppendRow("Local Archive",
                                    moved ? "QUARANTINED" : "UNEXPECTED", relToAppRoot,
                                    moved ? "moved to incoming-skip/unexpected" : ""));
                        }
                        else
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                dialog.AppendRow("Local Archive", "UNEXPECTED", relToAppRoot, ""));
                        }
                    }
                }
            }

            log.AppendLine($"  → verified={archiveVerified}  missing={archiveMissing}  " +
                           $"mismatch={archiveMismatch}  unexpected={archiveUnexpected}  " +
                           $"sha1-verified={FormatBytes(archiveVerifiedBytes)}");
        }
        else
        {
            log.AppendLine("  (no local archive artifacts for this DAT-line)");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.AppendRow("Local Archive", "SKIPPED", "", "no local archive artifacts"));
        }
        log.AppendLine();

        // ── PHASES 3+4: Verify volumes ─────────────────────────────────────────
        int totalVols     = volumes.Count, verifiedVols = 0, skippedVols = 0, restoredVols = 0;
        int totalExpected = 0, totalVerified = 0, totalMissing = 0, totalMismatch = 0, totalUnexpected = 0;

        log.AppendLine($"── Phase 3: Volumes ({totalVols}) ──────────────────────────────────────────────");

        if (volumes.Count == 0)
        {
            log.AppendLine("  (no volumes assigned to this DAT-line)");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.AppendRow("Volumes", "SKIPPED", "", "no assigned volumes"));
        }

        var allDisks     = _catalog.GetDisks().ToDictionary(d => d.Id, StringComparer.Ordinal);
        var runtimeDisks = Data.DiskDiscoveryService.DiscoverAll()
            .Where(d => d.DiskId.Length > 0)
            .ToDictionary(d => d.DiskId, StringComparer.Ordinal);

        for (int vi = 0; vi < volumes.Count && !cancelled; vi++)
        {
            var vol      = volumes[vi];
            var volLabel = vol.Label;
            bool wasLost = vol.Status == "lost";

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.SetStatus(
                    $"Phase 3: Volume {vi + 1}/{totalVols}: {volLabel}" +
                    $"  |  Verified: {totalVerified}  Missing: {totalMissing}  " +
                    $"Mismatch: {totalMismatch}"));

            log.AppendLine($"  [{volLabel}]{(wasLost ? " (was LOST)" : "")}");

            // ── Resolve volume root ──────────────────────────────────────────
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
                    if (!runtimeDisks.TryGetValue(loc.DiskId, out var rt))
                    {
                        var diskLabel = allDisks.TryGetValue(loc.DiskId, out var drC)
                            ? drC.Label : loc.DiskId;
                        bool retry = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                            async () => await new ConfirmDialog("Disk Not Mounted",
                                $"Volume \"{volLabel}\" is on disk \"{diskLabel}\" " +
                                "which is not currently mounted.\n\n" +
                                "Mount the disk and click OK to retry, or Cancel to stop " +
                                "verifying remaining volumes.")
                                .ShowDialog<bool>(this));

                        if (!retry)
                        {
                            cancelled = true;
                            log.AppendLine($"    CANCELLED by user — disk \"{diskLabel}\" not mounted");
                            break;
                        }

                        runtimeDisks = Data.DiskDiscoveryService.DiscoverAll()
                            .Where(d => d.DiskId.Length > 0)
                            .ToDictionary(d => d.DiskId, StringComparer.Ordinal);
                        runtimeDisks.TryGetValue(loc.DiskId, out rt);
                    }

                    if (rt is not null)
                    {
                        var diskRoot = Path.Combine(rt.Mountpoint, SafeFileName(volLabel));
                        if (Directory.Exists(diskRoot))
                        {
                            srcRoot  = diskRoot;
                            srcLabel = allDisks.TryGetValue(loc.DiskId, out var drD)
                                ? $"disk:{drD.Label}" : "disk";
                        }
                        else
                        {
                            skippedVols++;
                            log.AppendLine($"    SKIPPED — folder not found at {diskRoot}");
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                dialog.AppendRow(volLabel, "SKIPPED", "",
                                    "FOLDER NOT FOUND ON DISK"));
                            continue;
                        }
                    }
                    else
                    {
                        skippedVols++;
                        log.AppendLine($"    SKIPPED — disk not mounted after retry");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "SKIPPED", "", "DISK NOT MOUNTED"));
                        continue;
                    }
                }
                else
                {
                    skippedVols++;
                    var skipReason = wasLost ? "LOST — NO LOCATION" : "NO ACCESSIBLE SOURCE";
                    log.AppendLine($"    SKIPPED — {skipReason.ToLowerInvariant()}");
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(volLabel, "SKIPPED", "", skipReason));
                    continue;
                }
            }

            if (cancelled || srcRoot is null) break;

            // ── Expected artifact set for this volume ────────────────────────
            var vaIds    = volumeAssignmentsByVol.TryGetValue(vol.Id, out var ids)
                ? ids : new List<string>();
            var expected = store.GetArtifactVerifyInfos(vaIds);

            if (expected.Count == 0)
            {
                skippedVols++;
                log.AppendLine($"    SKIPPED — no active artifact assignments");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.AppendRow(volLabel, "SKIPPED", "", "NO ARTIFACTS"));
                continue;
            }

            log.AppendLine($"    source={srcLabel}  expected={expected.Count}");
            totalExpected += expected.Count;

            var expectedByRelPath = new Dictionary<string, Data.ArtifactVerifyInfo>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var e in expected)
                expectedByRelPath[Path.Combine(SafeFileName(e.ReleaseName), e.FileName)] = e;

            var actualFiles = Directory
                .EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(srcRoot.Length)
                              .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int volVerified = 0, volMissing = 0, volMismatch = 0, volUnexpected = 0;
            var volChangedIds = new List<string>();

            // ── Per-artifact verify ──────────────────────────────────────────
            foreach (var ei in expected)
            {
                var relPath  = Path.Combine(SafeFileName(ei.ReleaseName), ei.FileName);
                var absPath  = Path.Combine(srcRoot, relPath);
                var dispPath = $"{SafeFileName(ei.ReleaseName)}/{ei.FileName}";

                if (!File.Exists(absPath))
                {
                    volMissing++;
                    log.AppendLine($"    MISSING   {dispPath}");
                    store.BatchUpdateDerivedArtifactStatus(
                        new[] { ei.DerivedArtifactId }, "missing");
                    volChangedIds.Add(ei.DerivedArtifactId);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        dialog.AppendRow(volLabel, "MISSING", dispPath, ""));
                    continue;
                }

                var actualSize = new FileInfo(absPath).Length;

                if (ei.Sha1.Length > 0)
                {
                    var actualSha1 = ComputeFileSha1(absPath);
                    if (string.Equals(actualSha1, ei.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        volVerified++;
                        log.AppendLine($"    VERIFIED  {dispPath}  sha1={actualSha1}");
                        store.BatchUpdateDerivedArtifactStatus(
                            new[] { ei.DerivedArtifactId }, "present");
                        volChangedIds.Add(ei.DerivedArtifactId);
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "VERIFIED", dispPath,
                                FormatBytes(actualSize)));
                    }
                    else
                    {
                        volMismatch++;
                        var hashDetail = $"exp:{ei.Sha1[..8]}… got:{actualSha1[..8]}…";
                        log.AppendLine($"    MISMATCH  {dispPath}  " +
                                       $"expected={ei.Sha1}  actual={actualSha1}");
                        if (quarantineMismatch)
                        {
                            var qDir  = Path.Combine(quarantineBaseDir,
                                SafeFileName(ei.ReleaseName));
                            bool moved = TryQuarantineFile(absPath, ei.FileName,
                                qDir, out var moveErr);
                            if (moved)
                            {
                                log.AppendLine($"    QUARANTINED  {dispPath}  → incoming-skip");
                                store.BatchUpdateDerivedArtifactStatus(
                                    new[] { ei.DerivedArtifactId }, "missing");
                                volChangedIds.Add(ei.DerivedArtifactId);
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                    dialog.AppendRow(volLabel, "QUARANTINED", dispPath,
                                        "moved to incoming-skip"));
                            }
                            else
                            {
                                log.AppendLine($"    QUARANTINE-FAILED  {dispPath}  {moveErr}");
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                    dialog.AppendRow(volLabel, "MISMATCH", dispPath, hashDetail));
                            }
                        }
                        else
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                dialog.AppendRow(volLabel, "MISMATCH", dispPath, hashDetail));
                        }
                    }
                }
                else
                {
                    bool sizeOk = ei.SizeBytes <= 0 || actualSize == ei.SizeBytes;
                    if (sizeOk)
                    {
                        volVerified++;
                        log.AppendLine($"    VERIFIED  {dispPath}  (no sha1)");
                        store.BatchUpdateDerivedArtifactStatus(
                            new[] { ei.DerivedArtifactId }, "present");
                        volChangedIds.Add(ei.DerivedArtifactId);
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "VERIFIED", dispPath,
                                $"{FormatBytes(actualSize)} (no sha1)"));
                    }
                    else
                    {
                        volMismatch++;
                        var detail = $"size:{actualSize}≠{ei.SizeBytes}";
                        log.AppendLine($"    MISMATCH  {dispPath}  {detail}");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "MISMATCH", dispPath, detail));
                    }
                }
            }

            // ── Unexpected files ─────────────────────────────────────────────
            foreach (var rel in actualFiles)
            {
                if (!expectedByRelPath.ContainsKey(rel))
                {
                    volUnexpected++;
                    log.AppendLine($"    UNEXPECTED  {rel}");
                    if (quarantineUnexpected)
                    {
                        var uDir  = Path.Combine(appRoot, "incoming-skip", "unexpected",
                            SafeFileName(volLabel));
                        bool moved = TryQuarantineFile(Path.Combine(srcRoot, rel),
                            Path.GetFileName(rel), uDir, out var uErr);
                        log.AppendLine(moved
                            ? $"    QUARANTINED  {rel}  → incoming-skip/unexpected"
                            : $"    QUARANTINE-FAILED  {rel}  {uErr}");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, moved ? "QUARANTINED" : "UNEXPECTED", rel,
                                moved ? "moved to incoming-skip/unexpected" : ""));
                    }
                    else
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            dialog.AppendRow(volLabel, "UNEXPECTED", rel, ""));
                    }
                }
            }

            // ── Batch release recalculation for this volume ──────────────────
            if (volChangedIds.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(volChangedIds);

            totalVerified   += volVerified;
            totalMissing    += volMissing;
            totalMismatch   += volMismatch;
            totalUnexpected += volUnexpected;

            // ── Volume health ────────────────────────────────────────────────
            bool volClean  = volMissing == 0 && volMismatch == 0;
            var  volHealth = volClean && volVerified > 0 ? "ok" : "crit";
            _catalog.UpdateVolumeHealth(vol.Id, volHealth);

            // ── LOST restore — if was lost and now fully verified ────────────
            if (wasLost && volClean && volVerified > 0)
            {
                restoredVols++;
                _catalog.UpdateVolumeStatus(vol.Id, "present");
                var wsR   = Path.Combine(appRoot, "volumes", SafeFileName(volLabel));
                bool isWs = srcRoot.StartsWith(wsR, StringComparison.OrdinalIgnoreCase);
                var  loc  = _catalog.GetCurrentLocation(vol.Id);
                _catalog.SetCurrentLocation(new Data.VolumeLocationRecord
                {
                    Id           = Guid.NewGuid().ToString("N"),
                    VolumeId     = vol.Id,
                    LocationType = isWs ? "workspace" : "disk",
                    DiskId       = isWs ? null : loc?.DiskId,
                    Path         = srcRoot,
                    IsCurrent    = true,
                    CreatedAt    = DateTime.UtcNow,
                });
                log.AppendLine($"    RESTORED — was LOST, now present  health={volHealth}");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    dialog.AppendRow(volLabel, "RESTORED", "", "volume restored from LOST"));
            }
            else
            {
                log.AppendLine($"    → verified={volVerified}  missing={volMissing}  " +
                               $"mismatch={volMismatch}  unexpected={volUnexpected}  " +
                               $"health={volHealth}");
            }

            verifiedVols++;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                dialog.UpdateStats(totalVols, verifiedVols, skippedVols,
                                   totalExpected, totalVerified, totalMissing, totalMismatch));
        }

        // ── Write log ──────────────────────────────────────────────────────────
        var endTs = DateTime.UtcNow;
        log.AppendLine();
        log.AppendLine("── Summary ─────────────────────────────────────────────────────────────");
        log.AppendLine($"  Completed:     {endTs:o}");
        log.AppendLine($"  Duration:      {(endTs - startTs).TotalSeconds:F1}s");
        log.AppendLine($"  Result:        {(cancelled ? "partial" : "ok")}");
        log.AppendLine($"  Archive:       verified={archiveVerified}  missing={archiveMissing}  " +
                       $"mismatch={archiveMismatch}  unexpected={archiveUnexpected}  " +
                       $"sha1-verified={FormatBytes(archiveVerifiedBytes)}");
        log.AppendLine($"  Volumes:       total={totalVols}  scanned={verifiedVols}  " +
                       $"skipped={skippedVols}  restored={restoredVols}");
        log.AppendLine($"  Volume files:  verified={totalVerified}  missing={totalMissing}  " +
                       $"mismatch={totalMismatch}  unexpected={totalUnexpected}");
        if (cancelled)
            log.AppendLine("  Cancelled by user — remaining volumes not scanned.");

        try
        {
            var logDir  = Path.Combine(appRoot, "logs", "verify-all");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir,
                $"{endTs:yyyyMMdd-HHmmss}-verify-all-{SafeFileName(info.Name)}.log");
            File.WriteAllText(logFile, log.ToString());
        }
        catch { /* non-fatal */ }

        // ── Final dialog status ────────────────────────────────────────────────
        bool allClean = archiveMissing == 0 && archiveMismatch == 0 &&
                        totalMissing   == 0 && totalMismatch   == 0;
        var summary = new System.Text.StringBuilder();
        summary.AppendLine(cancelled
            ? "Verify ALL — partial (cancelled by user)."
            : "Verify ALL — complete.");
        summary.AppendLine($"Archive: verified={archiveVerified}  missing={archiveMissing}  " +
                           $"mismatch={archiveMismatch}  unexpected={archiveUnexpected}");
        summary.AppendLine($"Volumes: {verifiedVols}/{totalVols} scanned  " +
                           $"{skippedVols} skipped  {restoredVols} restored");
        summary.Append($"Files:   verified={totalVerified}  missing={totalMissing}  " +
                       $"mismatch={totalMismatch}  unexpected={totalUnexpected}");
        if (allClean && !cancelled)
            summary.Append("\nAll checked artifacts verified clean.");

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            dialog.UpdateStats(totalVols, verifiedVols, skippedVols,
                               totalExpected, totalVerified, totalMissing, totalMismatch);
            dialog.SetStatus(
                $"Archive: ok={archiveVerified}  missing={archiveMissing}  " +
                $"mismatch={archiveMismatch}" +
                $"  |  Volumes: {verifiedVols}/{totalVols}  " +
                $"Files: ok={totalVerified}  missing={totalMissing}  mismatch={totalMismatch}");
            dialog.SetCompleted(summary.ToString().TrimEnd());
        });
    }

    private async System.Threading.Tasks.Task OnUpdateDatLine(Systems.DatLineInfo info)
    {
        if (info.CatalogId is null || info.DataStorePath.Length == 0) return;

        var platformName  = _selectedPlatform?.Name ?? info.CatalogPlatformId ?? "";
        var strategyName  = info.StorageStrategy;

        var allDatLines = _catalog.LoadDatLines();
        var record      = allDatLines.FirstOrDefault(dl => dl.Id == info.CatalogId);
        if (record is null) return;

        var authorityName = GetAuthorityName(record.Authority);
        var updateDialog  = new UpdateDatDialog(record, platformName, authorityName, strategyName);
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
            RefreshSystemsKeepSelection(record.HardwareFamilyId);
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

        foreach (var game in games)
            if (game.WorkingState.Length > 0)
                _catalog.SetWorkingStateIfNotManual(game.Name, game.WorkingState);

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
            authority:     GetAuthorityName(record.Authority),
            releaseCount:  record.ReleaseCount);

        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed) return;

        _catalog.DeleteDatLine(catalogId, catalogPlatformId);

        var prevId = _selectedPlatform?.Id;
        RebuildLibraryDatasets();
        ResolveFlagImages();
        RefreshSystemsKeepSelection(prevId);
    }

    // ── Catalog ───────────────────────────────────────────────────────────────

    private List<LibraryEntry> _catalogDatasetEntries = [];
    private List<LibraryEntry> _filteredCatalogEntries = [];
    private LibraryEntry?      _catalogSelected;

    private Bitmap?            _catalogCoverBitmap;
    private Bitmap?            _catalogPhysicalBitmap;
    private readonly List<CoverItem> _coverItems = [];
    private int                _coverIndex;
    private readonly List<ExtrasItem> _extrasItems = [];
    private int                _extrasIndex;
    private Bitmap?            _extrasBitmap;
    private readonly List<string> _manualPaths = [];
    private Bitmap?            _galleryBitmap;
    private readonly List<GalleryItem> _galleryItems = [];
    private int                _galleryIndex;
    private LibVLC?            _libVlc;
    private MediaPlayer?       _mediaPlayer;
    private LibVLCSharp.Avalonia.VideoView? _videoView;
    private bool               _libVlcInitFailed;
    private double             _catalogLayoutScale = -1;
    private readonly MediaDiscoveryService _mediaDiscovery = new(_dataDir);

    private void InitCatalog()
    {
        CatalogStatusFilter.ItemsSource   = new[] {
            "All Statuses", "Present", "Outdated", "Pending", "Missing", "Lost", "New",
            "Unwanted", "Hidden",
        };
        CatalogStatusFilter.SelectedIndex = 0;
        BuildCatalogJumpList();

    }

    private void SyncCatalogContext()
    {
        // Populate platform selector from already-loaded datasets (same source as Library)
        var platforms = _activeLibraryDatasets.Select(d => d.Platform).Distinct().ToList();

        CatalogContextPlatform.SelectionChanged -= OnCatalogContextPlatformChanged;
        CatalogContextDatLine.SelectionChanged  -= OnCatalogContextDatLineChanged;

        CatalogContextPlatform.ItemsSource   = platforms;
        CatalogContextPlatform.SelectedIndex = platforms.Count > 0 ? 0 : -1;

        var activePlatform = CatalogContextPlatform.SelectedItem as string;
        var datLines = activePlatform is not null
            ? _activeLibraryDatasets.Where(d => d.Platform == activePlatform).Select(d => d.DatLine).ToList()
            : new List<string>();
        CatalogContextDatLine.ItemsSource   = datLines;
        CatalogContextDatLine.SelectedIndex = datLines.Count > 0 ? 0 : -1;

        CatalogContextPlatform.SelectionChanged += OnCatalogContextPlatformChanged;
        CatalogContextDatLine.SelectionChanged  += OnCatalogContextDatLineChanged;

        LoadCatalogDataset(activePlatform, CatalogContextDatLine.SelectedItem as string);
        UpdateCatalogResponsiveLayout();
    }

    private void OnCatalogContextPlatformChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        var platform = CatalogContextPlatform.SelectedItem as string;
        if (platform is null) return;
        var datLines = _activeLibraryDatasets.Where(d => d.Platform == platform).Select(d => d.DatLine).ToList();
        CatalogContextDatLine.SelectionChanged -= OnCatalogContextDatLineChanged;
        CatalogContextDatLine.ItemsSource       = datLines;
        CatalogContextDatLine.SelectedIndex     = 0;
        CatalogContextDatLine.SelectionChanged += OnCatalogContextDatLineChanged;
        LoadCatalogDataset(platform, datLines.Count > 0 ? datLines[0] : null);
    }

    private void OnCatalogContextDatLineChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        var platform = CatalogContextPlatform.SelectedItem as string;
        var datLine  = CatalogContextDatLine.SelectedItem  as string;
        if (platform is null) return;
        LoadCatalogDataset(platform, datLine);
    }

    private void LoadCatalogDataset(string? platform, string? datLine)
    {
        _catalogDatasetEntries = _activeLibraryDatasets
            .FirstOrDefault(d => d.Platform == platform && d.DatLine == datLine)
            ?.Entries.ToList() ?? [];
        ApplyCatalogFilter();
    }

    private void OnCatalogSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        => ApplyCatalogFilter();

    private void OnCatalogFilterChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => ApplyCatalogFilter();

    private void OnCatalogSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
        => UpdateCatalogHero(CatalogList.SelectedItem as LibraryEntry);

    private void ApplyCatalogFilter()
    {
        var search = CatalogSearch.Text?.Trim() ?? string.Empty;
        var status = CatalogStatusFilter.SelectedItem as string ?? "All Statuses";

        _filteredCatalogEntries = Catalog.CatalogFilterService.Apply(
            _catalogDatasetEntries, search, status);

        var total = _catalogDatasetEntries.Count;
        CatalogCountText.Text = _filteredCatalogEntries.Count == total
            ? $"{total} items"
            : $"{_filteredCatalogEntries.Count} of {total} items";

        // Filter change always clears selection
        RebuildCatalogList(preserveSelection: false);
        UpdateCatalogHero(null);
    }

    /// <summary>
    /// Stamps CatalogTitle on every filtered entry and re-renders the list.
    /// When preserveSelection is true, the previously selected entry is re-selected after re-render.
    /// </summary>
    private void RebuildCatalogList(bool preserveSelection)
    {
        foreach (var e in _filteredCatalogEntries)
            e.CatalogTitle = LibraryTitleResolver.Resolve(e.Name, "catalog", e.Metadata?.Title);

        var prev = preserveSelection ? CatalogList.SelectedItem as LibraryEntry : null;
        // Force Avalonia to re-render rows by clearing then re-assigning ItemsSource
        CatalogList.ItemsSource = null;
        CatalogList.ItemsSource = _filteredCatalogEntries;

        if (prev is not null && _filteredCatalogEntries.Contains(prev))
        {
            CatalogList.SelectedItem = prev;
            CatalogList.ScrollIntoView(prev);
        }
    }

    private void BuildCatalogJumpList()
    {
        CatalogJumpPanel.Children.Clear();
        const string Letters = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        foreach (var ch in Letters)
        {
            var letter = ch;
            var btn = new Button
            {
                Content     = ch.ToString(),
                Width       = 26,
                Height      = 18,
                Padding     = new Avalonia.Thickness(0),
                FontSize    = 9,
                FontWeight  = FontWeight.SemiBold,
                Background  = Avalonia.Media.Brushes.Transparent,
                Foreground  = new SolidColorBrush(Color.Parse("#666677")),
                BorderThickness = new Avalonia.Thickness(0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            btn.Click += (_, _) => JumpToLetter(letter);
            CatalogJumpPanel.Children.Add(btn);
        }
    }

    private void JumpToLetter(char letter)
    {
        // Jump matches CatalogTitle (the visible label), consistent with what the user sees
        LibraryEntry? target;
        if (letter == '#')
            target = _filteredCatalogEntries.FirstOrDefault(e =>
                e.CatalogTitle.Length > 0 && !char.IsLetter(e.CatalogTitle[0]));
        else
            target = _filteredCatalogEntries.FirstOrDefault(e =>
                e.CatalogTitle.Length > 0 &&
                char.ToUpperInvariant(e.CatalogTitle[0]) == char.ToUpperInvariant(letter));

        if (target is null) return;

        CatalogList.SelectedItem = target;
        CatalogList.ScrollIntoView(target);
    }

    private void OnCatalogManageMedia(object? sender, RoutedEventArgs e)
    {
        if (_catalogSelected is null) return;
        var idx    = _filteredCatalogEntries.IndexOf(_catalogSelected);
        var baseDir = Path.GetDirectoryName(_dataDir)!;
        var dialog = new CatalogManageMediaDialog(_filteredCatalogEntries, idx < 0 ? 0 : idx, baseDir);
        dialog.ShowDialog(this);
    }

    private void OnCatalogBulkScrape(object? sender, RoutedEventArgs e)
    {
        _cacheImport ??= new Arkadia.Providers.ScreenScraperCacheImportService(_dataDir, _catalog);
        var svc    = new CatalogBulkScrapeService(_dataDir, _catalog, _cacheImport);
        var dialog = new CatalogBulkScrapeDialog(
            svc, _catalogDatasetEntries, _catalogSelected, _metadataMappings);
        dialog.ShowDialog(this);
    }

    private async void OnCatalogExportAmp(object? sender, RoutedEventArgs e)
    {
        string hardwareFamilyId, datLineId;

        if (_catalogDatasetEntries.Count > 0)
        {
            var first        = _catalogDatasetEntries[0];
            hardwareFamilyId = first.HardwareFamilyId;
            datLineId        = first.DatLineId;
        }
        else
        {
            var datLineName = CatalogContextDatLine.SelectedItem as string;
            if (datLineName is null) return;
            var dl = _catalog.LoadDatLines()
                .Find(d => d.Name == datLineName);
            if (dl is null) return;
            hardwareFamilyId = dl.HardwareFamilyId;
            datLineId        = dl.Id;
        }

        CatalogExportAmpBtn.IsEnabled = false;
        try
        {
            var svc  = new AmpExportPlanService(_dataDir, _catalog);
            var plan = await System.Threading.Tasks.Task.Run(() =>
                svc.PlanExport(hardwareFamilyId, datLineId));
            var dialog = new AmpExportReportDialog(plan);
            await dialog.ShowDialog(this);
        }
        finally
        {
            CatalogExportAmpBtn.IsEnabled = true;
        }
    }

    private async void OnCatalogEditExtraNotes(object? sender, RoutedEventArgs e)
    {
        if (_catalogSelected is null) return;
        var entry        = _catalogSelected;
        var displayTitle = LibraryTitleResolver.Resolve(entry.Name, "catalog", entry.Metadata?.Title);
        var store        = new Arkadia.Data.DatLineStore(entry.DbPath);
        var current      = store.GetReleaseExtraNotes(entry.ReleaseId);

        var dialog = new CatalogEditExtraNotesDialog(displayTitle, current);
        var result = await dialog.ShowDialog<string?>(this);
        if (result is null) return; // cancelled

        store.SaveReleaseExtraNotes(entry.ReleaseId, result);
        RefreshCatalogExtraNotes(store, entry.ReleaseId);
    }

    private void RefreshCatalogExtraNotes(Arkadia.Data.DatLineStore store, string releaseId)
    {
        var notes = store.GetReleaseExtraNotes(releaseId) ?? "";
        CatalogExtraNotes.Text       = notes.Length > 0 ? notes : "No extra notes.";
        CatalogExtraNotes.Foreground = new SolidColorBrush(
            Color.Parse(notes.Length > 0 ? "#AAAABC" : "#666677"));
    }

    private void UpdateCatalogHero(LibraryEntry? entry)
    {
        _catalogSelected = entry;
        CatalogManageMediaBtn.IsEnabled = entry is not null;
        CatalogEditNotesBtn.IsEnabled   = entry is not null;

        if (entry is null)
        {
            StopVideo();
            _galleryItems.Clear();
            CatalogHeroEmpty.IsVisible   = true;
            CatalogHeroScroll.IsVisible  = false;
            return;
        }

        CatalogHeroEmpty.IsVisible  = false;
        CatalogHeroScroll.IsVisible = true;

        // Title
        var displayTitle = LibraryTitleResolver.Resolve(entry.Name, "catalog", entry.Metadata?.Title);
        CatalogHeroName.Text = displayTitle.Length > 0 ? displayTitle : entry.Name;

        // Alternate titles — from metadata, exclude duplicates of display title
        var alts = string.Empty;
        if (entry.Metadata?.AlternateTitles is { Length: > 0 } rawAlts)
        {
            alts = string.Join(" · ", rawAlts
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.Equals(displayTitle, StringComparison.OrdinalIgnoreCase) &&
                            !t.Equals(entry.Name,    StringComparison.OrdinalIgnoreCase))
                .Take(3));
        }
        CatalogHeroAltNames.Text      = alts;
        CatalogHeroAltNames.IsVisible = alts.Length > 0;

        // Original title — shown when present and different from the resolved display title and the DAT name
        var origTitle = entry.Metadata?.OriginalTitle ?? "";
        var showOrig  = CatalogHeroHelpers.ShouldShowOriginalTitle(origTitle, displayTitle, entry.Name);
        CatalogHeroOriginalTitle.Text      = showOrig ? $"Original: {origTitle}" : "";
        CatalogHeroOriginalTitle.IsVisible = showOrig;

        // TODO: add a large region flag to the hero header once region-keyed flag assets are available.
        //       The flag asset system is currently language-keyed (FlagImageLoader uses language codes).
        //       For v0 we show the same language flags in the subheader as a placeholder.
        CatalogHeroLangFlagsPanel.Children.Clear();
        foreach (var bmp in entry.FlagImages.Take(6))
        {
            CatalogHeroLangFlagsPanel.Children.Add(new Image
            {
                Source            = bmp,
                Width             = 18,
                Height            = 13,
                Margin            = new Avalonia.Thickness(0, 0, 3, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }

        // Cover gallery
        BuildCoverGallery(entry);
        ShowCoverItem(0);

        // Physical media — texture first, fallback to flat.
        CatalogPhysicalMediaImage.Source = null;
        _catalogPhysicalBitmap?.Dispose();
        _catalogPhysicalBitmap = null;

        var physicalPath = MediaStore.FindFirstPhysicalTexture(_dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name)
                        ?? MediaStore.FindFirstPhysical(_dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name);
        _catalogPhysicalBitmap = CoverLoader.TryLoad(physicalPath);
        if (_catalogPhysicalBitmap is not null)
        {
            CatalogPhysicalMediaImage.Source    = _catalogPhysicalBitmap;
            CatalogPhysicalMediaImage.IsVisible = true;
            CatalogPhysicalNoMedia.IsVisible    = false;
        }
        else
        {
            CatalogPhysicalMediaImage.IsVisible = false;
            CatalogPhysicalNoMedia.IsVisible    = true;
        }

        // Media gallery
        BuildGallery(entry);
        ShowGalleryItem(0);

        // Extras gallery
        BuildExtras(entry);
        ShowExtrasItem(0);

        // Manuals
        BuildManuals(entry);
        RefreshManualButtons();

        // Metadata details grid
        var badgeRegion = Arkadia.Data.MetadataValueNormalizer.Normalize(
            "region", entry.Region, _metadataMappings);
        var sm = entry.Metadata;

        SetMetaField(MetaCell_Status,    MetaVal_Status,    entry.Status);
        SetMetaField(MetaCell_System,    MetaVal_System,    entry.Platform.Length > 0 ? entry.Platform : entry.HardwareFamilyId);
        SetMetaField(MetaCell_Year,      MetaVal_Year,      sm?.Year       is { Length: > 0 } y  ? y  : "");
        SetMetaField(MetaCell_Region,    MetaVal_Region,    badgeRegion);
        SetMetaField(MetaCell_Genre,     MetaVal_Genre,     CatalogHeroHelpers.FormatGenreValue(sm?.Genre ?? "", sm?.Subgenre ?? ""));
        SetMetaField(MetaCell_Developer, MetaVal_Developer, sm?.Developer  is { Length: > 0 } d  ? d  : "");
        SetMetaField(MetaCell_Publisher, MetaVal_Publisher, sm?.Publisher  is { Length: > 0 } pb ? pb : "");
        SetMetaField(MetaCell_Language,  MetaVal_Language,  sm?.Languages  is { Length: > 0 } l  ? l  : "");
        SetMetaField(MetaCell_Rating,    MetaVal_Rating,    sm?.Rating     is { Length: > 0 } r  ? r  : "");
        SetMetaField(MetaCell_Players,   MetaVal_Players,   sm?.Players    is { Length: > 0 } p  ? p  : "");

        CatalogMetadataGrid.IsVisible =
            MetaCell_Status.IsVisible    || MetaCell_System.IsVisible    || MetaCell_Year.IsVisible  ||
            MetaCell_Region.IsVisible    || MetaCell_Genre.IsVisible     || MetaCell_Developer.IsVisible ||
            MetaCell_Publisher.IsVisible || MetaCell_Language.IsVisible  ||
            MetaCell_Rating.IsVisible    || MetaCell_Players.IsVisible;

        // Checklist
        static void SetChk(TextBlock icon, bool present)
        {
            icon.Text       = present ? "✓" : "✗";
            icon.Foreground = new SolidColorBrush(Color.Parse(present ? "#4CAF50" : "#EF5350"));
        }
        var m = entry.Metadata;
        SetChk(CatalogChkTitleIcon,    m is not null && m.Title.Length > 0);
        SetChk(CatalogChkOrigTitleIcon, m is not null && m.OriginalTitle.Length > 0);
        SetChk(CatalogChkDevIcon,      m is not null && m.Developer.Length > 0);
        SetChk(CatalogChkPubIcon,      m is not null && m.Publisher.Length > 0);
        SetChk(CatalogChkYearIcon,     m is not null && m.Year.Length > 0);
        SetChk(CatalogChkLangsIcon,    m is not null && m.Languages.Length > 0);

        // Quality indicator
        var score = m?.QualityScore ?? 0;
        CatalogQualityFilled.Text = new string('●', score);
        CatalogQualityEmpty.Text  = new string('●', 6 - score);
        CatalogQualityLabel.Text  = score switch
        {
            6       => "Perfect",
            5       => "High",
            3 or 4  => "Medium",
            1 or 2  => "Low",
            _       => ""
        };

        // Description
        var desc = m?.Description ?? "";
        CatalogHeroDesc.Text      = desc.Length > 0 ? desc : "No description available.";
        CatalogHeroDesc.Foreground = new SolidColorBrush(Color.Parse(desc.Length > 0 ? "#AAAABC" : "#666677"));

        // Extra Notes — Arkadia-owned, never touched by provider scrapes
        if (entry.DbPath.Length > 0 && entry.ReleaseId.Length > 0)
            RefreshCatalogExtraNotes(new Arkadia.Data.DatLineStore(entry.DbPath), entry.ReleaseId);
        else
        {
            CatalogExtraNotes.Text       = "No extra notes.";
            CatalogExtraNotes.Foreground = new SolidColorBrush(Color.Parse("#666677"));
        }

        // Scrape status — clear on hero change
        CatalogScrapeStatus.IsVisible = false;
        CatalogScrapeStatus.Text      = "";

    }

    // ── Media gallery ─────────────────────────────────────────────────────────

    private void EnsureLibVlc()
    {
        if (_libVlcInitFailed || _libVlc is not null) return;
        try
        {
            var vlcDir = Path.Combine(AppContext.BaseDirectory, "libraries", "lib-vlc",
                Environment.Is64BitProcess ? "win-x64" : "win-x86");

            if (!File.Exists(Path.Combine(vlcDir, "libvlc.dll")))
            {
                System.Diagnostics.Debug.WriteLine($"[VLC] Runtime not found at: {vlcDir}");
                _libVlcInitFailed = true;
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[VLC] Initializing from: {vlcDir}");
            Core.Initialize(vlcDir);

            _libVlc      = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVlc);
            _mediaPlayer.EncounteredError += (_, _) =>
                System.Diagnostics.Debug.WriteLine("[VLC] MediaPlayer.EncounteredError");

            _videoView = new LibVLCSharp.Avalonia.VideoView
            {
                MediaPlayer         = _mediaPlayer,
                IsVisible           = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Stretch,
            };
            CatalogVideoPanel.Children.Add(_videoView);
            System.Diagnostics.Debug.WriteLine("[VLC] Initialized OK");
        }
        catch (Exception ex)
        {
            _libVlcInitFailed = true;
            System.Diagnostics.Debug.WriteLine($"[VLC] Init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Catalog scale-based layout ────────────────────────────────────────────
    // Baseline: 2560×1080 (ultrawide workspace the layout was calibrated on).
    // scale = min(w/2560, h/1080) clamped to [0.70, 1.45].

    internal static double ComputeCatalogLayoutScale(double width, double height)
    {
        if (width <= 0 || height <= 0) return 1.0;
        double scale = Math.Min(width / 2560.0, height / 1080.0);
        return Math.Clamp(scale, 0.70, 1.45);
    }

    private void UpdateCatalogResponsiveLayout()
    {
        double newScale = ComputeCatalogLayoutScale(Bounds.Width, Bounds.Height);
        if (Math.Abs(newScale - _catalogLayoutScale) <= 0.02) return;
        _catalogLayoutScale = newScale;
        Debug.WriteLine($"[CatalogLayout] scale={newScale:F3} width={Bounds.Width:F0} height={Bounds.Height:F0}");
        ApplyCatalogLayout(newScale);
    }

    private void ApplyCatalogLayout(double scale)
    {
        CatalogCoverColumn.Width            = Math.Round(600 * scale);
        CatalogCoverViewport.Width          = Math.Round(600 * scale);
        CatalogCoverViewport.Height         = Math.Round(800 * scale);
        CatalogMediaWide.Width              = Math.Round(470 * scale);
        CatalogMediaViewport.Width          = Math.Round(470 * scale);
        CatalogMediaViewport.Height         = Math.Round(330 * scale);
        CatalogExtrasViewport.Width         = Math.Round(470 * scale);
        CatalogExtrasViewport.Height        = Math.Round(140 * scale);
        CatalogPhysicalMediaViewport.Width  = Math.Round(480 * scale);
        CatalogPhysicalMediaViewport.Height = Math.Round(260 * scale);

    }

    private void BuildGallery(LibraryEntry entry)
    {
        _galleryItems.Clear();
        _galleryItems.AddRange(_mediaDiscovery.FindGalleryItems(entry));
    }

    private void ShowGalleryItem(int index)
    {
        StopVideo();
        CatalogMediaImage.Source = null;
        _galleryBitmap?.Dispose();
        _galleryBitmap = null;

        if (_galleryItems.Count == 0)
        {
            _galleryIndex = 0;
            CatalogMediaNoItem.Text           = "No media";
            CatalogMediaNoItem.IsVisible      = true;
            CatalogMediaImage.IsVisible       = false;
            CatalogMediaPrev.IsVisible        = false;
            CatalogMediaNext.IsVisible        = false;
            CatalogVideoPlayOverlay.IsVisible = false;
            RefreshMediaCounter();
            return;
        }

        _galleryIndex = Math.Clamp(index, 0, _galleryItems.Count - 1);
        var item = _galleryItems[_galleryIndex];

        CatalogMediaNoItem.IsVisible = false;
        CatalogMediaPrev.IsVisible   = _galleryItems.Count > 1;
        CatalogMediaNext.IsVisible   = _galleryItems.Count > 1;

        if (item.IsVideo)
        {
            CatalogMediaImage.IsVisible = false;
            EnsureLibVlc();

            if (_libVlcInitFailed || _libVlc is null || _mediaPlayer is null)
            {
                CatalogMediaNoItem.Text           = $"▶  {Path.GetFileName(item.Path)}";
                CatalogMediaNoItem.IsVisible      = true;
                CatalogVideoPlayOverlay.IsVisible = true;
            }
            else
            {
                var autoplay     = _catalog.GetBoolSetting("catalog_video_autoplay", true);
                var audioEnabled = _catalog.GetBoolSetting("catalog_video_audio",    false);

                _videoView!.IsVisible             = true;
                CatalogMediaNoItem.IsVisible      = false;
                CatalogVideoPlayOverlay.IsVisible = !autoplay;

                if (autoplay)
                {
                    var capturedPath  = item.Path;
                    var capturedIndex = _galleryIndex;
                    var capturedMute  = !audioEnabled;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_mediaPlayer is null || _libVlc is null) return;
                        if (_galleryIndex != capturedIndex) return;
                        _mediaPlayer.Mute = capturedMute;
                        using var media = new Media(_libVlc, capturedPath, FromType.FromPath);
                        _mediaPlayer.Play(media);
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
            }
        }
        else
        {
            CatalogVideoPlayOverlay.IsVisible = false;
            _galleryBitmap              = CoverLoader.TryLoad(item.Path);
            CatalogMediaImage.Source    = _galleryBitmap;
            CatalogMediaImage.IsVisible = _galleryBitmap is not null;
            if (_galleryBitmap is null)
            {
                CatalogMediaNoItem.Text      = "No media";
                CatalogMediaNoItem.IsVisible = true;
            }
        }

        RefreshMediaCounter();
    }

    private void StopVideo()
    {
        try { if (_mediaPlayer?.IsPlaying == true) _mediaPlayer.Stop(); } catch { }
        if (_videoView is not null) _videoView.IsVisible = false;
        CatalogVideoPlayOverlay.IsVisible = false;
    }

    private void RefreshMediaCounter()
    {
        if (_galleryItems.Count == 0)
        {
            CatalogMediaCounter.Text = string.Empty;
            return;
        }
        var item = _galleryItems[_galleryIndex];
        CatalogMediaCounter.Text = $"{item.Label} · {_galleryIndex + 1} of {_galleryItems.Count}";
    }

    private void OnCatalogVideoPlay(object? sender, RoutedEventArgs e)
    {
        CatalogVideoPlayOverlay.IsVisible = false;
        if (_galleryItems.Count == 0 || _galleryIndex >= _galleryItems.Count) return;
        var item = _galleryItems[_galleryIndex];

        if (!item.IsVideo) return;

        if (_libVlcInitFailed || _libVlc is null || _mediaPlayer is null)
        {
            if (!File.Exists(item.Path)) return;
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
            return;
        }

        _videoView!.IsVisible = true;
        var capturedPath  = item.Path;
        var capturedIndex = _galleryIndex;
        var capturedMute  = !_catalog.GetBoolSetting("catalog_video_audio", false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_mediaPlayer is null || _libVlc is null) return;
            if (_galleryIndex != capturedIndex) return;
            _mediaPlayer.Mute = capturedMute;
            using var media = new Media(_libVlc, capturedPath, FromType.FromPath);
            _mediaPlayer.Play(media);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnCatalogMediaPrev(object? sender, RoutedEventArgs e)
    {
        if (_galleryItems.Count == 0) return;
        ShowGalleryItem((_galleryIndex - 1 + _galleryItems.Count) % _galleryItems.Count);
    }

    private void OnCatalogMediaNext(object? sender, RoutedEventArgs e)
    {
        if (_galleryItems.Count == 0) return;
        ShowGalleryItem((_galleryIndex + 1) % _galleryItems.Count);
    }

    // ── Cover gallery ─────────────────────────────────────────────────────────

    private void BuildCoverGallery(LibraryEntry entry)
    {
        _coverItems.Clear();
        _coverItems.AddRange(_mediaDiscovery.FindCoverItems(entry));
    }

    private void ShowCoverItem(int index)
    {
        CatalogHeroCover.Source = null;
        _catalogCoverBitmap?.Dispose();
        _catalogCoverBitmap = null;

        if (_coverItems.Count == 0)
        {
            _coverIndex = 0;
            CatalogHeroCover.IsVisible    = false;
            CatalogHeroNoCover.IsVisible  = true;
            CatalogCoverPrev.IsVisible    = false;
            CatalogCoverNext.IsVisible    = false;
            CatalogCoverCounter.Text      = string.Empty;
            RefreshCoverCounter();
            return;
        }

        _coverIndex = Math.Clamp(index, 0, _coverItems.Count - 1);
        var item = _coverItems[_coverIndex];

        _catalogCoverBitmap = CoverLoader.TryLoad(item.Path);
        if (_catalogCoverBitmap is not null)
        {
            CatalogHeroCover.Source      = _catalogCoverBitmap;
            CatalogHeroCover.IsVisible   = true;
            CatalogHeroNoCover.IsVisible = false;
        }
        else
        {
            CatalogHeroCover.IsVisible   = false;
            CatalogHeroNoCover.IsVisible = true;
        }

        bool multi = _coverItems.Count > 1;
        CatalogCoverPrev.IsVisible  = multi;
        CatalogCoverNext.IsVisible  = multi;
        RefreshCoverCounter();
    }

    private void RefreshCoverCounter()
    {
        if (_coverItems.Count == 0)
        {
            CatalogCoverCounter.Text = string.Empty;
            return;
        }
        var item = _coverItems[_coverIndex];
        CatalogCoverCounter.Text = $"{item.Label} · {_coverIndex + 1} of {_coverItems.Count}";
    }

    private void OnCatalogCoverPrev(object? sender, RoutedEventArgs e)
    {
        if (_coverItems.Count == 0) return;
        ShowCoverItem((_coverIndex - 1 + _coverItems.Count) % _coverItems.Count);
    }

    private void OnCatalogCoverNext(object? sender, RoutedEventArgs e)
    {
        if (_coverItems.Count == 0) return;
        ShowCoverItem((_coverIndex + 1) % _coverItems.Count);
    }

    // ── Extras gallery ────────────────────────────────────────────────────────

    private void BuildExtras(LibraryEntry entry)
    {
        _extrasBitmap?.Dispose();
        _extrasBitmap = null;
        _extrasItems.Clear();
        _extrasItems.AddRange(_mediaDiscovery.FindExtrasItems(entry));
    }

    private void ShowExtrasItem(int index)
    {
        CatalogExtrasImage.Source = null;
        _extrasBitmap?.Dispose();
        _extrasBitmap = null;

        if (_extrasItems.Count == 0)
        {
            _extrasIndex = 0;
            CatalogExtrasImage.IsVisible  = false;
            CatalogExtrasNoItem.IsVisible = true;
            CatalogExtrasPrev.IsVisible   = false;
            CatalogExtrasNext.IsVisible   = false;
            CatalogExtrasCounter.Text     = string.Empty;
            return;
        }

        _extrasIndex = Math.Clamp(index, 0, _extrasItems.Count - 1);
        var item = _extrasItems[_extrasIndex];

        _extrasBitmap = CoverLoader.TryLoad(item.Path);
        CatalogExtrasImage.Source    = _extrasBitmap;
        CatalogExtrasImage.IsVisible = _extrasBitmap is not null;
        CatalogExtrasNoItem.IsVisible = _extrasBitmap is null;

        bool multi = _extrasItems.Count > 1;
        CatalogExtrasPrev.IsVisible  = multi;
        CatalogExtrasNext.IsVisible  = multi;
        CatalogExtrasCounter.Text    = $"{item.Label} · {_extrasIndex + 1} of {_extrasItems.Count}";
    }

    private void OnCatalogExtrasPrev(object? sender, RoutedEventArgs e)
    {
        if (_extrasItems.Count == 0) return;
        ShowExtrasItem((_extrasIndex - 1 + _extrasItems.Count) % _extrasItems.Count);
    }

    private void OnCatalogExtrasNext(object? sender, RoutedEventArgs e)
    {
        if (_extrasItems.Count == 0) return;
        ShowExtrasItem((_extrasIndex + 1) % _extrasItems.Count);
    }

    // ── Manuals ───────────────────────────────────────────────────────────────

    private void BuildManuals(LibraryEntry entry)
    {
        _manualPaths.Clear();
        _manualPaths.AddRange(_mediaDiscovery.FindManualPaths(entry));
    }

    private void RefreshManualButtons()
    {
        CatalogManualButtons.Children.Clear();
        if (_manualPaths.Count == 0)
        {
            CatalogManualNoItem.IsVisible = true;
            return;
        }
        CatalogManualNoItem.IsVisible = false;
        for (int i = 0; i < _manualPaths.Count; i++)
        {
            var path = _manualPaths[i];
            var btn = new Button
            {
                Content         = (i + 1).ToString(),
                Width           = 32,
                Height          = 32,
                Margin          = new Avalonia.Thickness(0, 0, 6, 6),
                FontSize        = 12,
                FontWeight      = Avalonia.Media.FontWeight.SemiBold,
                Foreground      = new SolidColorBrush(Color.Parse("#9FA4FF")),
                Background      = new SolidColorBrush(Color.Parse("#1A1A2E")),
                BorderBrush     = new SolidColorBrush(Color.Parse("#2A2A50")),
                BorderThickness = new Avalonia.Thickness(1),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            };
            btn.Click += (_, _) =>
            {
                if (!File.Exists(path)) return;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Manual] Open failed: {ex.Message}"); }
            };
            CatalogManualButtons.Children.Add(btn);
        }
    }

    private static void SetMetaField(StackPanel cell, TextBlock valueText, string value)
    {
        cell.IsVisible = value.Length > 0;
        valueText.Text = value;
    }

    private void OnCatalogOpenInLibrary(object? sender, RoutedEventArgs e)
    {
        // Sync library context to match catalog selection, then navigate
        var platform = CatalogContextPlatform.SelectedItem as string;
        var datLine  = CatalogContextDatLine.SelectedItem  as string;

        if (platform is not null)
        {
            // Sync library selectors
            if (LibraryContextPlatform.Items.Cast<string>().Contains(platform))
                LibraryContextPlatform.SelectedItem = platform;
            if (datLine is not null && LibraryContextDatLine.Items.Cast<string>().Contains(datLine))
                LibraryContextDatLine.SelectedItem = datLine;
        }

        SetActive(NavLibrary);

        // Scroll to selected entry in library list
        if (_catalogSelected is not null)
        {
            var match = (LibraryList.ItemsSource as System.Collections.Generic.IEnumerable<LibraryEntry>)
                ?.FirstOrDefault(e2 => e2.ReleaseId == _catalogSelected.ReleaseId);
            if (match is not null)
            {
                LibraryList.SelectedItem = match;
                LibraryList.ScrollIntoView(match);
            }
        }
    }

    // ── Catalog edit metadata ─────────────────────────────────────────────────

    private async void OnCatalogEditMetadata(object? sender, RoutedEventArgs e)
    {
        var entry = _catalogSelected;
        if (entry is null) return;

        var dialog = new EditMetadataDialog(entry, _metadataMappings);
        var result = await dialog.ShowDialog<EditMetadataResult?>(this);
        if (result is null) return;

        // Mutate the in-memory entry in place — the same object is referenced by
        // _activeLibraryDatasets, _catalogDatasetEntries, and _filteredCatalogEntries,
        // so the update propagates to all three caches without a full DB reload.
        entry.Metadata = result.Metadata;
        entry.Region   = result.Region;

        System.Diagnostics.Debug.WriteLine(
            $"[EditMetadata] saved: releaseId={entry.ReleaseId} region={result.Region} " +
            $"changed=[{string.Join(",", result.ChangedFields)}]");

        RebuildCatalogList(preserveSelection: true);
        UpdateCatalogHero(_catalogSelected ?? entry);
    }

    // ── Catalog merge metadata ────────────────────────────────────────────────

    private async void OnCatalogMergeMetadata(object? sender, RoutedEventArgs e)
    {
        var entry = _catalogSelected;
        if (entry is null) return;

        var dialog = new MergeMetadataDialog(entry);
        var result = await dialog.ShowDialog<MergeMetadataResult?>(this);
        if (result is null) return;

        entry.Metadata = result.Metadata;

        System.Diagnostics.Debug.WriteLine(
            $"[MergeMetadata] applied: releaseId={entry.ReleaseId} " +
            $"fields=[{string.Join(",", result.AppliedFields)}]");

        RebuildCatalogList(preserveSelection: true);
        UpdateCatalogHero(_catalogSelected ?? entry);
    }

    // ── Catalog scrape ────────────────────────────────────────────────────────

    private async void OnCatalogScrape(object? sender, RoutedEventArgs e)
    {
        var entry = _catalogSelected;
        if (entry is null) return;

        // Read credentials so dialog can show availability before the user commits.
        var username    = _catalog.GetSetting("screenscraper_username");
        var password    = _catalog.GetSetting("screenscraper_password");
        var devId       = _catalog.GetSetting("screenscraper_dev_id");
        var devPassword = _catalog.GetSetting("screenscraper_dev_password");
        var softName    = _catalog.GetSetting("screenscraper_softname").Trim();

        // ── Provider selection ───────────────────────────────────────────────
        var hasCachePackages = _catalog.HasUsableCachePackages();
        var registry    = new AmpLocalRegistryService(_dataDir);
        var ampPackages = registry.ListPackagesForScope(
            entry.HardwareFamilyId,
            entry.DatLineId);
        var providerDialog = new ScraperProviderDialog(
        [
            new ScraperProviderInfo(
                ArkadiaProviders.ScreenScraper, ArkadiaProviders.ScreenScraperDisplayName,
                ScraperProviderInfo.IsScreenScraperConfigured(username, password, devId, devPassword)),
            new ScraperProviderInfo(
                ArkadiaProviders.ScreenScraperCache, ArkadiaProviders.ScreenScraperCacheDisplayName,
                hasCachePackages,
                UnavailableText: hasCachePackages ? "Available" : "Needs build"),
            new ScraperProviderInfo(
                ArkadiaProviders.ArkadiaMediaPack, ArkadiaProviders.ArkadiaMediaPackDisplayName,
                ampPackages.Count > 0,
                UnavailableText: "No local packages for this system"),
        ]);
        var selectedProvider = await providerDialog.ShowDialog<string?>(this);
        if (selectedProvider is null) return;

        // ── Resolve platform ─────────────────────────────────────────────────
        var family       = _catalog.GetHardwareFamily(entry.HardwareFamilyId);
        var scrapeId     = family?.ScrapeSystemId is { Length: > 0 } s ? s : entry.HardwareFamilyId;

        // ── ScreenScraper Cache path ─────────────────────────────────────────
        if (selectedProvider == ArkadiaProviders.ScreenScraperCache)
        {
            var initialCacheQuery = ScrapeReviewDialog.BuildInitialQuery(entry.CatalogTitle, entry.Name);
            var searchSvc  = new Arkadia.Data.ScreenScraperCacheSearchService(_catalog);
            var cacheDialog = new CacheReviewDialog(searchSvc, initialCacheQuery, scrapeId);
            var candidate   = await cacheDialog.ShowDialog<Arkadia.Data.ScreenScraperCacheCandidate?>(this);
            if (candidate is null) return;

            CatalogScrapeBtn.IsEnabled = false;
            try
            {
                _cacheImport ??= new Arkadia.Providers.ScreenScraperCacheImportService(_dataDir, _catalog);
                var importProgress = new Progress<string>(msg => SetScrapeStatus(msg, "#888899"));
                var cacheSummary = await _cacheImport.ImportAsync(
                    entry, candidate, _metadataMappings, importProgress);

                SetScrapeStatus("Review metadata…", "#888899");
                var mergeDialog = new MergeMetadataDialog(entry, Arkadia.Providers.ScreenScraperCacheImportService.ProviderId);
                var mergeResult = await mergeDialog.ShowDialog<MergeMetadataResult?>(this);

                if (mergeResult is not null)
                    entry.Metadata = mergeResult.Metadata;

                RebuildCatalogList(preserveSelection: true);
                UpdateCatalogHero(entry);

                string metaMsg;
                if (mergeResult is not null)
                    metaMsg = "metadata applied";
                else if (!cacheSummary.ProposalsSaved)
                    metaMsg = "metadata skipped: no fields extracted";
                else
                    metaMsg = "metadata skipped";
                var mediaMsg = cacheSummary.MediaExtracted > 0
                    ? $" + {cacheSummary.MediaExtracted} media file{(cacheSummary.MediaExtracted == 1 ? "" : "s")}"
                    : "";
                SetScrapeStatus(
                    $"Cache import — {metaMsg}{mediaMsg}.",
                    mergeResult is not null ? "#4CAF50" : "#888899");
            }
            catch (OperationCanceledException)
            {
                SetScrapeStatus("Cache import cancelled.", "#888899");
            }
            catch (Exception ex)
            {
                SetScrapeStatus($"Cache import failed: {ex.Message}", "#EF5350");
            }
            finally
            {
                CatalogScrapeBtn.IsEnabled = true;
            }
            return;
        }

        // ── Arkadia Media Pack path ──────────────────────────────────────────
        if (selectedProvider == ArkadiaProviders.ArkadiaMediaPack)
        {
            var picker = new AmpPickerDialog(ampPackages);
            var picked = await picker.ShowDialog<AmpLocalPackageInfo?>(this);
            if (picked is null) return;

            SetScrapeStatus("Verifying AMP package…", "#888899");
            var verified = await Task.Run(() => registry.VerifyPackage(picked.FilePath));
            if (verified.HasErrors)
            {
                await new InfoDialog(
                    "Cannot Import",
                    $"Package failed verification: {verified.Status}")
                    .ShowDialog(this);
                return;
            }

            if (verified.HasWarnings)
                SetScrapeStatus("Package has warnings. Proceeding…", "#E0A040");

            var ampReader = new AmpPackageReaderService();
            if (!ampReader.TryReadReleases(verified.FilePath, out var releases))
            {
                SetScrapeStatus("Failed to read AMP package releases.", "#EF5350");
                return;
            }

            var matchResult = AmpReleaseMatcher.FindRelease(
                releases, entry.ReleaseId, entry.Name);

            if (matchResult.Kind == AmpReleaseMatchKind.None || matchResult.Release is null)
            {
                SetScrapeStatus(
                    $"No matching release found in package for '{entry.Name}'.",
                    "#FFD54F");
                return;
            }

            CatalogScrapeBtn.IsEnabled = false;
            try
            {
                _ampImport ??= new AmpLocalPackageImportService(_dataDir);
                var importProgress = new Progress<string>(msg => SetScrapeStatus(msg, "#888899"));
                var ampSummary = await _ampImport.ImportAsync(
                    entry,
                    verified.FilePath,
                    matchResult.Release,
                    _metadataMappings,
                    matchKind: matchResult.Kind,
                    progress: importProgress);

                SetScrapeStatus("Review metadata…", "#888899");
                var mergeDialog = new MergeMetadataDialog(
                    entry, ArkadiaProviders.ArkadiaMediaPack);
                var mergeResult = await mergeDialog.ShowDialog<MergeMetadataResult?>(this);
                if (mergeResult is not null)
                    entry.Metadata = mergeResult.Metadata;

                RebuildCatalogList(preserveSelection: true);
                UpdateCatalogHero(entry);

                var metaMsg = mergeResult is not null
                    ? "metadata applied"
                    : !ampSummary.ProposalsSaved
                        ? "metadata skipped: no fields extracted"
                        : "metadata skipped";
                var mediaMsg = ampSummary.MediaFilesExtracted > 0
                    ? $" + {ampSummary.MediaFilesExtracted} media file{(ampSummary.MediaFilesExtracted == 1 ? "" : "s")}"
                    : "";
                SetScrapeStatus(
                    $"AMP import — {metaMsg}{mediaMsg}.",
                    mergeResult is not null ? "#4CAF50" : "#888899");
            }
            catch (OperationCanceledException)
            {
                SetScrapeStatus("AMP import cancelled.", "#888899");
            }
            catch (Exception ex)
            {
                SetScrapeStatus($"AMP import failed: {ex.Message}", "#EF5350");
            }
            finally
            {
                CatalogScrapeBtn.IsEnabled = true;
            }
            return;
        }

        // ── Online ScreenScraper path ────────────────────────────────────────
        if (selectedProvider != ArkadiaProviders.ScreenScraper) return;

        if (softName.Length == 0)
        {
            SetScrapeStatus("ScreenScraper Softname is required. Configure it in Providers \u2192 ROM Scrapers.", "#FFD54F");
            return;
        }

        var platformName = family?.Name is { Length: > 0 } n ? n : entry.HardwareFamilyId;

        if (!Arkadia.Providers.ScreenScraperClient.TryResolveSystemId(scrapeId, out _))
        {
            SetScrapeStatus($"No ScreenScraper system ID mapped for system '{scrapeId}'.", "#FFD54F");
            return;
        }

        // ── Candidate review dialog ──────────────────────────────────────────
        var isMame = string.Equals(entry.Authority, "mame",  StringComparison.OrdinalIgnoreCase)
                           || string.Equals(entry.Authority, "fbneo", StringComparison.OrdinalIgnoreCase);

        var initialQuery = ScrapeReviewDialog.BuildInitialQuery(entry.CatalogTitle, entry.Name);
        var reviewDialog = new ScrapeReviewDialog(
            devId, devPassword, username, password,
            scrapeId, platformName, initialQuery,
            entry.Name, isMame, softName);
        var reviewResult = await reviewDialog.ShowDialog<ScrapeReviewResult?>(this);
        if (reviewResult is null) return;

        CatalogScrapeBtn.IsEnabled = false;

        try
        {
            // Direct fallback path: result already fetched by ROM/DAT name lookup in dialog.
            // Candidate path: fetch full details from ScreenScraper by provider game ID.
            Arkadia.Providers.ScreenScraperResult? result;
            if (reviewResult.IsDirectResult)
            {
                result = reviewResult.DirectResult;
                SetScrapeStatus("Applying result from ScreenScraper…", "#888899");
            }
            else
            {
                SetScrapeStatus("Fetching details from ScreenScraper…", "#888899");
                result = await Arkadia.Providers.ScreenScraperClient.FetchDetailsByGameIdAsync(
                    devId, devPassword, username, password, reviewResult.Candidate!, softName: softName);
            }

            if (result is null)
            {
                SetScrapeStatus("No details found for selected candidate.", "#FFD54F");
                return;
            }

            // ── Import proposals + payload + media via service ───────────────
            var importProgress = new Progress<string>(msg => SetScrapeStatus(msg, "#888899"));
            var summary = await _scrapeImport.ImportAsync(
                entry, result, _metadataMappings, importProgress);

            // ── Open Merge Metadata dialog ───────────────────────────────────
            SetScrapeStatus("Review metadata…", "#888899");
            var mergeDialog = new MergeMetadataDialog(entry, ArkadiaProviders.ScreenScraper);
            var mergeResult = await mergeDialog.ShowDialog<MergeMetadataResult?>(this);

            if (mergeResult is not null)
                entry.Metadata = mergeResult.Metadata;

            RebuildCatalogList(preserveSelection: true);
            UpdateCatalogHero(entry);

            var parts = new List<string>();
            if (summary.Covers      > 0) parts.Add($"{summary.Covers} cover{(summary.Covers           > 1 ? "s" : "")}");
            if (summary.Screenshots > 0) parts.Add($"{summary.Screenshots} screenshot{(summary.Screenshots > 1 ? "s" : "")}");
            if (summary.Fanart      > 0) parts.Add($"{summary.Fanart} fanart");
            if (summary.GotVideo)        parts.Add("video");
            if (summary.Logos       > 0) parts.Add($"{summary.Logos} logo{(summary.Logos           > 1 ? "s" : "")}");
            if (summary.Marquees    > 0) parts.Add($"{summary.Marquees} marquee{(summary.Marquees       > 1 ? "s" : "")}");
            if (summary.Flyers      > 0) parts.Add($"{summary.Flyers} flyer{(summary.Flyers          > 1 ? "s" : "")}");
            if (summary.Manuals     > 0) parts.Add($"{summary.Manuals} manual{(summary.Manuals         > 1 ? "s" : "")}");

            var metaMsg = mergeResult is not null ? "metadata applied" : "metadata skipped";
            var msg = parts.Count > 0
                ? $"Scraped — {metaMsg} + {string.Join(" + ", parts)}."
                : $"Scraped — {metaMsg} (no media available).";
            SetScrapeStatus(msg, mergeResult is not null ? "#4CAF50" : "#888899");
        }
        catch (Arkadia.Providers.ScreenScraperRateLimitException)
        {
            SetScrapeStatus("Rate limited by ScreenScraper. Please wait before retrying.", "#EF5350");
        }
        catch (OperationCanceledException)
        {
            SetScrapeStatus("Scrape cancelled.", "#888899");
        }
        catch (Exception ex)
        {
            SetScrapeStatus($"Scrape failed: {ex.Message}", "#EF5350");
        }
        finally
        {
            CatalogScrapeBtn.IsEnabled = true;
        }
    }

    private void SetScrapeStatus(string message, string color)
    {
        CatalogScrapeStatus.Text      = message;
        CatalogScrapeStatus.Foreground = new SolidColorBrush(Color.Parse(color));
        CatalogScrapeStatus.IsVisible  = true;
    }

    // ── Ingestion ─────────────────────────────────────────────────────────────

    private async void OnIngestDatLine(DatLineInfo info)
    {
        if (info.CatalogId is null || info.CatalogPlatformId is null || info.DataStorePath.Length == 0)
            return;

        if (info.TransformStrategyType == "none")
        {
            await new InfoDialog("Ingest Files", "Configure this DAT first.").ShowDialog(this);
            return;
        }

        var platformId        = info.CatalogPlatformId;
        var datLineId         = info.CatalogId;
        var absDbPath         = Path.Combine(_dataDir, info.DataStorePath);
        var datLineRecord     = _catalog.LoadDatLines().FirstOrDefault(dl => dl.Id == datLineId);
        var storageStrategyId = datLineRecord?.StorageStrategyId ?? "";

        // ── Preflight: verify required tools are present ──────────────────────
        {
            var pfTransforms = _catalog.LoadTransforms();
            var pfTools      = _catalog.LoadTools().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var pfAppRoot    = AppContext.BaseDirectory;

            var requiredToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var activeXf = pfTransforms.FirstOrDefault(t => t.Id == storageStrategyId);
            if (activeXf?.ToolId is { Length: > 0 } sid)
                requiredToolIds.Add(sid);

            if (datLineRecord?.TransformStrategyType == "file_extension")
            {
                foreach (var m in _catalog.LoadExtensionMappings(datLineId))
                {
                    if (m.IsDiscard || string.IsNullOrEmpty(m.TransformId)) continue;
                    var xf = pfTransforms.FirstOrDefault(t => t.Id == m.TransformId);
                    if (xf?.ToolId is { Length: > 0 } eid)
                        requiredToolIds.Add(eid);
                }
            }

            var missingTools = requiredToolIds
                .Where(id => pfTools.TryGetValue(id, out var tool) &&
                             !File.Exists(Path.Combine(pfAppRoot, "tools", tool.FolderName, tool.ExecutableName)))
                .ToList();

            if (missingTools.Count > 0)
            {
                await new InfoDialog(
                    "Missing Tools",
                    $"The following tools are required for this ingestion but are not installed:\n{string.Join(", ", missingTools)}\n\nPlease install the missing tools before ingesting."
                ).ShowDialog(this);
                return;
            }
        }

        var progressDialog = new IngestionProgressDialog($"Ingest Files — {info.Name}");
        var progress       = new Progress<IngestionProgress>(p => progressDialog.Update(p));
        IngestionResult? ingestResult = null;

        var fileHandling = datLineRecord?.FileHandling ?? "archives_pre_extraction";
        var workTask = System.Threading.Tasks.Task.Run(() =>
            ingestResult = RunIngestionWork(platformId, datLineId, absDbPath, storageStrategyId, progress,
                fileHandling: fileHandling));

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

        if (ingestResult.Error is null && ingestResult.ReleasesPresent > 0)
        {
            RebuildLibraryDatasets();
            ResolveFlagImages();
            RefreshSystemsKeepSelection(platformId);
            RefreshPending();
        }

        if (ingestResult.Error is null && (ingestResult.FilesCopied > 0 || ingestResult.FilesSkipped > 0 || ingestResult.ReleasesPresent > 0))
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
        Func<string, bool>?          shouldIngest        = null,
        string?                      incomingDirOverride = null,
        string                       fileHandling        = "archives_pre_extraction")
    {
        var result  = new IngestionResult();
        var appRoot = AppContext.BaseDirectory;

        var incomingDir = incomingDirOverride ?? Path.Combine(appRoot, "incoming-roms", platformId);
        var stagingRoot = Path.Combine(appRoot, "staging",        platformId, datLineId);
        var sourceRoot  = Path.Combine(appRoot, "source",         platformId, datLineId);
        var skipDir     = Path.Combine(appRoot, "incoming-skip");

        Directory.CreateDirectory(incomingDir);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(skipDir);

        // ── Pre-Ingest: Extract archives ──────────────────────────────────────
        if (fileHandling == "archives_pre_extraction")
        {
            progress.Report(new IngestionProgress { PhaseText = "Pre-ingest: extracting archives…" });
            RunPreIngest(incomingDir, result, progress);
        }

        // Build a set of archive container paths that were successfully extracted.
        // These must not enter the scan / hash / match / skip pipeline — their
        // lifecycle is owned exclusively by Phase 9 archive cleanup.
        var extractedArchiveSet = IngestArchiveContainerFilter.BuildExtractedSet(result.ExtractedArchiveInfos);

        // Reverse map: every extracted file path → the archive info it came from.
        // Used by Phase 9 to associate source files with their origin archive.
        var extractedFileToArchive = new Dictionary<string, Ingestion.ExtractedArchiveInfo>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var info in result.ExtractedArchiveInfos)
            foreach (var f in info.ExtractedFiles)
                extractedFileToArchive[f] = info;

        // ── Phase 1: Scan ─────────────────────────────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Scanning incoming files…" });

        var sourceFiles = Directory.GetFiles(incomingDir, "*", SearchOption.AllDirectories)
            .Where(f => !IngestArchiveContainerFilter.IsExtractedArchive(f, extractedArchiveSet))
            .ToList();
        result.FilesScanned = sourceFiles.Count;

        if (sourceFiles.Count == 0)
            return result;

        var store = new DatLineStore(absDbPath);

        // ── Build hash indexes from non-outdated release files ─────────────────
        var releases = store.LoadReleases()
            .Where(r => r.Status != "outdated")
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var allReleaseFiles = store.LoadAllReleaseFiles();

        // Per-archive cleanup tracking (Phase 9).
        // successfulReleaseIds: pre-seeded with releases already present before this run
        // (covers re-ingest / allTargetsSatisfied cases), then extended in Phase 7.
        var successfulReleaseIds    = new HashSet<string>(StringComparer.Ordinal);
        var archiveTouchedReleases  = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unmatchedExtractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rid, rel) in releases)
            if (rel.Status == "present")
                successfulReleaseIds.Add(rid);

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

        // Build archive → release associations and unmatched-file set from the completed copyPlan.
        // Done here (after hashing) so allTargetsSatisfied files are also covered.
        foreach (var (srcPath, destinations) in copyPlan)
        {
            if (!extractedFileToArchive.TryGetValue(srcPath, out var archInfo)) continue;
            if (!archiveTouchedReleases.TryGetValue(archInfo.ArchivePath, out var touchSet))
                archiveTouchedReleases[archInfo.ArchivePath] = touchSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rid, _) in destinations)
                touchSet.Add(rid);
        }
        foreach (var filePath in extractedFileToArchive.Keys)
            if (!copyPlan.ContainsKey(filePath))
                unmatchedExtractedFiles.Add(filePath);

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

                // Source exists but release is not yet present → a previous transform failed.
                // Leave the target unsatisfied so the incoming file re-triggers staging and
                // a fresh transform attempt, rather than being silently deleted as a duplicate.
                if (releases.TryGetValue(releaseId, out var srcRel) && srcRel.Status != "present")
                    continue;

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
        // transformFailedReleases: releases where at least one file's transform failed — their
        //   contributing source files must not be deleted from incoming-roms.
        var successfullyCopied      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedFromIncoming       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allTargetsSatisfied     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedReleaseIds      = new HashSet<string>(StringComparer.Ordinal);
        var transformFailedReleases = new HashSet<string>(StringComparer.Ordinal);
        // incompleteReleases: releases where staging completeness check failed (e.g. orphan .bin, missing .cue).
        // These must also block archive deletion so the ZIP is preserved for recovery.
        var incompleteReleases      = new HashSet<string>(StringComparer.Ordinal);
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

            bool anyFailed           = false;
            bool wasMovedFromIncoming = false;

            foreach (var (releaseId, romName) in pending)
            {
                var relName    = releases.TryGetValue(releaseId, out var rel) ? rel.Name : releaseId;
                var safeFolder = SafeFileName(relName);
                var stagingDir = Path.Combine(stagingRoot, safeFolder);
                var destPath   = Path.Combine(stagingDir, romName);

                Directory.CreateDirectory(stagingDir);

                try
                {
                    // Move when this is the sole remaining target and paths share a volume
                    // (same-volume File.Move is an atomic NTFS rename — no byte copy).
                    // Copy otherwise (fan-out to multiple releases, or cross-volume).
                    StagingHelpers.StageFile(srcPath, destPath, pending.Count, out var stageOp);

                    // Size sanity check only for copies (moves are atomic and integrity-guaranteed).
                    if (stageOp != "stage-moved" && new FileInfo(destPath).Length != srcInfo.Length)
                        throw new IOException($"Size mismatch after copy for {romName}");

                    if (stageOp == "stage-moved") wasMovedFromIncoming = true;

                    // Mark this target satisfied so no later file re-copies it.
                    satisfiedTargets.Add($"{releaseId}|{romName}");
                    affectedReleaseIds.Add(releaseId);
                    result.FilesCopied++;
                    copyCount++;

                    var op = new IngestionOperation(
                        srcInfo.Name, stageOp,
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
            {
                if (wasMovedFromIncoming)
                    movedFromIncoming.Add(srcPath);
                else
                    successfullyCopied.Add(srcPath);
            }
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

        // Load transform strategy for this DAT line (file_extension / release_folder dispatch)
        var datLineStrategyType = "none";
        var extMappingDict      = new Dictionary<string, ExtensionTransformMapping>(StringComparer.OrdinalIgnoreCase);
        TransformRecord? folderXform = null;
        ToolRecord?      folderTool  = null;
        {
            var dlRecord = _catalog.LoadDatLines().FirstOrDefault(dl => dl.Id == datLineId);
            if (dlRecord?.TransformStrategyType == "file_extension")
            {
                datLineStrategyType = "file_extension";
                foreach (var m in _catalog.LoadExtensionMappings(datLineId))
                    extMappingDict[m.FileExtension] = m;
            }
            else if (dlRecord?.TransformStrategyType == "release_folder" &&
                     dlRecord.FolderTransformId.Length > 0)
            {
                datLineStrategyType = "release_folder";
                folderXform = allTransforms.FirstOrDefault(t => t.Id == dlRecord.FolderTransformId);
                folderTool  = folderXform?.ToolId.Length > 0
                    ? allTools.FirstOrDefault(t => t.Id == folderXform.ToolId)
                    : null;
            }
            else if (dlRecord?.TransformStrategyType == "release_shape")
            {
                datLineStrategyType = "release_shape";
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

            if (!complete)
            {
                incompleteReleases.Add(releaseId);
                var missingFiles = expectedFiles
                    .Where(f => !File.Exists(Path.Combine(stagingDir, f.RomName)))
                    .Select(f => f.RomName)
                    .ToList();
                var incompleteOp = new IngestionOperation(
                    release.Name,
                    "incomplete-skipped",
                    $"missing: {string.Join(", ", missingFiles)}");
                result.Operations.Add(incompleteOp);
                progress.Report(new IngestionProgress { NewOperation = incompleteOp });
                continue;
            }

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
                var failOp = new IngestionOperation(release.Name, "source-promoted-failed", ex.Message);
                result.Operations.Add(failOp);
                progress.Report(new IngestionProgress { NewOperation = failOp });
            }

            if (!sourceOk) continue;

            var archOp = new IngestionOperation(
                release.Name, "source-promoted",
                $"source/{platformId}/{datLineId}/{safeFolder}");
            result.Operations.Add(archOp);
            progress.Report(new IngestionProgress { NewOperation = archOp });

            // ── Dispatch: file-oriented or folder-oriented processor ──────────
            if (datLineStrategyType == "release_folder")
            {
                if (folderXform is { IsFolderOriented: true, OutputIsFile: true })
                {
                    ProcessFolderOrientedRelease(
                        releaseId, safeFolder, sourceDir,
                        folderXform, folderTool,
                        appRoot, platformId, datLineId,
                        now, store, result, progress, transformFailedReleases);
                }
                else if (folderXform is { IsFolderOriented: true, OutputIsFolder: true })
                {
                    ProcessFolderOrientedFolderRelease(
                        releaseId, safeFolder, sourceDir, expectedFiles,
                        folderXform,
                        appRoot, platformId, datLineId,
                        now, store, result, progress, transformFailedReleases);
                }
                else
                {
                    transformFailedReleases.Add(releaseId);
                    var failOp = new IngestionOperation(release.Name, "transform-config-error",
                        folderXform is null
                            ? "No folder-oriented transform is configured for this DAT line."
                            : $"Transform '{folderXform.Name}' is not folder-oriented.");
                    result.Operations.Add(failOp);
                    progress.Report(new IngestionProgress { NewOperation = failOp });
                }
            }
            else if (datLineStrategyType == "release_shape")
            {
                ProcessReleaseShapeOrientedRelease(
                    releaseId, safeFolder, sourceDir, expectedFiles,
                    allTransforms, allTools,
                    appRoot, platformId, datLineId,
                    release.Status,
                    now, store, result, progress, transformFailedReleases);
            }
            else
            {
                // ── Per-file: verify source + persist provenance + transform ──
                ProcessFileOrientedRelease(
                    releaseId, safeFolder, sourceDir, expectedFiles,
                    datLineStrategyType, extMappingDict,
                    activeXform, activeTool, allTransforms, allTools, storageStrategyId,
                    appRoot, platformId, datLineId,
                    now, store, result, progress, transformFailedReleases);
            }

            if (!transformFailedReleases.Contains(releaseId))
            {
                store.UpdateReleaseStatus(releaseId, "present");
                result.ReleasesPresent++;
                successfulReleaseIds.Add(releaseId);
            }
        }

        // ── Phase 8: Source file handling ─────────────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Handling source files…" });

        foreach (var srcPath in sourceFiles)
        {
            var fileName = Path.GetFileName(srcPath);

            if (successfullyCopied.Contains(srcPath))
            {
                // Guard: if any target release had a transform failure, leave the source
                // in incoming-roms so the user can retry after fixing the tool/config.
                bool hasTransformFailure = copyPlan.TryGetValue(srcPath, out var targets) &&
                    targets.Any(t => transformFailedReleases.Contains(t.ReleaseId));
                if (hasTransformFailure)
                    continue;

                // All pending targets were copied and transformed successfully → delete source.
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
            else if (movedFromIncoming.Contains(srcPath))
            {
                // File was moved (not copied) to staging in Phase 6 — already gone from
                // incoming-roms.  If a transform failed, the file is preserved in source/
                // and will be picked up on the next ingest run via the source-present/
                // derived-missing retry path.  Nothing to delete here.
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

        // ── Finalise result counters ──────────────────────────────────────────
        result.TransformsFailed    = transformFailedReleases.Count;
        result.ReleasesIncomplete  = incompleteReleases.Count;

        // ── Phase 9: Per-archive cleanup ──────────────────────────────────────
        // Each archive is deleted only when every release it contributed to succeeded.
        // Archives linked to any incomplete or failed release are preserved individually,
        // so one bad ZIP does not block cleanup of unrelated successful archives.
        if (result.ExtractedArchiveInfos.Count > 0)
        {
            progress.Report(new IngestionProgress { PhaseText = "Cleaning up extracted archives…" });
            var incomingLabel = Path.GetFileName(Path.GetDirectoryName(incomingDir)) ?? "incoming";

            var decisions = Ingestion.ArchiveCleanupPlanner.Plan(
                result.ExtractedArchiveInfos,
                archiveTouchedReleases,
                successfulReleaseIds,
                incompleteReleases,
                transformFailedReleases,
                unmatchedExtractedFiles);

            foreach (var decision in decisions)
            {
                var archiveName = Path.GetFileName(decision.Archive.ArchivePath);
                if (decision.ShouldDelete)
                {
                    try
                    {
                        File.Delete(decision.Archive.ArchivePath);
                        var delOp = new IngestionOperation(archiveName, "archive-deleted", incomingLabel);
                        result.Operations.Add(delOp);
                        progress.Report(new IngestionProgress { NewOperation = delOp });
                        result.FilesDeletedFromIncoming++;
                    }
                    catch
                    {
                        var failOp = new IngestionOperation(
                            archiveName, "archive-delete-failed", "could not remove archive");
                        result.Operations.Add(failOp);
                        progress.Report(new IngestionProgress { NewOperation = failOp });
                    }
                }
                else
                {
                    var presOp = new IngestionOperation(
                        archiveName, "archive-preserved", decision.Reason);
                    result.Operations.Add(presOp);
                    progress.Report(new IngestionProgress { NewOperation = presOp });
                }
            }
        }

        // ── Post-Ingest: Remove empty directories ─────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Post-ingest: cleaning empty directories…" });
        RunPostIngest(incomingDir);

        return result;
    }

    // ── File-oriented release processor ──────────────────────────────────────
    // Processes one release worth of files: strategy dispatch, source hashing,
    // transform execution, artifact ingestion, and source cleanup.
    // Called from the outer release loop in RunIngestionWork.
    // Future: a parallel FolderOrientedRelease processor will be plugged in at
    // the same call site when the release_folder strategy is implemented.

    private void ProcessFileOrientedRelease(
        string                                        releaseId,
        string                                        safeFolder,
        string                                        sourceDir,
        List<ReleaseFileRecord>                       expectedFiles,
        string                                        datLineStrategyType,
        Dictionary<string, ExtensionTransformMapping> extMappingDict,
        TransformRecord                               activeXform,
        ToolRecord?                                   activeTool,
        List<TransformRecord>                         allTransforms,
        List<ToolRecord>                              allTools,
        string                                        storageStrategyId,
        string                                        appRoot,
        string                                        platformId,
        string                                        datLineId,
        DateTime                                      now,
        DatLineStore                                  store,
        IngestionResult                               result,
        IProgress<IngestionProgress>                  progress,
        HashSet<string>                               transformFailedReleases)
    {
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
                var derivedSizeBytes = new FileInfo(destPath).Length;
                store.IngestDerivedArtifact(
                    contentIdentityKey: ck,
                    sourceArtifactId:   srcArtifactId,
                    storageStrategyId:  effectiveStratId,
                    fileName:           destName,
                    relativePath:       relPath,
                    derivedSizeBytes:   derivedSizeBytes,
                    hashedDerivedSha1:  hashedDerivedSha1,
                    hashedDerivedMd5:   hashedDerivedMd5,
                    hashedDerivedCrc32: hashedDerivedCrc32,
                    archiveTier:        effectiveXform.ArchiveTier);

                // ── 7. Link release → content identity ────────────────────
                store.SaveReleaseContentLink(new Data.ReleaseContentLinkRecord
                {
                    Id                 = Guid.NewGuid().ToString("N"),
                    ReleaseId          = releaseId,
                    ContentIdentityKey = ck,
                    CreatedAtUtc       = now,
                });

                // ── 8. Remove source file — derived artifact is valid ──────
                // Only delete when the derived artifact was actually produced
                // with non-zero size; leave the source intact on any failure path.
                if (derivedSizeBytes > 0)
                {
                    try { File.Delete(sourceFilePath); }
                    catch { /* best-effort; leave file if OS denies deletion */ }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Transform failures must never block the rest of ingestion.
                transformFailedReleases.Add(releaseId);
                var failOp = new IngestionOperation(f.RomName, "transform-failed", ex.Message);
                result.Operations.Add(failOp);
                progress.Report(new IngestionProgress { NewOperation = failOp });
            }
        }
    }

    // ── Folder-oriented release processor — output_kind = file ──────────────
    // Processes one promoted release folder as a single unit:
    //   source/{platform}/{datLine}/{safeFolder}/ → archive/.../{safeFolder}{ext}
    // Provenance: content_identity_key = "release:{releaseId}", source_artifact_id = "".
    // Called from the outer release loop when datLineStrategyType == "release_folder"
    // and the configured folder transform has output_kind = "file" (e.g. ZIP Compression).

    private void ProcessFolderOrientedRelease(
        string                       releaseId,
        string                       safeFolder,
        string                       sourceDir,
        TransformRecord              folderXform,
        ToolRecord?                  folderTool,
        string                       appRoot,
        string                       platformId,
        string                       datLineId,
        DateTime                     now,
        DatLineStore                 store,
        IngestionResult              result,
        IProgress<IngestionProgress> progress,
        HashSet<string>              transformFailedReleases)
    {
        var ck = $"release:{releaseId}";

        try
        {
            // ── 1. Ensure release-level content identity (hash fields null) ────
            store.EnsureContentIdentity(new Data.ContentIdentityRecord
            {
                ContentIdentityKey = ck,
                DatSha1            = null,
                DatMd5             = null,
                DatCrc32           = null,
                CreatedAtUtc       = now,
            });

            // ── 2. Build derived artifact destination path ─────────────────────
            var archiveDir = Path.Combine(appRoot, "archive", platformId, datLineId);
            Directory.CreateDirectory(archiveDir);
            var outputExt = folderXform.OutputExtension.Length > 0 ? folderXform.OutputExtension : ".zip";
            var destName  = safeFolder + outputExt;
            var destPath  = Path.Combine(archiveDir, destName);
            var relPath   = $"archive/{platformId}/{datLineId}/{destName}";

            // ── 3. Transform: source folder → derived file ────────────────────
            if (!File.Exists(destPath))
            {
                if (!TransformEngine.ExecuteTransform(folderXform, folderTool, appRoot, sourceDir, destPath, out var xformError))
                    throw new InvalidOperationException($"Transform failed: {xformError}");
            }

            // ── 4. Log transform step ─────────────────────────────────────────
            var xformOp = new IngestionOperation(safeFolder, "transform", $"{folderXform.Name} → {destName}");
            result.Operations.Add(xformOp);
            progress.Report(new IngestionProgress { NewOperation = xformOp });

            // ── 5. Hash derived file (SHA1 + MD5 + CRC32 in one pass) ──────────
            var (hashedDerivedSha1, hashedDerivedMd5, hashedDerivedCrc32) =
                ComputeSourceHashes(destPath);

            // ── 6. Persist derived artifact ───────────────────────────────────
            var derivedSizeBytes = new FileInfo(destPath).Length;
            store.IngestDerivedArtifact(
                contentIdentityKey: ck,
                sourceArtifactId:   "",
                storageStrategyId:  folderXform.Id,
                fileName:           destName,
                relativePath:       relPath,
                derivedSizeBytes:   derivedSizeBytes,
                hashedDerivedSha1:  hashedDerivedSha1,
                hashedDerivedMd5:   hashedDerivedMd5,
                hashedDerivedCrc32: hashedDerivedCrc32,
                archiveTier:        folderXform.ArchiveTier);

            // ── 7. Link release → release-level content identity ──────────────
            store.SaveReleaseContentLink(new Data.ReleaseContentLinkRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                ReleaseId          = releaseId,
                ContentIdentityKey = ck,
                CreatedAtUtc       = now,
            });

            // ── 8. Remove source folder — all DB writes succeeded ─────────────
            // Delete the promoted release folder only after the derived artifact
            // is validated (non-zero size) and all provenance rows are written.
            if (derivedSizeBytes > 0)
            {
                try { Directory.Delete(sourceDir, recursive: true); }
                catch { /* best-effort; leave folder if OS denies deletion */ }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Transform failures must never block the rest of ingestion.
            transformFailedReleases.Add(releaseId);
            var failOp = new IngestionOperation(safeFolder, "transform-failed", ex.Message);
            result.Operations.Add(failOp);
            progress.Report(new IngestionProgress { NewOperation = failOp });
        }
    }

    // ── Release-shape dispatch processor ─────────────────────────────────────
    // Dispatches per release based on its detected shape:
    //   single .iso  → CHD DVD Compression (chd_dvd_compression)
    //   .cue + .bin  → CHD CD Compression  (chd_cd_compression)
    // One derived artifact per release; .bin files are dependencies, not transform inputs.
    // Provenance: content_identity_key = "release:{releaseId}", source_artifact_id = "".

    private void ProcessReleaseShapeOrientedRelease(
        string                       releaseId,
        string                       safeFolder,
        string                       sourceDir,
        List<ReleaseFileRecord>      expectedFiles,
        List<TransformRecord>        allTransforms,
        List<ToolRecord>             allTools,
        string                       appRoot,
        string                       platformId,
        string                       datLineId,
        string                       releaseStatus,
        DateTime                     now,
        DatLineStore                 store,
        IngestionResult              result,
        IProgress<IngestionProgress> progress,
        HashSet<string>              transformFailedReleases)
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease(releaseId, expectedFiles);

        if (plan.Shape == ReleaseTransformShape.Unsupported)
        {
            transformFailedReleases.Add(releaseId);
            var failOp = new IngestionOperation(safeFolder, "transform-config-error",
                "Release shape is not supported for Per release shape strategy. " +
                "Expected single .iso or .cue+.bin combination.");
            result.Operations.Add(failOp);
            progress.Report(new IngestionProgress { NewOperation = failOp });
            return;
        }

        var xform = allTransforms.FirstOrDefault(t => t.Id == plan.TransformId);
        if (xform == null)
        {
            transformFailedReleases.Add(releaseId);
            var failOp = new IngestionOperation(safeFolder, "transform-config-error",
                $"Transform '{plan.TransformId}' not found. Ensure chdman is configured.");
            result.Operations.Add(failOp);
            progress.Report(new IngestionProgress { NewOperation = failOp });
            return;
        }

        var tool = xform.ToolId.Length > 0
            ? allTools.FirstOrDefault(t => t.Id == xform.ToolId)
            : null;

        var ck = $"release:{releaseId}";

        try
        {
            // ── 1. Ensure release-level content identity ──────────────────────
            store.EnsureContentIdentity(new Data.ContentIdentityRecord
            {
                ContentIdentityKey = ck,
                DatSha1            = null,
                DatMd5             = null,
                DatCrc32           = null,
                CreatedAtUtc       = now,
            });

            // ── 2. Build derived artifact destination path ────────────────────
            var archiveDir = Path.Combine(appRoot, "archive", platformId, datLineId);
            Directory.CreateDirectory(archiveDir);
            var outputExt  = xform.OutputExtension.Length > 0 ? xform.OutputExtension : ".chd";
            var destName   = Path.GetFileNameWithoutExtension(plan.MainInputFile) + outputExt;
            var destPath   = Path.Combine(archiveDir, destName);
            var relPath    = $"archive/{platformId}/{datLineId}/{destName}";

            // ── 2b. Satisfaction check — skip if artifact is already valid ────
            // A "present" release whose CHD exists, has size > 0, and whose physical
            // hash matches the DB record does not need to be re-transformed.
            bool fileExistedBefore = File.Exists(destPath);
            {
                var existingArtifacts = store.GetDerivedArtifactsByReleaseId(releaseId);
                var check = Ingestion.DerivedArtifactSatisfactionChecker.Check(
                    releaseStatus, existingArtifacts, destPath);
                if (check.IsSatisfied)
                {
                    var alreadyOp = new IngestionOperation(destName, "already-present", relPath);
                    result.Operations.Add(alreadyOp);
                    progress.Report(new IngestionProgress { NewOperation = alreadyOp });
                    // Source files were promoted to sourceDir — delete them; the CHD covers them.
                    foreach (var f in expectedFiles)
                        try { File.Delete(Path.Combine(sourceDir, f.RomName)); } catch { }
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
                            Directory.Delete(sourceDir);
                    }
                    catch { }
                    return;
                }
                if (fileExistedBefore)
                {
                    var rebuildOp = new IngestionOperation(destName, "rebuild-required", check.Reason);
                    result.Operations.Add(rebuildOp);
                    progress.Report(new IngestionProgress { NewOperation = rebuildOp });
                }
            }

            // ── 3. Transform: main input file → derived CHD ───────────────────
            string workdirNote = "";
            if (plan.Shape == ReleaseTransformShape.CueBin)
            {
                // CUE/BIN: routes through a short-path workdir so chdman never sees long filenames.
                // File.Exists guard removed — satisfaction check above handles idempotency correctly.
                if (!CueBinWorkdir.Run(
                        appRoot, xform, tool, sourceDir,
                        plan.MainInputFile, plan.DependencyFiles,
                        destPath, out var workdirUsed, out var xformErr))
                    throw new InvalidOperationException($"Transform failed: {xformErr}");
                workdirNote = $"; {plan.DependencyFiles.Count} bin(s), workdir {Path.GetFileName(workdirUsed)}";
            }
            else
            {
                // SingleIso: always run via workdir; stale partial CHDs are overwritten atomically
                // only after the workdir output.chd is fully produced and verified.
                var mainInputPath = Path.Combine(sourceDir, plan.MainInputFile);
                if (!IsoChdWorkdir.Run(
                        appRoot, xform, tool, mainInputPath, destPath,
                        out var workdirUsed, out var hardlinked, out var xformErr))
                    throw new InvalidOperationException($"Transform failed: {xformErr}");
                workdirNote = $"; workdir {Path.GetFileName(workdirUsed)}; materialized: {(hardlinked ? "hardlink" : "copy fallback")}";
            }

            if (fileExistedBefore)
            {
                var overwriteOp = new IngestionOperation(destName, "stale-artifact-overwritten", relPath);
                result.Operations.Add(overwriteOp);
                progress.Report(new IngestionProgress { NewOperation = overwriteOp });
            }

            // ── 4. Log transform step ─────────────────────────────────────────
            var xformOp = new IngestionOperation(
                plan.MainInputFile, "transform",
                $"{xform.Name} → {destName}{workdirNote}");
            result.Operations.Add(xformOp);
            progress.Report(new IngestionProgress { NewOperation = xformOp });

            // ── 5. Hash derived file (SHA1 + MD5 + CRC32 in one pass) ─────────
            var (hashedDerivedSha1, hashedDerivedMd5, hashedDerivedCrc32) =
                ComputeSourceHashes(destPath);

            // ── 6. Persist source provenance + derived artifact ───────────────
            // Source files are still present in sourceDir at this point (step 8 deletes them).
            long totalSourceBytes = 0;
            foreach (var f in expectedFiles)
            {
                try { totalSourceBytes += new FileInfo(Path.Combine(sourceDir, f.RomName)).Length; }
                catch { /* file missing — size contribution stays 0 */ }
            }
            store.SaveSourceArtifact(new Data.SourceArtifactRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                ContentIdentityKey = ck,
                SourceSizeBytes    = totalSourceBytes,
                HashedSourceSha1   = "",
                HashedSourceMd5    = null,
                HashedSourceCrc32  = null,
                VerifiedAtUtc      = now,
            });

            var derivedSizeBytes = new FileInfo(destPath).Length;
            store.IngestDerivedArtifact(
                contentIdentityKey: ck,
                sourceArtifactId:   "",
                storageStrategyId:  xform.Id,
                fileName:           destName,
                relativePath:       relPath,
                derivedSizeBytes:   derivedSizeBytes,
                hashedDerivedSha1:  hashedDerivedSha1,
                hashedDerivedMd5:   hashedDerivedMd5,
                hashedDerivedCrc32: hashedDerivedCrc32,
                archiveTier:        xform.ArchiveTier);

            // ── 7. Link release → release-level content identity ──────────────
            store.SaveReleaseContentLink(new Data.ReleaseContentLinkRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                ReleaseId          = releaseId,
                ContentIdentityKey = ck,
                CreatedAtUtc       = now,
            });

            // ── 8. Log derived artifact committed ────────────────────────────
            var committedOp = new IngestionOperation(
                destName, "derived-committed",
                $"archive/{platformId}/{datLineId}/{destName}");
            result.Operations.Add(committedOp);
            progress.Report(new IngestionProgress { NewOperation = committedOp });

            // ── 9. Remove source files — derived artifact is valid ────────────
            if (derivedSizeBytes > 0)
            {
                foreach (var f in expectedFiles)
                {
                    var srcPath = Path.Combine(sourceDir, f.RomName);
                    try { File.Delete(srcPath); } catch { /* best-effort */ }
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
                        Directory.Delete(sourceDir);
                }
                catch { /* best-effort */ }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            transformFailedReleases.Add(releaseId);
            var failOp = new IngestionOperation(safeFolder, "transform-failed", ex.Message);
            result.Operations.Add(failOp);
            progress.Report(new IngestionProgress { NewOperation = failOp });
        }
    }

    // ── Folder-oriented release processor — output_kind = folder ─────────────
    // Processes one promoted release folder as a single unit where the derived
    // artifact is itself a folder (no external compression):
    //   source/{platform}/{datLine}/{safeFolder}/ → archive/.../{safeFolder}/
    // Provenance: content_identity_key = "release:{releaseId}", source_artifact_id = "".
    // Called from the outer release loop when datLineStrategyType == "release_folder"
    // and the configured folder transform has output_kind = "folder" (No Compression (Folder)).
    // derived_size_bytes = sum of all contained file sizes.

    private void ProcessFolderOrientedFolderRelease(
        string                       releaseId,
        string                       safeFolder,
        string                       sourceDir,
        List<ReleaseFileRecord>      expectedFiles,
        TransformRecord              folderXform,
        string                       appRoot,
        string                       platformId,
        string                       datLineId,
        DateTime                     now,
        DatLineStore                 store,
        IngestionResult              result,
        IProgress<IngestionProgress> progress,
        HashSet<string>              transformFailedReleases)
    {
        var ck = $"release:{releaseId}";

        try
        {
            // ── 1. Ensure release-level content identity (hash fields null) ────
            store.EnsureContentIdentity(new Data.ContentIdentityRecord
            {
                ContentIdentityKey = ck,
                DatSha1            = null,
                DatMd5             = null,
                DatCrc32           = null,
                CreatedAtUtc       = now,
            });

            // ── 2. Build derived folder destination path ───────────────────────
            var archiveDir = Path.Combine(appRoot, "archive", platformId, datLineId);
            Directory.CreateDirectory(archiveDir);
            var destPath = Path.Combine(archiveDir, safeFolder);   // folder, no extension
            var relPath  = $"archive/{platformId}/{datLineId}/{safeFolder}";

            // ── 3. Copy source folder → derived folder (no compression) ────────
            // Only transforms with an empty command template are supported for folder
            // output — those use a direct recursive copy.  External-tool folder-output
            // transforms are guarded here for future implementation.
            if (!Directory.Exists(destPath))
            {
                if (folderXform.CommandTemplate.Length > 0)
                    throw new InvalidOperationException(
                        $"Folder-output transforms with a command template are not yet supported " +
                        $"(transform: {folderXform.Name}).");
                CopyFolderRecursive(sourceDir, destPath);
            }

            // ── 4. Validate output folder ─────────────────────────────────────
            if (!Directory.Exists(destPath))
                throw new InvalidOperationException($"Output folder absent after copy: {destPath}");

            foreach (var f in expectedFiles)
            {
                var outFile = Path.Combine(destPath, f.RomName);
                if (!File.Exists(outFile))
                    throw new InvalidOperationException(
                        $"Expected file missing from derived folder: {f.RomName}");

                if (f.Sha1.Length > 0)
                {
                    var (actualSha1, _, _) = ComputeSourceHashes(outFile);
                    if (!string.Equals(actualSha1, f.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"SHA1 mismatch for {f.RomName}: expected {f.Sha1[..8]}… got {actualSha1[..8]}…");
                }
            }

            // ── 5. Log transform step ─────────────────────────────────────────
            var xformOp = new IngestionOperation(safeFolder, "transform", $"{folderXform.Name} → {safeFolder}/");
            result.Operations.Add(xformOp);
            progress.Report(new IngestionProgress { NewOperation = xformOp });

            // ── 6. Compute derived size (sum of all files in the derived folder) ─
            var derivedSizeBytes = Directory
                .EnumerateFiles(destPath, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);

            // ── 7. Persist derived artifact ───────────────────────────────────
            // hashed_derived_sha1 = "" — no single-file hash for a folder artifact.
            store.IngestDerivedArtifact(
                contentIdentityKey: ck,
                sourceArtifactId:   "",
                storageStrategyId:  folderXform.Id,
                fileName:           safeFolder,
                relativePath:       relPath,
                derivedSizeBytes:   derivedSizeBytes,
                hashedDerivedSha1:  "",
                hashedDerivedMd5:   null,
                hashedDerivedCrc32: null,
                archiveTier:        folderXform.ArchiveTier);

            // ── 8. Link release → release-level content identity ──────────────
            store.SaveReleaseContentLink(new Data.ReleaseContentLinkRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                ReleaseId          = releaseId,
                ContentIdentityKey = ck,
                CreatedAtUtc       = now,
            });

            // ── 9. Remove source folder — all validation and DB writes succeeded ─
            if (derivedSizeBytes > 0)
            {
                try { Directory.Delete(sourceDir, recursive: true); }
                catch { /* best-effort; leave folder if OS denies deletion */ }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            transformFailedReleases.Add(releaseId);
            var failOp = new IngestionOperation(safeFolder, "transform-failed", ex.Message);
            result.Operations.Add(failOp);
            progress.Report(new IngestionProgress { NewOperation = failOp });
        }
    }

    private static void CopyFolderRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyFolderRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
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
    /// Attempts to move <paramref name="srcPath"/> into <paramref name="quarantineDir"/>.
    /// Uses copy+delete as fallback if cross-volume move fails. Returns true on success.
    /// </summary>
    private static bool TryQuarantineFile(string srcPath, string fileName, string quarantineDir, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(quarantineDir);
            var dest = IncomingSkipUniquePath(quarantineDir, fileName);
            try { File.Move(srcPath, dest, overwrite: false); }
            catch
            {
                File.Copy(srcPath, dest, overwrite: false);
                File.Delete(srcPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Pre-ingest: extract .zip, .7z, and .rar archives found recursively under <paramref name="incomingDir"/>.
    /// ZIP archives are handled natively. .7z and .rar require tools\7zip\7zip.exe.
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
                       string.Equals(ext, ".7z",  StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase);
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

            var archiveExt = Path.GetExtension(archivePath).ToLowerInvariant();
            var isZip      = archiveExt == ".zip";

            // For non-ZIP archives, resolve the bundled 7zip before doing anything else
            string? sevenZipPath = null;
            if (!isZip)
            {
                sevenZipPath = ProviderHelpers.Find7zip();
                if (sevenZipPath is null)
                {
                    var skipOp = new IngestionOperation(archiveName, "extract-skipped",
                        "7zip not found — place tools\\7zip\\7zip.exe to enable .7z and .rar extraction.");
                    result.Operations.Add(skipOp);
                    progress.Report(new IngestionProgress { NewOperation = skipOp });
                    continue;
                }
            }

            // Check decompressed size and free space (ZIP only; 7z/rar skip this check)
            long decompressedSize = 0;
            if (isZip)
            {
                try
                {
                    using var za = ZipFile.OpenRead(archivePath);
                    decompressedSize = za.Entries.Sum(e => e.Length);
                }
                catch (Exception ex)
                {
                    var failOp = new IngestionOperation(archiveName, "extract-failed",
                        $"could not read archive: {ex.Message}");
                    result.Operations.Add(failOp);
                    progress.Report(new IngestionProgress { NewOperation = failOp });
                    continue;
                }
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

                if (isZip)
                {
                    // Explicit using-block so za is fully disposed before File.Delete runs.
                    // 'using var' would defer disposal to end of the try-block, keeping
                    // the handle open during the delete call.
                    using (var za = ZipFile.OpenRead(archivePath))
                    {
                        foreach (var entry in za.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;  // directory entry
                            var relPath  = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                            var fullPath = Path.GetFullPath(Path.Combine(destFolder, relPath));
                            // path traversal guard
                            if (!fullPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                                continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                            using var outStream   = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
                            using var entryStream = entry.Open();
                            entryStream.CopyTo(outStream);
                        }
                    } // za disposed here — archive handle fully closed
                }
                else
                {
                    // 7zip handles path safety internally; sevenZipPath is non-null (checked above)
                    ProviderHelpers.ExtractWith7zip(sevenZipPath!, archivePath, destFolder);
                }

                var okOp = new IngestionOperation(archiveName, "extract-ok", folderName);
                result.Operations.Add(okOp);
                progress.Report(new IngestionProgress { NewOperation = okOp });

                // Record the archive and every file it produced for per-archive cleanup in Phase 9.
                var extractedFiles = Directory.Exists(destFolder)
                    ? (IReadOnlyList<string>)Directory
                        .GetFiles(destFolder, "*", SearchOption.AllDirectories)
                        .Select(Path.GetFullPath)
                        .ToList()
                    : (IReadOnlyList<string>)Array.Empty<string>();
                result.ExtractedArchiveInfos.Add(new Ingestion.ExtractedArchiveInfo(
                    archivePath,
                    Path.GetFullPath(destFolder),
                    extractedFiles));
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
            sb.AppendLine("── COUNTS ─────────────────────────────────────────────────────────────────");
            sb.AppendLine($"  Files scanned:          {result.FilesScanned}");
            sb.AppendLine($"  Files matched:          {result.FilesMatched}");
            sb.AppendLine($"  Files copied:           {result.FilesCopied}");
            sb.AppendLine($"  Releases present:       {result.ReleasesPresent}");
            sb.AppendLine($"  Releases incomplete:    {result.ReleasesIncomplete}");
            sb.AppendLine($"  Files skipped:          {result.FilesSkipped}");
            sb.AppendLine($"  Transforms failed:      {result.TransformsFailed}");
            sb.AppendLine($"  Archives deleted:       {result.FilesDeletedFromIncoming}");
            sb.AppendLine();

            if (result.TransformsFailed > 0 || result.ReleasesIncomplete > 0)
            {
                sb.AppendLine("── FAILURES ───────────────────────────────────────────────────────────────");
                foreach (var op in result.Operations)
                {
                    if (op.Action == "transform-failed"    ||
                        op.Action == "transform-config-error" ||
                        op.Action == "incomplete-skipped")
                        sb.AppendLine($"  {op.Object,-50} | {op.Action,-22} | {op.Destination}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("── OPERATIONS ─────────────────────────────────────────────────────────────");
            foreach (var op in result.Operations)
                sb.AppendLine($"  {op.Object,-50} | {op.Action,-22} | {op.Destination}");
            sb.AppendLine();
            sb.AppendLine($"RESULT: {result.StatusText}");
            if (result.Error is not null)
                sb.AppendLine($"  ERROR: {result.Error}");

            File.WriteAllText(path, sb.ToString());
        }
        catch { /* log failure is non-fatal */ }
    }

    private async void OnCreatePlatform(object? sender, RoutedEventArgs e)
    {
        var existingIds   = _catalog.GetHardwareFamilies().Select(p => p.Id);
        var hardwareTypes = _catalog.LoadHardwareTypes();
        var dialog        = new CreatePlatformDialog(existingIds, null, Path.Combine(_dataDir, "systemimages"), hardwareTypes, _catalog);
        var confirmed   = await dialog.ShowDialog<bool>(this);
        if (!confirmed || dialog.CreatedPlatform is null) return;

        var platform = dialog.CreatedPlatform;
        _catalog.SaveHardwareFamilies([platform]);

        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "incoming-roms", platform.Id));

        // Copy images into data/systemimages/
        var imageDir = Path.Combine(_dataDir, "systemimages");
        Directory.CreateDirectory(imageDir);

        if (dialog.LogoImagePath is not null)
        {
            File.Copy(dialog.LogoImagePath,
                Path.Combine(imageDir, $"{platform.Id}-logo.png"), overwrite: true);
            var src = Path.Combine(imageDir, Systems.PlatformImageCache.SourceFileName(platform.Id, "logo"));
            File.Copy(dialog.LogoImagePath, src, overwrite: true);
            Systems.PlatformImageCache.GenerateCachedVariants(src, platform.Id, "logo");
        }
        if (dialog.DetailsImagePath is not null)
        {
            File.Copy(dialog.DetailsImagePath,
                Path.Combine(imageDir, $"{platform.Id}-details.png"), overwrite: true);
            var src = Path.Combine(imageDir, Systems.PlatformImageCache.SourceFileName(platform.Id, "details"));
            File.Copy(dialog.DetailsImagePath, src, overwrite: true);
            Systems.PlatformImageCache.GenerateCachedVariants(src, platform.Id, "details");
        }

        RefreshSystemsKeepSelection(platform.Id);
    }

    private async void OnEditPlatform(object? sender, RoutedEventArgs e)
    {
        var platform = _selectedPlatform;
        if (platform is null) return;

        var existing = _catalog.GetHardwareFamilies().FirstOrDefault(p => p.Id == platform.Id);
        if (existing is null) return;

        var otherIds      = _catalog.GetHardwareFamilies().Select(p => p.Id).Where(id => id != existing.Id);
        var imageDir      = Path.Combine(_dataDir, "systemimages");
        var hardwareTypes = _catalog.LoadHardwareTypes();
        var dialog        = new CreatePlatformDialog(otherIds, existing, imageDir, hardwareTypes, _catalog);
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed || dialog.CreatedPlatform is null) return;

        _catalog.SaveHardwareFamilies([dialog.CreatedPlatform]);

        Directory.CreateDirectory(imageDir);

        // Apply image deletions
        if (dialog.DeleteLogoImage)
        {
            DeletePlatformImageFiles(imageDir, existing.Id, "logo");
        }
        if (dialog.DeleteDetailsImage)
        {
            DeletePlatformImageFiles(imageDir, existing.Id, "details");
        }

        // Apply new images (overwrite if replacing)
        if (dialog.LogoImagePath is not null)
        {
            File.Copy(dialog.LogoImagePath,
                Path.Combine(imageDir, $"{existing.Id}-logo.png"), overwrite: true);
            var src = Path.Combine(imageDir, Systems.PlatformImageCache.SourceFileName(existing.Id, "logo"));
            File.Copy(dialog.LogoImagePath, src, overwrite: true);
            Systems.PlatformImageCache.GenerateCachedVariants(src, existing.Id, "logo");
        }
        if (dialog.DetailsImagePath is not null)
        {
            File.Copy(dialog.DetailsImagePath,
                Path.Combine(imageDir, $"{existing.Id}-details.png"), overwrite: true);
            var src = Path.Combine(imageDir, Systems.PlatformImageCache.SourceFileName(existing.Id, "details"));
            File.Copy(dialog.DetailsImagePath, src, overwrite: true);
            Systems.PlatformImageCache.GenerateCachedVariants(src, existing.Id, "details");
        }

        RefreshSystemsKeepSelection(existing.Id);
    }

    private async void OnDeletePlatform(object? sender, RoutedEventArgs e)
    {
        if (_selectedPlatformId is null) return;
        var platformId   = _selectedPlatformId;
        var platformName = _selectedPlatform?.Name ?? platformId;

        if (_catalog.HardwareFamilyHasDependencies(platformId))
        {
            await new InfoDialog(
                "Cannot Delete System",
                "Cannot delete this system because it has registered DAT lines.\n\n" +
                "Delete all DAT lines for this system first.")
                .ShowDialog(this);
            return;
        }

        var confirmed = await new ConfirmDialog(
            "Delete System",
            $"Permanently delete system \"{platformName}\"?\n\n" +
            "This will remove the system from the catalog.\n" +
            "No DAT lines, releases, or media will be affected.\n\n" +
            "This action cannot be undone.")
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        _catalog.DeleteHardwareFamily(platformId);
        _selectedPlatformId = null;
        _selectedPlatform   = null;
        _selectedDatLine    = null;
        RefreshSystemsKeepSelection(null);
    }

    private async void OnImportDat(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedPlatformId))
        {
            await new InfoDialog("Import DAT", "Select a system first.").ShowDialog(this);
            return;
        }

        var platforms        = _catalog.GetHardwareFamilies();
        var existingDatLines = _catalog.LoadDatLines();
        var importDialog     = new ImportDatDialog(platforms, existingDatLines, _catalog, _dataDir, _selectedPlatformId);
        var ok               = await importDialog.ShowDialog<bool>(this);
        if (!ok) return;

        var platformId    = importDialog.HardwareFamilyId ?? "";
        var authority     = importDialog.Authority        ?? "";
        var mediaTypeId   = importDialog.MediaTypeId      ?? "";
        var datLineId     = importDialog.DatLineId        ?? "";
        var authorityName = importDialog.SelectedAuthority?.Name ?? authority;
        var datLineName   = mediaTypeId;
        var version     = importDialog.Version     ?? "";
        var parsedGames = importDialog.ParsedGames.ToList();

        var relPath = $"systems/{platformId}/{datLineId}.db";
        var absPath = Path.Combine(_dataDir, relPath);

        var newDatLineRecord = new DatLineRecord
        {
            Id                = datLineId,
            HardwareFamilyId  = platformId,
            Name              = datLineName,
            Authority         = authority,
            MediaTypeId       = mediaTypeId,
            Version           = version,
            StorageStrategyId = "",
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

        foreach (var game in parsedGames)
            if (game.WorkingState.Length > 0)
                _catalog.SetWorkingStateIfNotManual(game.Name, game.WorkingState);

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

            var platformName  = allPlatforms.FirstOrDefault(p => p.Id == dl.HardwareFamilyId)?.Name ?? dl.HardwareFamilyId;
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
                    DatLineName        = FormatDatLineName(dl.Authority, dl.MediaTypeId),
                    PlatformId         = dl.HardwareFamilyId,
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
        MappingsList.ItemsSource = _mappingRows;
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
        SettingShowDebugArtifactInfo.IsChecked     = _catalog.GetBoolSetting("show_debug_artifact_info",       defaultValue: false);
        SettingImageCacheRegenWriteLog.IsChecked   = _catalog.GetBoolSetting("image_cache_regen_write_log",   defaultValue: true);
        SettingLogsToKeep.Text                     = _catalog.GetSetting("logs_to_keep_per_type", "5");
        SettingCatalogVideoAutoplay.IsChecked      = _catalog.GetBoolSetting("catalog_video_autoplay",        defaultValue: true);
        SettingCatalogVideoAudio.IsChecked         = _catalog.GetBoolSetting("catalog_video_audio",           defaultValue: false);
        LoadMappingsSettings();
    }

    private async void OnOpenCacheBuilder(object? sender, RoutedEventArgs e)
    {
        var dialog = new ScreenScraperCacheBuilderDialog(_catalog);
        await dialog.ShowDialog(this);
    }

    private async void OnManageCachePackages(object? sender, RoutedEventArgs e)
    {
        var dialog = new ScreenScraperCacheManagerDialog(_catalog);
        await dialog.ShowDialog(this);
    }

    private async void OnManageStaging(object? sender, RoutedEventArgs e)
    {
        var dialog = new ScreenScraperStagingManagerDialog(AppContext.BaseDirectory);
        await dialog.ShowDialog(this);
    }

    private async void OnManageAmpLocalPacks(object? sender, RoutedEventArgs e)
    {
        var dialog = new AmpLocalPackagesDialog(_dataDir);
        await dialog.ShowDialog(this);
    }

    private void OnProvidersAmpOpenFolder(object? sender, RoutedEventArgs e)
    {
        var registry = new AmpLocalRegistryService(_dataDir);
        registry.EnsureFolder();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = registry.RegistryFolder,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        _catalog.SetSetting("quarantine_mismatch_on_verify",  SettingQuarantineMismatch.IsChecked    == true ? "true" : "false");
        _catalog.SetSetting("quarantine_unexpected_on_verify", SettingQuarantineUnexpected.IsChecked  == true ? "true" : "false");
        _catalog.SetSetting("auto_export_ingestion_logs",      SettingAutoExportLogs.IsChecked        == true ? "true" : "false");
        _catalog.SetSetting("log_on_copy",                     SettingLogOnCopy.IsChecked             == true ? "true" : "false");
        _catalog.SetSetting("auto_export_verify_logs",         SettingAutoExportVerifyLogs.IsChecked  == true ? "true" : "false");
        _catalog.SetSetting("auto_export_repair_logs",         SettingAutoExportRepairLogs.IsChecked  == true ? "true" : "false");
        _catalog.SetSetting("show_debug_artifact_info",      SettingShowDebugArtifactInfo.IsChecked   == true ? "true" : "false");
        _catalog.SetSetting("image_cache_regen_write_log",  SettingImageCacheRegenWriteLog.IsChecked == true ? "true" : "false");
        _catalog.SetSetting("catalog_video_autoplay",        SettingCatalogVideoAutoplay.IsChecked    == true ? "true" : "false");
        _catalog.SetSetting("catalog_video_audio",           SettingCatalogVideoAudio.IsChecked       == true ? "true" : "false");
        var logsToKeepRaw = SettingLogsToKeep.Text?.Trim() ?? "5";
        var logsToKeep    = int.TryParse(logsToKeepRaw, out var lv) && lv >= 1 ? lv : 5;
        _catalog.SetSetting("logs_to_keep_per_type", logsToKeep.ToString());
        SettingLogsToKeep.Text = logsToKeep.ToString();
        // Apply show_debug_artifact_info immediately (affects current session without restart)
        _showDebugArtifactInfo = SettingShowDebugArtifactInfo.IsChecked == true;
    }

    private void OnReloadSettings(object? sender, RoutedEventArgs e)
        => LoadAllSettings();

    // ── Metadata Value Mappings settings ─────────────────────────────────────

    private void LoadMappingsSettings()
    {
        foreach (var old in _mappingRows)
            old.PropertyChanged -= OnMappingRowPropertyChanged;
        _mappingRows.Clear();
        foreach (var m in _catalog.LoadMetadataValueMappings())
        {
            var vm = new MappingRowVm(m);
            vm.PropertyChanged += OnMappingRowPropertyChanged;
            _mappingRows.Add(vm);
        }
    }

    private void OnMappingRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MappingRowVm.Enabled)) return;
        if (sender is not MappingRowVm vm) return;
        _catalog.SaveMetadataValueMapping(vm.Field, vm.MatchValue, vm.Replacement, vm.Enabled);
        RefreshMappingsCache();
    }

    private void RefreshMappingsCache()
    {
        _metadataMappings = _catalog.LoadMetadataValueMappings();
        if (_catalogSelected is not null) UpdateCatalogHero(_catalogSelected);
    }

    private void OnMappingsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MappingsList.SelectedItem is not MappingRowVm vm)
        {
            MappingDeleteBtn.IsEnabled = false;
            return;
        }

        MappingField.SelectedItem = MappingField.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Content?.ToString(), vm.Field, StringComparison.Ordinal));
        MappingMatchValue.Text        = vm.MatchValue;
        MappingReplacement.Text       = vm.Replacement;
        MappingEnabled.IsChecked      = vm.Enabled;
        MappingDeleteBtn.IsEnabled    = true;
        MappingValidationMsg.IsVisible = false;
    }

    private void OnAddUpdateMapping(object? sender, RoutedEventArgs e)
    {
        var field = (MappingField.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "";
        var match = MappingMatchValue.Text?.Trim() ?? "";
        var repl  = MappingReplacement.Text?.Trim() ?? "";
        var enabled = MappingEnabled.IsChecked == true;

        if (field.Length == 0 || match.Length == 0 || repl.Length == 0)
        {
            MappingValidationMsg.Text      = "Field, Match Value, and Replacement are all required.";
            MappingValidationMsg.IsVisible = true;
            return;
        }

        MappingValidationMsg.IsVisible = false;
        _catalog.SaveMetadataValueMapping(field, match, repl, enabled);
        LoadMappingsSettings();
        RefreshMappingsCache();
        ClearMappingForm();
    }

    private void OnDeleteMapping(object? sender, RoutedEventArgs e)
    {
        if (MappingsList.SelectedItem is not MappingRowVm vm) return;
        _catalog.DeleteMetadataValueMapping(vm.Field, vm.MatchValue);
        LoadMappingsSettings();
        RefreshMappingsCache();
        ClearMappingForm();
    }

    private void ClearMappingForm()
    {
        MappingsList.SelectedItem      = null;
        MappingField.SelectedItem      = null;
        MappingMatchValue.Text         = "";
        MappingReplacement.Text        = "";
        MappingEnabled.IsChecked       = true;
        MappingDeleteBtn.IsEnabled     = false;
        MappingValidationMsg.IsVisible = false;
    }

    private async void OnPruneLogs(object? sender, RoutedEventArgs e)
    {
        var keepRaw = SettingLogsToKeep.Text?.Trim() ?? "5";
        var keep    = int.TryParse(keepRaw, out var kv) && kv >= 1 ? kv : 5;

        var logsRoot = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsRoot))
        {
            await new InfoDialog("Prune Logs", "No logs directory found. Nothing to prune.").ShowDialog(this);
            return;
        }

        var subfolders    = Directory.GetDirectories(logsRoot);
        int typesChecked  = 0;
        int filesDeleted  = 0;

        foreach (var folder in subfolders)
        {
            typesChecked++;
            var files = Directory.GetFiles(folder)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(keep)
                .ToList();

            foreach (var file in files)
            {
                try { File.Delete(file); filesDeleted++; }
                catch { /* non-fatal */ }
            }
        }

        await new InfoDialog(
            "Prune Logs",
            $"Pruning complete.\n\nLog types checked: {typesChecked}\nFiles deleted: {filesDeleted}"
        ).ShowDialog(this);
    }

    // ── Integrity Validation ──────────────────────────────────────────────────

    private async void OnValidateIntegrity(object? sender, RoutedEventArgs e)
    {
        var appRoot = AppContext.BaseDirectory;
        var dataDir = _dataDir;
        var startTime = DateTime.UtcNow;

        Data.IntegrityReport? report = null;
        string? errorMessage = null;

        try
        {
            report = await Task.Run(() =>
                Data.IntegrityValidator.Validate(_catalog, dataDir, appRoot));
        }
        catch (Exception ex) { errorMessage = ex.Message; }

        if (errorMessage is not null)
        {
            await new InfoDialog("Integrity Validation Failed",
                $"Validation could not complete:\n\n{errorMessage}")
                .ShowDialog(this);
            return;
        }

        var elapsed = DateTime.UtcNow - startTime;

        // ── Build report text ──────────────────────────────────────────────
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Integrity Validation Report");
        sb.AppendLine($"Generated: {startTime:o}");
        sb.AppendLine($"Duration:  {elapsed.TotalSeconds:F1}s");
        sb.AppendLine();

        void WriteCheck(string label, System.Collections.Generic.List<Data.IntegrityViolation> violations,
            bool informational = false)
        {
            var tag = informational ? " (informational)" : "";
            sb.AppendLine(violations.Count == 0
                ? $"{label}: OK"
                : $"{label}: {violations.Count} violation(s){tag}");
            foreach (var v in violations)
                sb.AppendLine($"  [{v.DatLine}] {v.FileName} — {v.Detail}");
            if (violations.Count > 0) sb.AppendLine();
        }

        WriteCheck("CHECK 1 — Artifact Availability", report!.Check1_Availability);
        WriteCheck("CHECK 2 — Lost Volume Invariant",  report.Check2_LostVolume);
        WriteCheck("CHECK 3 — Release Consistency",    report.Check3_Release);
        WriteCheck("CHECK 4 — Orphan Mappings",        report.Check4_Orphan);
        WriteCheck("CHECK 5 — Duplicate Presence",     report.Check5_Duplicate, informational: true);
        sb.AppendLine();
        sb.AppendLine(report.IsHealthy
            ? "SYSTEM HEALTH: OK"
            : $"SYSTEM HEALTH: NOT OK — {report.TotalViolations} violation(s) found");

        // ── Write log ──────────────────────────────────────────────────────
        string? logPath = null;
        try
        {
            var logDir = Path.Combine(appRoot, "logs", "integrity");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir,
                $"{startTime:yyyyMMdd-HHmmss}-integrity.log");
            File.WriteAllText(logPath, sb.ToString());
        }
        catch { /* non-fatal */ }

        // ── Summary dialog ─────────────────────────────────────────────────
        static string StatusLine(int count, bool informational = false) =>
            count == 0 ? "OK" : informational ? $"{count} (informational)" : $"{count} VIOLATION(S)";

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"CHECK 1 — Availability:        {StatusLine(report.Check1Count)}");
        summary.AppendLine($"CHECK 2 — Lost volume:         {StatusLine(report.Check2Count)}");
        summary.AppendLine($"CHECK 3 — Release consistency: {StatusLine(report.Check3Count)}");
        summary.AppendLine($"CHECK 4 — Orphan mappings:     {StatusLine(report.Check4Count)}");
        summary.AppendLine($"CHECK 5 — Duplicate presence:  {StatusLine(report.Check5Count, informational: true)}");
        summary.AppendLine();
        summary.AppendLine(report.IsHealthy
            ? "SYSTEM HEALTH: OK"
            : $"SYSTEM HEALTH: NOT OK — {report.Check1Count + report.Check2Count + report.Check3Count + report.Check4Count} structural violation(s)");
        if (logPath is not null)
        {
            summary.AppendLine();
            summary.Append($"Report: {logPath}");
        }

        await new InfoDialog(
            report.IsHealthy ? "Integrity OK" : "Integrity Violations Found",
            summary.ToString())
            .ShowDialog(this);
    }

    // ── Image Cache Regeneration ──────────────────────────────────────────────

    private async void OnRegenerateImageCache(object? sender, RoutedEventArgs e)
    {
        var imageDir  = Path.Combine(_dataDir, "systemimages");
        var writeLog  = _catalog.GetBoolSetting("image_cache_regen_write_log", defaultValue: true);
        var startTime = DateTime.UtcNow;
        var dialog    = new ImageCacheProgressDialog("Regenerate Image Cache");

        Systems.ImageCacheResult? result = null;
        var progress = new Progress<Systems.ImageCacheProgress>(p =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Update(p)));

        var workTask = Task.Run(() => result = RunImageCacheRegen(imageDir, progress));

        _ = workTask.ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (t.IsFaulted)
                    dialog.SetFailed(t.Exception!.InnerException?.Message ?? t.Exception.Message);
                else
                    dialog.SetCompleted(result!);
            }),
            System.Threading.Tasks.TaskContinuationOptions.None);

        await dialog.ShowDialog<bool>(this);

        if (writeLog && result is not null && result.Success)
            WriteImageCacheLog(result, startTime);
    }

    private static Systems.ImageCacheResult RunImageCacheRegen(
        string imageDir, IProgress<Systems.ImageCacheProgress> progress)
    {
        var result = new Systems.ImageCacheResult();

        if (!Directory.Exists(imageDir))
        {
            progress.Report(new() { PhaseText = "No images directory found.", IsIndeterminate = false });
            return result;
        }

        var sourceFiles = Directory.GetFiles(imageDir, "*_source.png");
        int total       = sourceFiles.Length;

        progress.Report(new()
        {
            PhaseText       = $"Found {total} source file(s).",
            IsIndeterminate = false,
            Total           = total,
            Processed       = 0,
            Generated       = 0,
        });

        foreach (var sourceFile in sourceFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(sourceFile);
            if (!baseName.EndsWith("_source", StringComparison.OrdinalIgnoreCase)) continue;

            var idAndRole = baseName[..^"_source".Length];
            string? role = null, platformId = null;
            foreach (var r in (string[])["logo", "details"])
            {
                if (idAndRole.EndsWith($"-{r}", StringComparison.OrdinalIgnoreCase))
                {
                    role       = r;
                    platformId = idAndRole[..^(r.Length + 1)];
                    break;
                }
            }
            if (role is null || platformId is null) continue;

            result.SourcesProcessed++;
            var fileName = Path.GetFileName(sourceFile);

            var sourceOp = new Ingestion.IngestionOperation(fileName, "SOURCE", sourceFile);
            result.Operations.Add(sourceOp);
            progress.Report(new()
            {
                PhaseText    = $"Processing {fileName}…",
                IsIndeterminate = false,
                Total        = total,
                Processed    = result.SourcesProcessed,
                Generated    = result.CachedGenerated,
                NewOperation = sourceOp,
            });

            foreach (var (w, h) in Systems.PlatformImageSizes.All)
            {
                var cachedName = Systems.PlatformImageCache.CachedFileName(platformId, role, w, h);
                var cachedPath = Path.Combine(imageDir, cachedName);
                try
                {
                    Systems.PlatformImageCache.GenerateSingle(sourceFile, cachedPath, w, h);
                    result.CachedGenerated++;

                    var cacheOp = new Ingestion.IngestionOperation(fileName, "CACHE", cachedPath);
                    result.Operations.Add(cacheOp);
                    progress.Report(new()
                    {
                        IsIndeterminate = false,
                        Total           = total,
                        Processed       = result.SourcesProcessed,
                        Generated       = result.CachedGenerated,
                        NewOperation    = cacheOp,
                    });
                }
                catch (Exception ex)
                {
                    var errOp = new Ingestion.IngestionOperation(fileName, "cache-failed", ex.Message);
                    result.Operations.Add(errOp);
                    progress.Report(new()
                    {
                        IsIndeterminate = false,
                        Total           = total,
                        Processed       = result.SourcesProcessed,
                        Generated       = result.CachedGenerated,
                        NewOperation    = errOp,
                    });
                }
            }

            foreach (var w in Systems.PlatformImageSizes.AllWidthConstrained)
            {
                var cachedName = Systems.PlatformImageCache.CachedWidthFileName(platformId, role, w);
                var cachedPath = Path.Combine(imageDir, cachedName);
                try
                {
                    Systems.PlatformImageCache.GenerateSingleWidthConstrained(sourceFile, cachedPath, w);
                    result.CachedGenerated++;

                    var cacheOp = new Ingestion.IngestionOperation(fileName, "CACHE", cachedPath);
                    result.Operations.Add(cacheOp);
                    progress.Report(new()
                    {
                        IsIndeterminate = false,
                        Total           = total,
                        Processed       = result.SourcesProcessed,
                        Generated       = result.CachedGenerated,
                        NewOperation    = cacheOp,
                    });
                }
                catch (Exception ex)
                {
                    var errOp = new Ingestion.IngestionOperation(fileName, "cache-failed", ex.Message);
                    result.Operations.Add(errOp);
                    progress.Report(new()
                    {
                        IsIndeterminate = false,
                        Total           = total,
                        Processed       = result.SourcesProcessed,
                        Generated       = result.CachedGenerated,
                        NewOperation    = errOp,
                    });
                }
            }
        }

        progress.Report(new()
        {
            PhaseText       = "Done.",
            IsIndeterminate = false,
            Total           = total,
            Processed       = result.SourcesProcessed,
            Generated       = result.CachedGenerated,
        });

        return result;
    }

    private static void WriteImageCacheLog(Systems.ImageCacheResult result, DateTime startTime)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs", "imagecache");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"image_cache_regen_{startTime:yyyyMMdd-HHmmss}.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Image Cache Regeneration Log");
            sb.AppendLine($"Generated: {startTime:o}");
            sb.AppendLine();
            foreach (var op in result.Operations)
                sb.AppendLine($"{op.Action,-14} | {op.Object,-40} | {op.Destination}");
            sb.AppendLine();
            sb.AppendLine($"Sources processed:     {result.SourcesProcessed}");
            sb.AppendLine($"Cache files generated: {result.CachedGenerated}");
            File.WriteAllText(logPath, sb.ToString());
        }
        catch { /* non-fatal */ }
    }

    // ── Operations ───────────────────────────────────────────────────────────

    private ToolRecord?                    _selectedTool;
    private Border?                        _selectedToolBorder;
    private readonly Dictionary<string, Border> _toolBorders = new();

    private List<TransformRecord>               _transforms       = [];
    private TransformRecord?                    _editingTransform;
    private Border?                             _selectedTransformBorder;
    private readonly Dictionary<string, Border> _transformBorders = new();

    private void InitOperations(string? selectToolId = null)
    {
        var appRoot = AppContext.BaseDirectory;
        var tools   = _catalog.LoadTools();

        _selectedTool           = null;
        _selectedToolBorder     = null;
        ToolEditBtn.IsEnabled   = false;
        ToolDeleteBtn.IsEnabled = false;
        _toolBorders.Clear();

        OperationsToolsPanel.Children.Clear();
        foreach (var tool in tools)
        {
            var exePath = Path.Combine(appRoot, "tools", tool.FolderName, tool.ExecutableName);
            var present = File.Exists(exePath);

            var originText    = tool.IsBundled ? "BUNDLED" : "CUSTOM";
            var originColor   = tool.IsBundled ? "#29B6F6" : "#888899";
            var presenceText  = present ? "PRESENT" : "MISSING";
            var presenceColor = present ? "#4CAF50" : "#EF5350";
            var pathColor     = present ? "#555566" : "#EF5350";

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("180,Auto,Auto,*") };
            var nameBlock = new TextBlock
            {
                Text              = tool.Id,
                FontSize          = 12,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse("#CCCCDD")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var originBlock = new TextBlock
            {
                Text              = originText,
                FontSize          = 10,
                FontWeight        = Avalonia.Media.FontWeight.SemiBold,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse(originColor)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin            = new Avalonia.Thickness(0, 0, 8, 0),
            };
            var presenceBlock = new TextBlock
            {
                Text              = presenceText,
                FontSize          = 10,
                FontWeight        = Avalonia.Media.FontWeight.SemiBold,
                Foreground        = new SolidColorBrush(Avalonia.Media.Color.Parse(presenceColor)),
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
            Avalonia.Controls.Grid.SetColumn(nameBlock,     0);
            Avalonia.Controls.Grid.SetColumn(originBlock,   1);
            Avalonia.Controls.Grid.SetColumn(presenceBlock, 2);
            Avalonia.Controls.Grid.SetColumn(pathBlock,     3);
            row.Children.Add(nameBlock);
            row.Children.Add(originBlock);
            row.Children.Add(presenceBlock);
            row.Children.Add(pathBlock);

            var rowBorder = new Border
            {
                Background    = Brushes.Transparent,
                CornerRadius  = new Avalonia.CornerRadius(3),
                Padding       = new Avalonia.Thickness(4, 2),
                Child         = row,
                Cursor        = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            var capturedTool   = tool;
            var capturedBorder = rowBorder;
            rowBorder.PointerPressed += (_, _) => SelectTool(capturedTool, capturedBorder);
            _toolBorders[tool.Id] = rowBorder;
            OperationsToolsPanel.Children.Add(rowBorder);
        }

        // Restore selection if requested
        if (selectToolId != null && _toolBorders.TryGetValue(selectToolId, out var selBorder))
        {
            var selTool = tools.FirstOrDefault(t => t.Id == selectToolId);
            if (selTool != null) SelectTool(selTool, selBorder);
        }

        _transforms = _catalog.LoadTransforms();
        BuildTransformListPanel();
        TransformEditorPanel.IsVisible = false;
    }

    private void SelectTool(ToolRecord tool, Border border)
    {
        if (_selectedToolBorder != null)
            _selectedToolBorder.Background = Brushes.Transparent;

        border.Background   = new SolidColorBrush(Avalonia.Media.Color.Parse("#1A1A2C"));
        _selectedToolBorder = border;
        _selectedTool       = tool;

        ToolEditBtn.IsEnabled   = true;
        ToolDeleteBtn.IsEnabled = !tool.IsBundled;
    }

    private async void OnDeleteTool(object? sender, RoutedEventArgs e)
    {
        if (_selectedTool is not ToolRecord tool) return;
        if (tool.IsBundled) return;

        if (_catalog.ToolHasDependencies(tool.Id))
        {
            await new InfoDialog(
                "Cannot Delete Tool",
                "This tool is used by one or more transforms and cannot be deleted.\n\n" +
                "Please update or remove those transforms before deleting this tool.")
                .ShowDialog(this);
            return;
        }

        var confirmed = await new ConfirmDialog(
            "Delete Tool",
            $"This will permanently remove tool \"{tool.Id}\" from the catalog.\n\n" +
            "This action cannot be undone. Proceed?")
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        _catalog.DeleteTool(tool.Id);
        _selectedTool           = null;
        _selectedToolBorder     = null;
        ToolEditBtn.IsEnabled   = false;
        ToolDeleteBtn.IsEnabled = false;
        InitOperations();
    }

    private async void OnAddTool(object? sender, RoutedEventArgs e)
    {
        var existingIds = _catalog.LoadTools().Select(t => t.Id);
        var dialog      = new ToolDialog(existingIds, null);
        var ok          = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.Result is not ToolRecord tool) return;

        _catalog.SaveTool(tool);
        InitOperations(tool.Id);
    }

    private async void OnEditTool(object? sender, RoutedEventArgs e)
    {
        if (_selectedTool is not ToolRecord current) return;

        var existingIds = _catalog.LoadTools()
            .Where(t => t.Id != current.Id)
            .Select(t => t.Id);
        var dialog = new ToolDialog(existingIds, current);
        var ok     = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.Result is not ToolRecord tool) return;

        _catalog.SaveTool(tool);
        InitOperations(tool.Id);
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

        var fileItems   = _transforms.Where(t => t.ProcessorType != "folder_oriented").ToList();
        var folderItems = _transforms.Where(t => t.ProcessorType == "folder_oriented").ToList();

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
            bool isFolder       = xform.IsFolderOriented;
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

        TransformNameBox.Text              = t.Name;
        TransformIdText.Text               = t.Id;
        TransformToolText.Text             = t.ToolId.Length > 0 ? t.ToolId : "(none)";
        TransformTypeBox.SelectedIndex       = t.ProcessorType == "folder_oriented" ? 1 : 0;
        TransformOutputKindBox.SelectedIndex = t.OutputKind == "folder" ? 1 : 0;
        TransformArchiveTierBox.SelectedIndex = t.ArchiveTier switch { "B" => 1, "C" => 2, _ => 0 };
        TransformCmdBox.Text               = t.CommandTemplate;
        TransformOutputExtBox.Text         = t.OutputExtension;
        TransformDeleteBtn.IsEnabled   = t.Id.StartsWith("custom_");
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

        var processorType = TransformTypeBox.SelectedIndex == 1 ? "folder_oriented" : "file_oriented";
        var outputKind    = TransformOutputKindBox.SelectedIndex == 1 ? "folder" : "file";
        var xformType     = processorType == "folder_oriented" ? "folder_strategy" : "file_strategy";
        var archiveTier   = TransformArchiveTierBox.SelectedIndex switch { 1 => "B", 2 => "C", _ => "A" };

        var updated = new TransformRecord
        {
            Id              = t.Id,
            Name            = name,
            ToolId          = t.ToolId,
            ProcessorType   = processorType,
            OutputKind      = outputKind,
            TransformType   = xformType,
            CommandTemplate = cmd,
            OutputExtension = TransformOutputExtBox.Text?.Trim() ?? "",
            IsEnabled       = t.IsEnabled,
            ArchiveTier     = archiveTier,
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
            ProcessorType   = "file_oriented",
            OutputKind      = "file",
            TransformType   = "file_strategy",
            ArchiveTier     = "A",
        };
        _transforms.Add(draft);
        BuildTransformListPanel(draft.Id);
    }

    private async void OnDeleteTransform(object? sender, RoutedEventArgs e)
    {
        if (_editingTransform is not TransformRecord t)
            return;

        if (!t.Id.StartsWith("custom_"))
            return;

        if (_catalog.TransformHasDependencies(t.Id))
        {
            await new InfoDialog(
                "Cannot Delete Transform",
                $"\"{t.Name}\" is still referenced by one or more DAT line configurations.\n\n" +
                "Remove all DAT line references to this transform before deleting it.")
                .ShowDialog(this);
            return;
        }

        var confirmed = await new ConfirmDialog(
            "Delete Transform",
            $"This will permanently delete \"{t.Name}\".\n\n" +
            "This action cannot be undone.")
            .ShowDialog<bool>(this);
        if (!confirmed) return;

        _catalog.DeleteTransform(t.Id);
        _transforms            = _catalog.LoadTransforms();
        _editingTransform      = null;
        TransformEditorPanel.IsVisible = false;
        TransformDeleteBtn.IsEnabled   = false;
        BuildTransformListPanel();
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

        if (btn == NavLibrary)
            ApplySystemsContextToLibrary();

        if (btn == NavCatalog)
            SyncCatalogContext();

        if (btn == NavSettings)
            LoadAllSettings();

        if (btn == NavProviders && ProvidersRomScrapersPanel.IsVisible)
            LoadScraperSettings();

        if (btn == NavOperations)
            InitOperations();

        if (btn == NavAnalytics)
            BuildAnalytics();

        if (btn == NavLogs)
            BuildLogsTree();

        if (btn == NavBackups)
            InitBackups();
    }

    // ── Analytics ─────────────────────────────────────────────────────────────

    private sealed record AnalyticsData(
        long                       TotalSourceBytes,
        long                       TotalDerivedBytes,
        long                       SavedBytes,
        double                     SavedPct,
        Dictionary<string, long>   DerivedByStrategy,
        Dictionary<string, int>    ExtensionCounts,
        int RelMissing, int RelPending, int RelOutdated, int RelPresent, int RelLost, int RelUnwanted,
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
        int  totalDerivedCount = 0;
        var  byStrategy   = new Dictionary<string, long>(StringComparer.Ordinal);
        var  extCounts    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int  relMissing = 0, relPending = 0, relOutdated = 0, relPresent = 0, relLost = 0, relUnwanted = 0;

        foreach (var dl in datLines)
        {
            if (dl.DataStorePath.Length == 0) continue;
            var dbPath = Path.Combine(_dataDir, dl.DataStorePath);
            if (!File.Exists(dbPath)) continue;
            var store   = new DatLineStore(dbPath);
            var summary = store.GetAnalyticsSummary();

            totalSource       += summary.TotalSourceBytes;
            totalDerived      += summary.TotalDerivedBytes;
            totalDerivedCount += summary.TotalDerivedCount;
            foreach (var (sid, bytes) in summary.DerivedByStrategy)
                byStrategy[sid] = byStrategy.GetValueOrDefault(sid) + bytes;
            foreach (var (ext, cnt) in summary.ExtensionCounts)
                extCounts[ext] = extCounts.GetValueOrDefault(ext) + cnt;

            var (m, pe, o, pr, l, u) = store.GetAllStatusCounts();
            relMissing  += m;  relPending  += pe;
            relOutdated += o;  relPresent  += pr;
            relLost     += l;  relUnwanted += u;
        }

        long   savedBytes = Math.Max(0L, totalSource - totalDerived);
        double savedPct   = totalSource > 0 ? savedBytes * 100.0 / totalSource : 0.0;

        _analyticsData = new AnalyticsData(
            totalSource, totalDerived, savedBytes, savedPct,
            byStrategy, extCounts,
            relMissing, relPending, relOutdated, relPresent, relLost, relUnwanted,
            volumes, platNames, dlNames, stratNames);

        // ── KPI strip ─────────────────────────────────────────────────────────
        AnalyticsKpiSourceSize.Text      = FormatBytes(totalSource);
        AnalyticsKpiDerivedSize.Text     = FormatBytes(totalDerived);
        AnalyticsKpiSavedPct.Text        = totalSource > 0 ? $"{savedPct:F1}%" : "—";
        AnalyticsKpiSavedAbs.Text        = totalSource > 0 ? FormatBytes(savedBytes) : "—";
        AnalyticsKpiVolumes.Text         = volumes.Count.ToString("N0");
        int critCount = volumes.Count(v => v.Health == "crit");
        AnalyticsKpiCritVolumes.Text     = critCount.ToString("N0");
        AnalyticsKpiStoredArtifacts.Text = totalDerivedCount.ToString("N0");
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
        AnalyticsBuildSectionE(relMissing, relPending, relOutdated, relPresent, relLost, relUnwanted, volumes);
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
            bool   isEmpty     = vol.ActualSizeBytes == 0;
            double fill        = Math.Clamp((double)vol.ActualSizeBytes / maxActual, 0.0, 1.0);
            bool   isLost      = vol.Status == "lost";
            Color  color       = isLost || vol.Health == "crit"
                                 ? Color.Parse("#EF5350")
                                 : Color.Parse("#4CAF50");
            string healthLabel = isLost ? "LOST" : vol.Health;
            AnalyticsVolumeHeatmapPanel.Children.Add(
                MakeHeatmapRow(vol.Label, secondary, healthLabel,
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

            // Col 3: Health badge — ok=green, warning=orange, crit=red, LOST=red
            {
                var (healthColor, healthBg) = health switch
                {
                    "crit"    => (Color.Parse("#FF5252"), Color.Parse("#3A0A0A")),
                    "warning" => (Color.Parse("#FF9800"), Color.Parse("#1E1400")),
                    "LOST"    => (Color.Parse("#EF5350"), Color.Parse("#3A0A0A")),
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
        int missing, int pending, int outdated, int present, int lost, int unwanted,
        List<VolumeRecord> volumes)
    {
        // Release status
        AnalyticsReleaseStatusPanel.Children.Clear();
        int relTotal  = missing + pending + outdated + present + lost + unwanted;
        int relWanted = relTotal - unwanted;   // DAT total minus explicitly excluded

        // Coverage metrics
        double wantedCoveragePct = relWanted > 0 ? present * 100.0 / relWanted : 0.0;
        double fullDatCoveragePct = relTotal > 0
            ? (present + unwanted) * 100.0 / relTotal : 0.0;  // unwanted are "done" in DAT terms

        var relRows  = new (string Label, int Count, string Hex)[]
        {
            ("Present",  present,  "#4CAF50"),  // green  — canonical present
            ("Missing",  missing,  "#FFA726"),  // orange
            ("Outdated", outdated, "#FF8A65"),  // salmon
            ("Pending",  pending,  "#FFD54F"),  // amber
            ("Lost",     lost,     "#EF5350"),  // red
            ("Unwanted", unwanted, "#9E9E9E"),  // grey   — user decision, excluded from wanted set
        };
        foreach (var (lbl, cnt, hex) in relRows)
        {
            double pct = relTotal > 0 ? cnt * 100.0 / relTotal : 0.0;
            AnalyticsReleaseStatusPanel.Children.Add(
                MakeBarRow(lbl, cnt, Math.Max(relTotal, 1), $"{cnt:N0} ({pct:F1}%)", Color.Parse(hex),
                           labelWidth: 75, valueWidth: 110));
        }

        // Coverage summary rows
        AnalyticsReleaseStatusPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.Parse("#222233")),
            Margin     = new Avalonia.Thickness(0, 8, 0, 8),
        });
        AnalyticsReleaseStatusPanel.Children.Add(
            MakeCoverageRow("Wanted",   $"{relWanted:N0} releases",    $"{wantedCoveragePct:F1}%",   Color.Parse("#4CAF50")));
        AnalyticsReleaseStatusPanel.Children.Add(
            MakeCoverageRow("Full DAT", $"{relTotal:N0} releases",     $"{fullDatCoveragePct:F1}%",  Color.Parse("#7B68EE")));
        AnalyticsReleaseStatusPanel.Children.Add(
            MakeCoverageRow("Unwanted", $"{unwanted:N0} excluded",     "",                           Color.Parse("#9E9E9E")));

        // Volume health
        AnalyticsVolumeHealthPanel.Children.Clear();
        int volLost = volumes.Count(v => v.Status == "lost");
        int volCrit = volumes.Count(v => v.Status != "lost" && v.Health == "crit");
        int volOk   = volumes.Count(v => v.Status != "lost" && v.Health == "ok");
        int volTotal = volumes.Count;
        var volRows  = new (string Label, int Count, string Hex)[]
        {
            ("OK",       volOk,   "#4CAF50"),
            ("Critical", volCrit, "#EF5350"),
            ("Lost",     volLost, "#EF5350"),
        };
        foreach (var (lbl, cnt, hex) in volRows)
        {
            double pct = volTotal > 0 ? cnt * 100.0 / volTotal : 0.0;
            AnalyticsVolumeHealthPanel.Children.Add(
                MakeBarRow(lbl, cnt, Math.Max(volTotal, 1), $"{cnt:N0} ({pct:F1}%)", Color.Parse(hex),
                           labelWidth: 75, valueWidth: 110));
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Two-column coverage summary row: [label + denominator] [coverage %].
    /// Used in Section E to show Wanted and Full DAT coverage at a glance.
    /// </summary>
    private static Grid MakeCoverageRow(string label, string denominator, string pct, Color pctColor)
    {
        var row = new Grid { Margin = new Avalonia.Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions = new ColumnDefinitions("*,Auto");

        var left = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(new TextBlock
        {
            Text       = label,
            FontSize   = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#AAAACC")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        if (denominator.Length > 0)
            left.Children.Add(new TextBlock
            {
                Text       = denominator,
                FontSize   = 10,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        row.Children.Add(left);

        if (pct.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text       = pct,
                FontSize   = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(pctColor),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                [Grid.ColumnProperty] = 1,
            });
        return row;
    }

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
        sb.Append("<table><thead><tr><th>Label</th><th>System</th><th>Status</th><th>Health</th><th>Size</th></tr></thead><tbody>");
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

    // ── ARK Backups ────────────────────────────────────────────────────────────

    private void SetArkBusy(bool busy)
    {
        _arkBusy = busy;
        ArkCreateBackupBtn.IsEnabled    = !busy;
        ArkRefreshBackupsBtn.IsEnabled  = !busy;
        ArkBackupsList.IsEnabled        = !busy;
        ArkRestoreSelectedBtn.IsEnabled = !busy && ArkBackupsList.SelectedItem is not null;
    }

    private void AppendBackupLog(string line)
    {
        BackupLogText.Text = string.IsNullOrEmpty(BackupLogText.Text)
            ? line
            : BackupLogText.Text + "\n" + line;
    }

    private void RefreshArkBackupsList()
    {
        var folder = ArkUiHelpers.BackupsFolder(AppContext.BaseDirectory);
        var items = new List<ArkBackupItem>();
        if (Directory.Exists(folder))
        {
            foreach (var f in Directory.GetFiles(folder, "*.ark")
                                       .OrderByDescending(x => File.GetLastWriteTime(x)))
            {
                var info   = new FileInfo(f);
                var detail = $"{info.LastWriteTime:yyyy-MM-dd HH:mm}  ·  {AmpReportHelpers.FormatBytes(info.Length)}";
                items.Add(new ArkBackupItem(info.Name, detail, f));
            }
        }
        ArkBackupsList.ItemsSource        = items;
        ArkBackupsEmptyText.IsVisible     = items.Count == 0;
        ArkRestoreSelectedBtn.IsEnabled   = false;
    }

    private void InitBackups()
    {
        RefreshArkBackupsList();
    }

    private async void OnArkCreateBackup(object? sender, RoutedEventArgs e)
    {
        if (_arkBusy) return;

        var backupsFolder = ArkUiHelpers.BackupsFolder(AppContext.BaseDirectory);
        var outputPath    = Path.Combine(backupsFolder, ArkUiHelpers.SuggestedArkFileName());

        SetArkBusy(true);
        BackupLogText.Text = string.Empty;
        AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Planning backup…");

        try
        {
            var options = new ArkExportOptions(
                IncludeMedia:       false,
                IncludeSettings:    true,
                IncludeAmpRegistry: true);

            var planService = new ArkExportPlanService(_dataDir, _catalog);
            var plan        = planService.PlanExport(options);

            if (plan.Issues.Count > 0)
                foreach (var issue in plan.Issues)
                    AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Issue: {issue}");

            AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Writing {plan.DatLineCount} DAT line(s)…");

            var writer = new ArkWriterService(_dataDir, _catalog);
            var result = await Task.Run(() => writer.Write(options, outputPath));

            if (result.Success)
            {
                AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Size: {AmpReportHelpers.FormatBytes(result.PackageBytes)}");
                foreach (var issue in result.Issues)
                    AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Note: {issue}");

                AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Verifying package…");
                var verifier        = new ArkPackageVerifierService();
                var verifyResult    = await Task.Run(() => verifier.Verify(result.OutputPath));
                var hasErrors       = verifyResult.Issues.Any(i => i.Severity == ArkPackageVerificationSeverity.Error);
                if (hasErrors)
                {
                    foreach (var vi in verifyResult.Issues)
                        AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Verify {vi.Severity}: {vi.Message}");
                    AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] WARNING: package verification reported errors.");
                }
                else
                {
                    AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Package verified OK.");
                    AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] BACKUP COMPLETE — {Path.GetFileName(result.OutputPath)}");
                }
                RefreshArkBackupsList();
            }
            else
            {
                AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Backup failed.");
                foreach (var issue in result.Issues)
                    AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Error: {issue}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppendBackupLog($"[{DateTime.Now:HH:mm:ss}] Exception: {ex.Message}");
        }
        finally
        {
            SetArkBusy(false);
        }
    }

    private async void OnArkRestoreSelected(object? sender, RoutedEventArgs e)
    {
        if (_arkBusy) return;
        if (ArkBackupsList.SelectedItem is not ArkBackupItem item) return;

        await new InfoDialog(
            "Restore Not Available",
            "Restore to the live data directory is not supported while Arkadia is running.\n\n" +
            "To restore, close Arkadia and use the restore tool, or extract the .ark package manually.\n\n" +
            "The selected backup is located at:\n" + item.FullPath)
            .ShowDialog(this);
    }

    private void OnArkBackupsSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        ArkRestoreSelectedBtn.IsEnabled = !_arkBusy && ArkBackupsList.SelectedItem is not null;
    }

    private void OnArkRefreshBackups(object? sender, RoutedEventArgs e)
    {
        RefreshArkBackupsList();
    }
}

/// <summary>View-model row for the metadata value mappings settings table.</summary>
public sealed class MappingRowVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Field       { get; }
    public string MatchValue  { get; }
    public string Replacement { get; }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public MappingRowVm(Arkadia.Data.MetadataValueMappingRecord r)
    {
        Field       = r.Field;
        MatchValue  = r.MatchValue;
        Replacement = r.Replacement;
        _enabled    = r.Enabled;
    }
}

// ── ARK Backup item view model ────────────────────────────────────────────────
file sealed record ArkBackupItem(string FileName, string Detail, string FullPath);

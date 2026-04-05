using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Arkadia.Dashboard;
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

    private readonly DashboardLayoutEngine _layoutEngine = new();
    private DashboardLayoutMode _currentMode = DashboardLayoutMode.Compact;

    // ── View registry — maps nav button → content panel ──────────────────────
    private Dictionary<Button, Control> _views = [];

    private static readonly string _dataDir = Path.Combine(AppContext.BaseDirectory, "data");

    private readonly CatalogService _catalog = new(_dataDir);
    private readonly bool _showDebugArtifactInfo;

    public MainWindow()
    {
        InitializeComponent();
        _showDebugArtifactInfo = _catalog.GetBoolSetting("show_debug_artifact_info");

        _navButtons.AddRange([
            NavDashboard, NavSystems, NavPending, NavStaging, NavLibrary, NavVolumes, NavDisks, NavOperations,
            NavLogs, NavSettings,
        ]);

        _views = new()
        {
            [NavDashboard] = ViewDashboard,
            [NavSystems]   = ViewSystems,
            [NavPending]   = ViewPending,
            [NavStaging]   = ViewStaging,
            [NavLibrary]   = ViewLibrary,
            [NavDisks]     = ViewDisks,
            [NavVolumes]   = ViewVolumes,
        };

        InitSystems();
        InitPending();
        InitStaging();
        InitLibrary();
        InitDisks();
        InitVolumes();
        ResolveFlagImages();
        SetActive(NavDashboard);
        InitArchiveFormatsChart();
        InitLogo();
    }

    // ── Systems ──────────────────────────────────────────────────────────────

    private List<SystemPlatform> _systemsPlatforms = [];
    // id → display name, rebuilt on every RefreshSystems
    private Dictionary<string, string> _hardwareTypeMap   = [];
    private Dictionary<string, string> _strategyNameMap   = [];

    private string? _systemsThemeDir;
    private Border? _selectedCardBorder;   // currently highlighted card border
    private SystemPlatform? _selectedPlatform;

    private static readonly Avalonia.Media.IBrush CardNormalBorder   = new SolidColorBrush(Color.Parse("#242433"));
    private static readonly Avalonia.Media.IBrush CardSelectedBorder = new SolidColorBrush(Color.Parse("#7B68EE"));

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
    }

    private void RefreshSystemsKeepSelection(string? platformId)
    {
        RefreshSystems();
        if (platformId is null) return;
        var p = _systemsPlatforms.FirstOrDefault(x => x.Id == platformId);
        if (p is null) return;
        var idx  = _systemsPlatforms.IndexOf(p);
        var card = idx >= 0
            ? SystemsCardPanel.Children.OfType<Border>().ElementAtOrDefault(idx)
            : null;
        SelectCard(card, p);
        SystemsList.SelectedItem = p;
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
                    Missing      = 0,
                    Lost         = 0,
                };
            })
            .ToList();

        BuildSystemsCards();
        SystemsList.ItemsSource = _systemsPlatforms;

        if (_systemsPlatforms.Count > 0)
        {
            SystemsList.SelectedIndex = 0;
            SelectCard(SystemsCardPanel.Children.OfType<Border>().FirstOrDefault(),
                       _systemsPlatforms[0]);
        }
        else
        {
            _selectedPlatform = null;
            UpdateSystemsDetail(null);
        }
    }

    private void BuildSystemsCards()
    {
        SystemsCardPanel.Children.Clear();
        foreach (var p in _systemsPlatforms)
            SystemsCardPanel.Children.Add(MakeSystemCard(p));
    }

    private void SelectCard(Border? card, SystemPlatform p)
    {
        // Deselect previous
        if (_selectedCardBorder is not null)
            _selectedCardBorder.BorderBrush = CardNormalBorder;

        _selectedCardBorder = card;
        if (_selectedCardBorder is not null)
            _selectedCardBorder.BorderBrush = CardSelectedBorder;

        _selectedPlatform = p;
        UpdateSystemsDetail(p);
    }

    private Bitmap? LoadSystemImage(string platformId, string suffix)
    {
        var catalogPath = Path.Combine(_dataDir, "systemimages", $"{platformId}-{suffix}.png");
        if (File.Exists(catalogPath))
            try { return new Bitmap(catalogPath); } catch { }
        return null;
    }

    private Border MakeSystemCard(SystemPlatform p)
    {
        var img = LoadSystemImage(p.Id, "logo")
               ?? (_systemsThemeDir is not null ? SystemImageLoader.Load(_systemsThemeDir, p.Id) : null);

        var imageControl = new Image
        {
            MaxHeight  = 60,
            MaxWidth   = 160,
            Stretch    = Avalonia.Media.Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Margin     = new Avalonia.Thickness(0, 0, 0, 10),
            Source     = img,
            IsVisible  = img is not null,
        };

        var namePlaceholder = new TextBlock
        {
            Text       = p.Name,
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F0F0F0")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin     = new Avalonia.Thickness(0, 0, 0, 8),
            IsVisible  = img is null, // show text fallback only when no image
        };

        var nameAlways = new TextBlock
        {
            Text       = p.Name,
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F0F0F0")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin     = new Avalonia.Thickness(0, img is not null ? 4 : 0, 0, 6),
        };

        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var accent    = new SolidColorBrush(Color.Parse("#7B68EE"));

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(imageControl);
        content.Children.Add(namePlaceholder);
        content.Children.Add(nameAlways);

        var statsGrid = new Grid();
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        void AddStat(string key, string val, bool isAccent = false)
        {
            var row = statsGrid.RowDefinitions.Count;
            statsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var k = new TextBlock { Text = key, FontSize = 11, Foreground = secondary, Margin = new Avalonia.Thickness(0, 2) };
            var v = new TextBlock { Text = val, FontSize = 11, FontWeight = FontWeight.Medium,
                                    Foreground = isAccent ? accent : secondary,
                                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                    Margin = new Avalonia.Thickness(0, 2) };
            Grid.SetRow(k, row); Grid.SetColumn(k, 0);
            Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            statsGrid.Children.Add(k);
            statsGrid.Children.Add(v);
        }

        AddStat("Titles",   $"{p.TotalTitles:N0}");
        AddStat("Present",  $"{p.Present:N0}");
        AddStat("Coverage", p.Coverage, isAccent: true);

        content.Children.Add(statsGrid);

        var card = new Border
        {
            Background      = new SolidColorBrush(Color.Parse("#1A1A24")),
            BorderBrush     = CardNormalBorder,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius    = new Avalonia.CornerRadius(8),
            Padding         = new Avalonia.Thickness(16, 14),
            Margin          = new Avalonia.Thickness(0, 0, 12, 12),
            Width           = 208,
            Cursor          = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child           = content,
        };

        // Capture p for the closure
        var platform = p;
        card.PointerPressed += (_, _) =>
        {
            SelectCard(card, platform);
            // Keep list selection in sync
            SystemsList.SelectedItem = platform;
        };

        return card;
    }

    private void OnSystemsViewToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked) return;
        var isCard = clicked == SystemsCardToggle;
        SystemsCardView.IsVisible  =  isCard;
        SystemsListView.IsVisible  = !isCard;
        SystemsCardToggle.Classes.Set("active",  isCard);
        SystemsListToggle.Classes.Set("active", !isCard);
    }

    private void OnSystemsListSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        var p = SystemsList.SelectedItem as SystemPlatform;
        if (p is null || p == _selectedPlatform) return;

        // Sync card highlight to list selection
        var idx  = _systemsPlatforms.IndexOf(p);
        var card = idx >= 0
            ? SystemsCardPanel.Children.OfType<Border>().ElementAtOrDefault(idx)
            : null;
        SelectCard(card, p);
    }

    private void UpdateSystemsDetail(SystemPlatform? p)
    {
        if (p is null)
        {
            SystemsDetailEmpty.IsVisible   = true;
            SystemsDetailContent.IsVisible = false;
            return;
        }

        SystemsDetailName.Text         = p.Name;
        SystemsDetailId.Text           = p.Id;
        SystemsDetailManufacturer.Text = p.Manufacturer;
        SystemsDetailHardwareType.Text      = p.HardwareType.Length > 0 ? p.HardwareType : "—";
        SystemsDetailHardwareTypeRow.IsVisible = true;
        SystemsDetailDatLines.Text              = p.DatLines.ToString();
        SystemsDetailTotal.Text                 = $"{p.TotalTitles:N0}";
        SystemsDetailPresent.Text               = $"{p.Present:N0}";
        SystemsDetailOutdated.Text              = $"{p.Outdated:N0}";
        SystemsDetailOutdatedRow.IsVisible      = p.Outdated > 0;
        SystemsDetailMissing.Text               = $"{p.Missing:N0}";
        SystemsDetailLost.Text                  = $"{p.Lost:N0}";
        SystemsDetailCoverage.Text              = p.Coverage;

        SystemsDetailImage.Source    = LoadSystemImage(p.Id, "details")
                                    ?? LoadSystemImage(p.Id, "logo")
                                    ?? (_systemsThemeDir is not null ? SystemImageLoader.Load(_systemsThemeDir, p.Id) : null);
        SystemsDetailImage.IsVisible = SystemsDetailImage.Source is not null;

        // Hardware Details section
        var record = _catalog.GetPlatform(p.Id);
        SetHwRow(SystemsHwCpuRow,        SystemsHwCpu,        record?.Cpu);
        SetHwRow(SystemsHwMemoryRow,     SystemsHwMemory,     record?.Memory);
        SetHwRow(SystemsHwGraphicsRow,   SystemsHwGraphics,   record?.Graphics);
        SetHwRow(SystemsHwSoundRow,      SystemsHwSound,      record?.Sound);
        SetHwRow(SystemsHwResolutionRow, SystemsHwResolution, record?.DisplayResolution);
        SetHwRow(SystemsHwAspectRow,     SystemsHwAspect,     record?.AspectRatio);
        SystemsHardwareDetailsSection.IsVisible =
            !string.IsNullOrEmpty(record?.Cpu)               ||
            !string.IsNullOrEmpty(record?.Memory)            ||
            !string.IsNullOrEmpty(record?.Graphics)          ||
            !string.IsNullOrEmpty(record?.Sound)             ||
            !string.IsNullOrEmpty(record?.DisplayResolution) ||
            !string.IsNullOrEmpty(record?.AspectRatio);

        // DAT lines — static seed first, then any persisted catalog entries
        SystemsDatLinesList.Children.Clear();
        foreach (var d in BuildDatLineInfos(p.Id))
            SystemsDatLinesList.Children.Add(MakeDatLineRow(d));

        SystemsDetailEmpty.IsVisible   = false;
        SystemsDetailContent.IsVisible = true;
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
                    Name:              dl.Name,
                    Releases:          dl.ReleaseCount,
                    Outdated:          outdated,
                    LastImport:        dl.ImportedAtUtc.ToString("yyyy-MM-dd"),
                    StorageStrategy:   _strategyNameMap.TryGetValue(dl.StorageStrategyId, out var sn) ? sn : "",
                    Authority:         dl.Authority,
                    DatCategory:       dl.DatCategory,
                    DataStorePath:     dl.DataStorePath,
                    CatalogId:         dl.Id,
                    CatalogPlatformId: dl.PlatformId);
            })
            .ToList();

    private Control MakeDatLineRow(DatLineInfo d)
    {
        var secondary = new SolidColorBrush(Color.Parse("#888899"));

        // Line 1 — DAT line label
        var nameBlock = new TextBlock
        {
            Text         = d.Name,
            FontSize     = 12,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = new SolidColorBrush(Color.Parse("#D0D0E0")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        // Line 2 — release count (+ outdated if any)
        var countsText = d.Outdated > 0
            ? $"{d.Releases:N0} releases · {d.Outdated:N0} outdated"
            : $"{d.Releases:N0} releases";
        var countsBlock = new TextBlock
        {
            Text         = countsText,
            FontSize     = 11,
            Foreground   = secondary,
            Margin       = new Avalonia.Thickness(0, 3, 0, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        // Line 3 — last import date
        var importBlock = new TextBlock
        {
            Text       = $"Last import {d.LastImport}",
            FontSize   = 11,
            Foreground = secondary,
            Margin     = new Avalonia.Thickness(0, 2, 0, 0),
        };

        var row = new StackPanel
        {
            Spacing  = 0,
            Margin   = new Avalonia.Thickness(0, 0, 0, 10),
            Children = { nameBlock, countsBlock, importBlock },
        };

        // Line 4 — storage strategy (optional)
        if (d.StorageStrategy.Length > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text       = $"Storage: {d.StorageStrategy}",
                FontSize   = 11,
                Foreground = secondary,
                Margin     = new Avalonia.Thickness(0, 2, 0, 0),
            });
        }

        // Line 5 — actions
        var actionsPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 14,
            Margin      = new Avalonia.Thickness(0, 7, 0, 0),
        };

        if (d.LibraryPlatform is not null && d.LibraryDatLine is not null)
        {
            var libPlatform = d.LibraryPlatform;
            var libDatLine  = d.LibraryDatLine;
            var link        = new TextBlock { Text = "Open in Library →" };
            link.Classes.Add("text-action");
            link.Classes.Add("accent");
            link.PointerPressed += (_, _) => NavigateToLibrary(libPlatform, libDatLine);
            actionsPanel.Children.Add(link);
        }

        if (d.CatalogId is not null && d.CatalogPlatformId is not null)
        {
            var catalogId         = d.CatalogId;
            var catalogPlatformId = d.CatalogPlatformId;
            var datLineInfo       = d;

            var ingestLink = new TextBlock { Text = "Ingest Files" };
            ingestLink.Classes.Add("text-action");
            ingestLink.Classes.Add("accent");
            ingestLink.PointerPressed += (_, _) => OnIngestDatLine(datLineInfo);
            actionsPanel.Children.Add(ingestLink);

            var updateLink = new TextBlock { Text = "Update DAT" };
            updateLink.Classes.Add("text-action");
            updateLink.Classes.Add("accent");
            updateLink.PointerPressed += async (_, _) => await OnUpdateDatLine(datLineInfo);
            actionsPanel.Children.Add(updateLink);

            var deleteLink = new TextBlock { Text = "Delete" };
            deleteLink.Classes.Add("text-action");
            deleteLink.Classes.Add("danger");
            deleteLink.PointerPressed += async (_, _) => await OnDeleteDatLine(catalogId, catalogPlatformId, datLineInfo);
            actionsPanel.Children.Add(deleteLink);
        }

        if (actionsPanel.Children.Count > 0)
            row.Children.Add(actionsPanel);

        return row;
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
        var dialog = new CreateDiskDialog();
        var ok     = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.Result is null) return;

        _catalog.SaveDisk(dialog.Result);
        RefreshDisks();
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
            DiskVolumeList.Children.Add(new Grid
            {
                ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto"),
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
                Children =
                {
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 0,
                        Text = v.Label,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1,
                        Text = FormatBytes(v.ActualSizeBytes),
                        FontSize = 12,
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
        var colors = new[]
        {
            "#5C6BC0", "#26A69A", "#EF5350", "#FFA726", "#66BB6A",
            "#AB47BC", "#42A5F5", "#EC407A", "#8D6E63", "#78909C",
        };
        int colorIdx = 0;
        const double BarWidth = 260.0; // detail panel is 300 - 40 margin

        foreach (var v in volumes)
        {
            if (v.ActualSizeBytes <= 0) continue;
            var ratio = Math.Clamp((double)v.ActualSizeBytes / disk.DeclaredCapacityBytes, 0, 1);
            var segW  = Math.Max(2, ratio * BarWidth);
            panel.Children.Add(new Border
            {
                Width      = segW,
                Height     = 14,
                Background = new SolidColorBrush(Color.Parse(colors[colorIdx % colors.Length])),
            });
            colorIdx++;
        }

        // Free space segment
        var usedRatio = Math.Clamp((double)disk.UsedBytes / disk.DeclaredCapacityBytes, 0, 1);
        var freeW     = Math.Max(0, (1 - usedRatio) * BarWidth);
        if (freeW > 0)
            panel.Children.Add(new Border
            {
                Width      = freeW,
                Height     = 14,
                Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            });

        DiskSegmentBar.Child = panel;
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
                : loc.LocationType;

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

    private async void OnAssignVolumeToDisk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;

        var disks  = _catalog.GetDisks();
        var dialog = new AssignVolumeToDiskDialog(entry.Label, disks);
        var ok     = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.SelectedDisk is null) return;

        var loc = new Data.VolumeLocationRecord
        {
            Id           = Guid.NewGuid().ToString("N"),
            VolumeId     = entry.Id,
            LocationType = "disk",
            DiskId       = dialog.SelectedDisk.Id,
            Path         = null,
            IsCurrent    = true,
            CreatedAt    = DateTime.UtcNow,
        };
        _catalog.SetCurrentLocation(loc);
        RefreshVolumes();
        RefreshDisks();
        // Re-select the same volume in the refreshed list
        var updated = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updated is not null)
        {
            VolumesList.SelectedItem = updated;
            UpdateVolumeDetailPanel(updated);
        }
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
        VolumeDetailArtifactList.Children.Clear();
        var assignments = _catalog.GetVolumeArtifacts(entry.Id);
        VolumeDetailArtifactCount.Text = assignments.Count.ToString();

        if (assignments.Count > 0 && entry.DbPath.Length > 0 && File.Exists(entry.DbPath))
        {
            var store       = new DatLineStore(entry.DbPath);
            var derivedById = store.GetDerivedArtifacts()
                .ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);

            foreach (var va in assignments)
            {
                if (!derivedById.TryGetValue(va.DerivedArtifactId, out var da)) continue;
                VolumeDetailArtifactList.Children.Add(new Grid
                {
                    ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("*,Auto"),
                    Margin = new Avalonia.Thickness(0, 0, 0, 2),
                    Children =
                    {
                        new TextBlock
                        {
                            [Grid.ColumnProperty] = 0,
                            Text         = da.FileName,
                            FontSize     = 11,
                            Foreground   = new SolidColorBrush(Color.Parse("#AAAACC")),
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            [Grid.ColumnProperty] = 1,
                            Text       = FormatBytes(da.SizeBytes),
                            FontSize   = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#555566")),
                        },
                    },
                });
            }
        }

        VolumeDetailEmpty.IsVisible   = false;
        VolumeDetailContent.IsVisible = true;
    }

    private async void OnAssignDerivedArtifacts(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = VolumesList.SelectedItem as VolumeEntry;
        if (entry is null) return;

        try
        {
            await AssignDerivedArtifactsCore(entry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await new InfoDialog("Error", ex.Message).ShowDialog(this);
        }
    }

    private async System.Threading.Tasks.Task AssignDerivedArtifactsCore(VolumeEntry entry)
    {
        if (entry.DbPath.Length == 0)
        {
            await new InfoDialog(
                "No DAT Line DB",
                $"Volume \"{entry.Label}\" is linked to DAT line \"{entry.DatLineId}\" but no matching " +
                "DAT line database was found in the catalog.\n\n" +
                "Import the DAT line first before assigning artifacts.")
                .ShowDialog(this);
            return;
        }

        if (!File.Exists(entry.DbPath))
        {
            await new InfoDialog(
                "DAT Line DB Missing",
                $"The DAT line database file could not be found on disk:\n{entry.DbPath}")
                .ShowDialog(this);
            return;
        }

        var store     = new DatLineStore(entry.DbPath);
        var artifacts = store.GetDerivedArtifacts();

        if (artifacts.Count == 0)
        {
            await new InfoDialog(
                "No Archive Artifacts",
                $"No archive artifacts exist for DAT line \"{entry.DatLineId}\" yet.\n\n" +
                "Run ingestion on this DAT line first. Archive artifacts are created " +
                "automatically when releases are promoted to source.")
                .ShowDialog(this);
            return;
        }

        // Build set of already-assigned archive artifact IDs for this volume
        var existing = _catalog.GetVolumeArtifacts(entry.Id)
            .Select(va => va.DerivedArtifactId)
            .ToHashSet(StringComparer.Ordinal);

        var dialog = new AssignDerivedArtifactsDialog(entry.Label, artifacts, existing);
        var ok     = await dialog.ShowDialog<bool>(this);
        if (!ok || dialog.SelectedArtifacts.Count == 0) return;

        var now          = DateTime.UtcNow;
        var sizeByDrvId  = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var da in dialog.SelectedArtifacts)
        {
            if (_catalog.VolumeArtifactExists(entry.Id, da.Id)) continue;

            _catalog.SaveVolumeArtifact(new Data.VolumeArtifactRecord
            {
                Id                = Guid.NewGuid().ToString("N"),
                VolumeId          = entry.Id,
                DatLineId         = entry.RawDatLineId,
                DerivedArtifactId = da.Id,
                Status            = "present_in_final",
                AddedAtUtc        = now,
            });
            sizeByDrvId[da.Id] = da.SizeBytes;
        }

        // Build full size map for recalculation (include previously assigned artifacts too)
        foreach (var da in artifacts)
            sizeByDrvId.TryAdd(da.Id, da.SizeBytes);

        _catalog.RecalculateVolumeActualSize(entry.Id, sizeByDrvId);

        RefreshVolumes();
        RefreshDisks();

        // Re-select the same volume
        var updated = _filteredVolumes.FirstOrDefault(v => v.Id == entry.Id);
        if (updated is not null)
        {
            VolumesList.SelectedItem = updated;
            UpdateVolumeDetailPanel(updated);
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

    // ── Library ───────────────────────────────────────────────────────────────

    private void InitLibrary()
    {
        RebuildLibraryDatasets();
        LibraryStatusFilter.ItemsSource   = new[] { "All Statuses", "Present", "Missing", "Lost" };
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
                        Name      = r.Name,
                        Platform  = platformName,
                        Status    = Capitalize(r.Status),
                        Region    = r.Region,
                        Languages = r.Languages.ToUpperInvariant(),
                        Format    = r.Format,
                        Size      = r.Size,
                        Tier      = r.Tier,
                        RomFiles  = romFiles ?? [],
                        ReleaseId = r.Id,
                        DbPath    = absPath,
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
            .Where(e => status == "All Statuses" || e.Status == status)
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
        DetailTier.Text             = entry.TierDisplay;
        DetailTier.Foreground       = entry.TierBrush;
        DetailRegion.Text           = entry.Region;
        DetailLanguages.Text        = entry.Languages;
        DetailFormat.Text           = entry.Format;
        DetailSize.Text             = entry.Size;

        // ROM FILES
        DetailDatFiles.Children.Clear();
        if (entry.RomFiles.Count > 0)
            foreach (var f in entry.RomFiles)
                DetailDatFiles.Children.Add(MakeRomFileRow(f));
        else
            DetailDatFiles.Children.Add(new TextBlock
            {
                Text       = "No ROM files on record",
                FontSize   = 12,
                Foreground = new SolidColorBrush(Color.Parse("#555566")),
            });

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

    private static Control MakeRomFileRow(Data.ReleaseFileRecord f)
    {
        var primary   = new SolidColorBrush(Color.Parse("#D0D0E0"));
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var mono      = new FontFamily("Consolas,Courier New,monospace");

        static TextBlock Label(string text, SolidColorBrush fg, FontFamily? ff = null)
            => new TextBlock
            {
                Text       = text,
                FontSize   = 11,
                Foreground = fg,
                FontFamily = ff ?? FontFamily.Default,
            };

        var panel = new StackPanel { Spacing = 2 };

        panel.Children.Add(new TextBlock
        {
            Text       = f.RomName.Length > 0 ? f.RomName : "(unnamed)",
            FontSize   = 12,
            FontWeight = FontWeight.Medium,
            Foreground = primary,
        });

        if (f.Size.Length > 0)
            panel.Children.Add(Label($"Size  {f.Size}", secondary));
        if (f.Crc.Length > 0)
            panel.Children.Add(Label($"CRC   {f.Crc}", secondary, mono));
        if (f.Md5.Length > 0)
            panel.Children.Add(Label($"MD5   {f.Md5}", secondary, mono));
        if (f.Sha1.Length > 0)
            panel.Children.Add(Label($"SHA1  {f.Sha1}", secondary, mono));

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

    private void InitArchiveFormatsChart()
    {
        ArchiveFormatsChart.Segments =
        [
            new DonutSegment { Value = 50, Fill = new SolidColorBrush(Color.Parse("#7B68EE")) }, // .iso
            new DonutSegment { Value = 40, Fill = new SolidColorBrush(Color.Parse("#4A90D9")) }, // .chd
            new DonutSegment { Value = 10, Fill = new SolidColorBrush(Color.Parse("#3D7A5C")) }, // .cue
        ];
    }

    // ── Layout engine wiring ──────────────────────────────────────────────────

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyDashboardLayout(e.NewSize.Width - SidebarWidth);
    }

    private void ApplyDashboardLayout(double contentWidth)
    {
        var mode    = DashboardLayoutEngine.ResolveMode(contentWidth);
        var visible = _layoutEngine.ResolveWidgets(mode);
        var ids     = new HashSet<string>(visible.Select(w => w.Id));

        // Widget visibility (unchanged from v1)
        CardLibraryCoverage.IsVisible     = ids.Contains(DashboardWidgetId.LibraryCoverage);
        CardVolumes.IsVisible             = ids.Contains(DashboardWidgetId.Volumes);
        CardSystems.IsVisible             = ids.Contains(DashboardWidgetId.Systems);
        CardStorage.IsVisible             = ids.Contains(DashboardWidgetId.Storage);
        CardRecentActivity.IsVisible      = ids.Contains(DashboardWidgetId.RecentActivitySummary);
        WidgetRecentOperations.IsVisible  = ids.Contains(DashboardWidgetId.RecentOperations);
        WidgetAttentionRequired.IsVisible = ids.Contains(DashboardWidgetId.AttentionRequired);
        WidgetArchiveFormats.IsVisible    = ids.Contains(DashboardWidgetId.ArchiveFormats);

        // Slot composition (v1.5): only reconfigure when mode actually changes
        if (mode == _currentMode)
            return;
        _currentMode = mode;
        ApplyMainWidgetsGridSlots(mode);
    }

    /// <summary>
    /// Reconfigures MainWidgetsGrid column/row definitions and each widget's Grid cell
    /// to match the target layout mode. No controls are moved between parents.
    ///
    ///  Compact  (1 col, 3 rows): vertical stack — RecentOps, Attention, ArchiveFormats
    ///  Standard (2 cols, 2 rows): top row = RecentOps | Attention; second row = ArchiveFormats (col-span 2)
    ///  Wide     (3 cols, 1 row):  all three side by side
    /// </summary>
    private void ApplyMainWidgetsGridSlots(DashboardLayoutMode mode)
    {
        switch (mode)
        {
            case DashboardLayoutMode.Compact:
                MainWidgetsGrid.ColumnDefinitions = new ColumnDefinitions("*");
                MainWidgetsGrid.RowDefinitions    = new RowDefinitions("Auto,Auto,Auto");

                Place(WidgetRecentOperations,  row: 0, col: 0, colSpan: 1, margin: new(0, 0, 0, 8));
                Place(WidgetAttentionRequired, row: 1, col: 0, colSpan: 1, margin: new(0, 0, 0, 8));
                Place(WidgetArchiveFormats,    row: 2, col: 0, colSpan: 1, margin: new(0));
                break;

            case DashboardLayoutMode.Standard:
                MainWidgetsGrid.ColumnDefinitions = new ColumnDefinitions("*,8,*");
                MainWidgetsGrid.RowDefinitions    = new RowDefinitions("Auto,Auto");

                // Top row: RecentOps (col 0) | 8px gap col | Attention (col 2)
                Place(WidgetRecentOperations,  row: 0, col: 0, colSpan: 1, margin: new(0, 0, 0, 8));
                Place(WidgetAttentionRequired, row: 0, col: 2, colSpan: 1, margin: new(0, 0, 0, 8));
                // Bottom row: ArchiveFormats spans all 3 columns (including gap column)
                Place(WidgetArchiveFormats,    row: 1, col: 0, colSpan: 3, margin: new(0));
                break;

            case DashboardLayoutMode.Wide:
                MainWidgetsGrid.ColumnDefinitions = new ColumnDefinitions("*,8,*,8,*");
                MainWidgetsGrid.RowDefinitions    = new RowDefinitions("Auto");

                Place(WidgetRecentOperations,  row: 0, col: 0, colSpan: 1, margin: new(0));
                Place(WidgetAttentionRequired, row: 0, col: 2, colSpan: 1, margin: new(0));
                Place(WidgetArchiveFormats,    row: 0, col: 4, colSpan: 1, margin: new(0));
                break;
        }
    }

    private static void Place(Control ctrl, int row, int col, int colSpan, Avalonia.Thickness margin)
    {
        Grid.SetRow(ctrl, row);
        Grid.SetColumn(ctrl, col);
        Grid.SetColumnSpan(ctrl, colSpan);
        ctrl.Margin = margin;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    // ── DAT operations ────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task OnUpdateDatLine(DatLineInfo info)
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
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            WriteIngestionLog(logsDir, platformId, datLineId, ingestResult);
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
        IProgress<IngestionProgress> progress)
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

        // ── Phase 1: Scan ─────────────────────────────────────────────────────
        progress.Report(new IngestionProgress { PhaseText = "Scanning incoming files…" });

        var sourceFiles = Directory.GetFiles(incomingDir, "*", SearchOption.TopDirectoryOnly).ToList();
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

            // Save artifact records + link to release
            foreach (var f in expectedFiles)
            {
                var sourceFilePath = Path.Combine(sourceDir, f.RomName);
                long fileSize      = 0;
                try { fileSize = new FileInfo(sourceFilePath).Length; } catch { }

                var ck = f.Sha1.Length > 0 ? $"sha1:{f.Sha1}"
                       : f.Md5.Length  > 0 ? $"md5:{f.Md5}"
                       : "";

                var artifactId = Guid.NewGuid().ToString("N");
                store.SaveArtifact(new ArtifactRecord
                {
                    Id                 = artifactId,
                    SourceFileName     = f.RomName,
                    SourceRelativePath = $"source/{platformId}/{datLineId}/{safeFolder}/{f.RomName}",
                    SourceSizeBytes    = fileSize,
                    Sha1               = f.Sha1,
                    Md5                = f.Md5,
                    Crc                = f.Crc,
                    ContentIdentityKey = ck,
                    Status             = "sourced",
                    CreatedAtUtc       = now,
                    VerifiedAtUtc      = now,
                });
                store.LinkReleaseArtifact(new ReleaseArtifactRecord
                {
                    Id           = Guid.NewGuid().ToString("N"),
                    ReleaseId    = releaseId,
                    ArtifactId   = artifactId,
                    CreatedAtUtc = now,
                });

                // ── Transform v1: no_compression ─────────────────────────────
                if (ck.Length > 0)
                {
                    try
                    {
                        store.RunNoCompressionTransform(
                            sourceArtifactId:    artifactId,
                            sourceFilePath:      sourceFilePath,
                            fileName:            f.RomName,
                            sizeBytes:           fileSize,
                            crc:                 f.Crc,
                            md5:                 f.Md5,
                            sha1:                f.Sha1,
                            contentIdentityKey:  ck,
                            platformId:          platformId,
                            datLineId:           datLineId,
                            releaseFolderName:   safeFolder,
                            storageStrategyId:   storageStrategyId.Length > 0 ? storageStrategyId : "none",
                            appRoot:             appRoot);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Transform failures must never block ingestion.
                        var failOp = new IngestionOperation(f.RomName, "transform-failed", ex.Message);
                        result.Operations.Add(failOp);
                        progress.Report(new IngestionProgress { NewOperation = failOp });
                    }
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
                // Equivalent content already covered all targets — duplicate source.
                result.FilesSkipped++;
                var destPath = IncomingSkipUniquePath(skipDir, fileName);
                try
                {
                    File.Move(srcPath, destPath, overwrite: false);
                    var op = new IngestionOperation(fileName, "skip",
                        $"incoming-skip/{Path.GetFileName(destPath)} (duplicate_content_same_target)");
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

    private static void WriteIngestionLog(
        string          logsDir,
        string          platformId,
        string          datLineId,
        IngestionResult result)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            var date    = DateTime.UtcNow.ToString("yyyyMMdd");
            var prefix  = $"ingestion-{platformId}-{date}";
            int counter = 1;
            string path;
            do { path = Path.Combine(logsDir, $"{prefix}-{counter:D3}.log"); counter++; }
            while (File.Exists(path));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ARKADIA INGESTION LOG");
            sb.AppendLine($"Date:         {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Platform:     {platformId}");
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
                        imported);
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
                ContentKey = game.ContentKey,
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
            PhaseText       = "Saving ROM files…",
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
                    PhaseText       = "Saving ROM files…",
                    IsIndeterminate = false,
                    Total           = count,
                    Processed       = i + 1,
                    Accepted        = i + 1,
                    Rejected        = 0,
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
    }
}

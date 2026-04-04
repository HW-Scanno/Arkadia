using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Arkadia.Dashboard;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.Systems;
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

    public MainWindow()
    {
        InitializeComponent();

        _navButtons.AddRange([
            NavDashboard, NavSystems, NavLibrary, NavVolumes, NavDisks, NavOperations,
            NavLogs, NavSettings,
        ]);

        _views = new()
        {
            [NavDashboard] = ViewDashboard,
            [NavSystems]   = ViewSystems,
            [NavLibrary]   = ViewLibrary,
        };

        InitSystems();
        InitLibrary();
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

        _systemsPlatforms = _catalog.LoadPlatforms()
            .Select(p =>
            {
                var (dl, total, present, missing, lost) = _catalog.GetPlatformStats(p.Id);
                return new SystemPlatform
                {
                    Id           = p.Id,
                    Name         = p.Name,
                    Manufacturer = p.Manufacturer,
                    HardwareType = _hardwareTypeMap.TryGetValue(p.HardwareTypeId, out var ht) ? ht : "",
                    DatLines     = dl,
                    TotalTitles  = total,
                    Present      = present,
                    Missing      = missing,
                    Lost         = lost,
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
        SystemsDetailDatLines.Text     = p.DatLines.ToString();
        SystemsDetailTotal.Text        = $"{p.TotalTitles:N0}";
        SystemsDetailPresent.Text      = $"{p.Present:N0}";
        SystemsDetailMissing.Text      = $"{p.Missing:N0}";
        SystemsDetailLost.Text         = $"{p.Lost:N0}";
        SystemsDetailCoverage.Text     = p.Coverage;

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
            .Select(dl => new DatLineInfo(
                Name:             dl.Name,
                Releases:         dl.ReleaseCount,
                LastImport:       dl.ImportedAtUtc.ToString("yyyy-MM-dd"),
                StorageStrategy:  _strategyNameMap.TryGetValue(dl.StorageStrategyId, out var sn) ? sn : "",
                CatalogId:        dl.Id,
                CatalogPlatformId: dl.PlatformId))
            .ToList();

    private Control MakeDatLineRow(DatLineInfo d)
    {
        var secondary = new SolidColorBrush(Color.Parse("#888899"));
        var accent    = new SolidColorBrush(Color.Parse("#7B68EE"));

        var nameBlock = new TextBlock
        {
            Text         = d.Name,
            FontSize     = 12,
            Foreground   = new SolidColorBrush(Color.Parse("#D0D0E0")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var meta = new TextBlock
        {
            Text       = $"{d.Releases:N0} releases · last import {d.LastImport}",
            FontSize   = 11,
            Foreground = secondary,
            Margin     = new Avalonia.Thickness(0, 2, 0, 0),
        };

        var row = new StackPanel
        {
            Spacing = 0,
            Margin  = new Avalonia.Thickness(0, 0, 0, 8),
            Children = { nameBlock, meta },
        };

        if (d.StorageStrategy.Length > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text       = $"Storage: {d.StorageStrategy}",
                FontSize   = 11,
                Foreground = secondary,
                Margin     = new Avalonia.Thickness(0, 1, 0, 0),
            });
        }

        // Actions row
        var actionsPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Avalonia.Thickness(0, 5, 0, 0) };

        if (d.LibraryPlatform is not null && d.LibraryDatLine is not null)
        {
            var libPlatform = d.LibraryPlatform;
            var libDatLine  = d.LibraryDatLine;

            var link = new TextBlock
            {
                Text       = "Open in Library →",
                FontSize   = 11,
                Foreground = accent,
                Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            link.PointerPressed += (_, _) => NavigateToLibrary(libPlatform, libDatLine);
            actionsPanel.Children.Add(link);
        }

        if (d.CatalogId is not null && d.CatalogPlatformId is not null)
        {
            var catalogId         = d.CatalogId;
            var catalogPlatformId = d.CatalogPlatformId;
            var datLineInfo       = d;

            var deleteLink = new TextBlock
            {
                Text       = "Delete",
                FontSize   = 11,
                Foreground = new SolidColorBrush(Color.Parse("#EF5350")),
                Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
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

    // Active dataset entries (set when DAT line changes)
    private IReadOnlyList<LibraryEntry> _activeDatasetEntries = [];
    private List<LibraryDataset> _activeLibraryDatasets = [];

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

            var releases = new DatLineStore(absPath).LoadReleases()
                .Select(r => new LibraryEntry
                {
                    Name      = r.Name,
                    Platform  = platformName,
                    Status    = Capitalize(r.Status),
                    Region    = r.Region,
                    Languages = r.Languages.ToUpperInvariant(),
                    Format    = r.Format,
                    Size      = r.Size,
                    Tier      = r.Tier,
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

        // DAT FILES
        DetailDatFiles.Children.Clear();
        foreach (var f in BuildDatFiles(entry))
            DetailDatFiles.Children.Add(MakeFileRow(f));

        // ARKADIA ARTIFACTS
        DetailArtifacts.Children.Clear();
        var artifacts = BuildArtifacts(entry);
        if (artifacts.Count > 0)
        {
            DetailArtifactsEmpty.IsVisible = false;
            foreach (var f in artifacts)
                DetailArtifacts.Children.Add(MakeFileRow(f));
        }
        else
        {
            DetailArtifactsEmpty.IsVisible = true;
        }

        DetailEmptyState.IsVisible = false;
        DetailContent.IsVisible    = true;
    }

    // ── Mock file generation ──────────────────────────────────────────────────

    private static List<FileRecord> BuildDatFiles(LibraryEntry entry)
    {
        var slug = Slugify(entry.Name);
        return [new FileRecord($"{slug}.dat", FakeHash(entry.Name + ".dat", 32))];
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

    // ── DAT Import stub ───────────────────────────────────────────────────────

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
        var dialog            = new ImportDatDialog(platforms, storageStrategies, existingDatLines);
        var ok        = await dialog.ShowDialog<bool>(this);
        if (!ok) return;

        var platformId   = dialog.PlatformId  ?? "";
        var authority    = dialog.Authority   ?? "";
        var datCategory  = dialog.DatCategory ?? "";
        var datLineId    = dialog.DatLineId   ?? "";    // stable: <platformId>-<authority>-<categorySlug>
        var datLineName  = $"{AuthorityLabel(authority)}: {datCategory}";

        // Build real ReleaseRecord rows from parsed games.
        // SaveReleases replaces the full release set for this dat line on conflict.
        var releases = new List<ReleaseRecord>(dialog.ParsedGames.Count);
        foreach (var game in dialog.ParsedGames)
        {
            releases.Add(new ReleaseRecord
            {
                Id        = System.Guid.NewGuid().ToString("N"),
                DatLineId = datLineId,
                Name      = game.Name,
                Status    = "missing",
                Region    = game.Region,
                Languages = game.Languages,
            });
        }

        // Per-dat-line DB path: relative stored in catalog, absolute used for I/O.
        var relPath = $"systems/{platformId}/{datLineId}.db";
        var absPath = Path.Combine(_dataDir, relPath);

        // Import is create-only — duplicate IDs are blocked in the dialog.
        // Always insert a fresh row here.
        existingDatLines.Add(new DatLineRecord
        {
            Id                 = datLineId,
            PlatformId         = platformId,
            Name               = datLineName,
            Authority          = authority,
            DatCategory        = datCategory,
            Version            = dialog.Version ?? "",
            StorageStrategyId  = dialog.StorageStrategyId ?? "",
            DataStorePath      = relPath,
            ReleaseCount       = releases.Count,
            ImportedAtUtc      = DateTime.UtcNow,
        });
        _catalog.SaveDatLines(existingDatLines);
        new DatLineStore(absPath).SaveReleases(releases);

        RebuildLibraryDatasets();
        ResolveFlagImages();
        RefreshSystemsKeepSelection(platformId);

        PageTitle.Text = $"Imported: {datLineName} ({releases.Count} entries)";
        await System.Threading.Tasks.Task.Delay(2000);
        if (PageTitle.Text?.StartsWith("Imported:") == true)
            PageTitle.Text = "Systems";
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

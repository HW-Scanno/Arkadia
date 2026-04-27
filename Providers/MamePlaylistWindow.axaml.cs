using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Arkadia.Data;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Arkadia;

// ── Domain types ──────────────────────────────────────────────────────────────

public sealed record FamilyRule(
    string   DisplayName,
    string   RuleType,
    string[] RuleValues)
{
    public const string SourcefileIn     = "sourcefile in";
    public const string CategoryContains = "category contains";
    public const string NonArcade        = "non-arcade";

    public string Summary => RuleType switch
    {
        CategoryContains => $"{RuleValues.Length} keyword{(RuleValues.Length == 1 ? "" : "s")}",
        NonArcade        => "non-arcade machines",
        _                => $"{RuleValues.Length} sourcefile{(RuleValues.Length == 1 ? "" : "s")}",
    };
}

// ── Playlist persistence ──────────────────────────────────────────────────────

internal static class PlaylistStore
{
    internal static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "config", "mame", "mame-playlists.json");

    internal static List<JsonObject> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            var arr = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonArray;
            return arr?.OfType<JsonObject>().ToList() ?? [];
        }
        catch { return []; }
    }

    internal static void Save(List<JsonObject> playlists)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var arr = new JsonArray(playlists.Select(p => (JsonNode)p).ToArray());
        File.WriteAllText(FilePath, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void Delete(string name)
    {
        var list = Load();
        list.RemoveAll(p => (p["name"]?.GetValue<string>() ?? "") == name);
        Save(list);
    }
}

// ── Families persistence ──────────────────────────────────────────────────────

internal sealed record FamiliesData(List<FamilyRule> Rules, JsonObject? Defaults);

internal static class FamiliesStore
{
    internal static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "config", "mame", "families.json");

    // Returns null on hard JSON failure (caller keeps current list unchanged).
    // Returns FamiliesData with an empty Rules list if the file is absent or has no valid rules.
    internal static FamiliesData? TryLoad(Action<string, string> log)
    {
        if (!File.Exists(FilePath))
        {
            log($"families.json not found at {FilePath}.", "#FFA726");
            return new FamiliesData([], null);
        }

        JsonObject? root;
        try   { root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject; }
        catch (Exception ex)
        {
            log($"Failed to parse families.json: {ex.Message}", "#EF5350");
            return null;
        }

        if (root is null)
        {
            log("families.json is not a valid JSON object.", "#EF5350");
            return null;
        }

        // Parse optional defaults section — failure here is non-fatal.
        JsonObject? defaults = null;
        try { defaults = root["defaults"] as JsonObject; }
        catch (Exception ex) { log($"Warning: could not read defaults section: {ex.Message}", "#FFA726"); }

        var rules = new List<FamilyRule>();
        if (root["families"] is JsonArray arr)
        {
            foreach (var node in arr.OfType<JsonObject>())
            {
                var displayName = (node["displayName"]?.GetValue<string>() ?? "").Trim();
                var ruleType    =  node["ruleType"]?.GetValue<string>()    ?? "";

                if (displayName.Length == 0) continue;

                if (ruleType != FamilyRule.SourcefileIn  &&
                    ruleType != FamilyRule.CategoryContains &&
                    ruleType != FamilyRule.NonArcade)
                {
                    log($"Skipped \"{displayName}\": unsupported ruleType \"{ruleType}\".", "#888899");
                    continue;
                }

                string[] ruleValues = node["ruleValues"] is JsonArray va
                    ? [.. va.OfType<JsonValue>()
                           .Select(v => v.GetValue<string>().Trim())
                           .Where(v => v.Length > 0)
                           .Distinct(StringComparer.OrdinalIgnoreCase)]
                    : [];

                // non-arcade uses empty ruleValues by design; others require at least one value.
                if (ruleValues.Length == 0 && ruleType != FamilyRule.NonArcade) continue;

                rules.Add(new FamilyRule(displayName, ruleType, ruleValues));
            }
        }

        return new FamiliesData(rules, defaults);
    }

    internal static void Save(IEnumerable<FamilyRule> rules, JsonObject defaults)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var obj = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["defaults"]      = defaults,
            ["families"]      = new JsonArray(rules.Select(r => (JsonNode)new JsonObject
            {
                ["displayName"] = r.DisplayName,
                ["ruleType"]    = r.RuleType,
                ["ruleValues"]  = new JsonArray(r.RuleValues.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
        };
        File.WriteAllText(FilePath,
            obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ── Category index (category.ini) ────────────────────────────────────────────

internal sealed class CategoryIndex
{
    private readonly Dictionary<string, string> _machineCategory;

    private CategoryIndex(Dictionary<string, string> machineCategory) =>
        _machineCategory = machineCategory;

    internal string BuildSummary(IEnumerable<string> machineNames)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in machineNames)
            if (_machineCategory.TryGetValue(name, out var cat))
                counts[cat] = counts.GetValueOrDefault(cat, 0) + 1;

        if (counts.Count == 0) return "";
        return string.Join(", ", counts
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => $"{kv.Key} ({kv.Value})"));
    }

    internal string? GetCategory(string machineName) =>
        _machineCategory.TryGetValue(machineName, out var cat) ? cat : null;

    private static readonly string[] NonArcadeTerms =
    [
        "Computer", "Home Computer", "Console", "Game Console", "Home Videogame",
        "Handheld", "Plug n' Play", "TV Game", "Calculator", "Musical Instrument",
        "Chess", "Tabletop", "PDA", "Printer", "Terminal", "Workstation",
    ];

    internal bool IsNonArcade(string machineName) =>
        _machineCategory.TryGetValue(machineName, out var cat) &&
        NonArcadeTerms.Any(t => cat.Contains(t, StringComparison.OrdinalIgnoreCase));

    internal bool MatchesCategoryContains(string machineName, string[] values) =>
        _machineCategory.TryGetValue(machineName, out var cat) &&
        values.Any(v => cat.Contains(v, StringComparison.OrdinalIgnoreCase));

    internal HashSet<string> GetArcadeNames()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _machineCategory)
            if (kv.Value.StartsWith("Arcade", StringComparison.OrdinalIgnoreCase))
                result.Add(kv.Key);
        return result;
    }

    internal static CategoryIndex? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string currentSection = "";
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
                currentSection = t[1..^1].Trim();
            else if (currentSection.Length > 0 && t.Length > 0 && !t.StartsWith(';'))
                map.TryAdd(t, currentSection);
        }
        return map.Count > 0 ? new CategoryIndex(map) : null;
    }
}

// ── Window ────────────────────────────────────────────────────────────────────

public partial class MamePlaylistWindow : Window
{
    // ── Data model ────────────────────────────────────────────────────────────

    private sealed record MameRom(
        string  Name,
        string? Size,
        string? Crc,
        string? Sha1,
        string? Merge,
        string? Status,
        string? Region,
        string? Offset);

    private sealed record MameDisk(
        string  Name,
        string? Sha1,
        string? Merge,
        string? Status,
        string? Region);

    private sealed record MameMachine(
        string Name,
        string Cloneof,
        string Romof,
        string Description,
        string Sourcefile,
        string DriverStatus,
        bool   IsBios,
        bool   IsDevice,
        bool   IsMechanical,
        IReadOnlyList<MameRom>  Roms,
        IReadOnlyList<MameDisk> Disks);

    private sealed record DriverInfo(
        string   Sourcefile,
        int      MachineCount,
        string[] SampleNames,
        string   CategorySummary);

    // ── Rule state ────────────────────────────────────────────────────────────

    private readonly List<FamilyRule> _rules = [];
    private int  _selectedRuleIdx = -1;
    private bool _generating;

    // ── Version state ─────────────────────────────────────────────────────────

    private readonly List<(Border Row, CheckBox Box, string VersionName, string CacheDir)> _versionRows = [];
    private string? _selectedVersion;
    private string? _selectedCacheDir;
    private bool    _updatingVersion;

    // ── Machine cache ─────────────────────────────────────────────────────────

    private List<MameMachine>?       _cachedMachines;
    private string?                  _cachedForVersion;
    private int[]                    _ruleCounts = [];
    private CancellationTokenSource? _machineLoadCts;

    // ── Driver explorer ───────────────────────────────────────────────────────

    private List<DriverInfo>? _allDriverInfos; // base-filtered, no exclusions
    private string            _driverSearch = "";

    // ── Exclusion state ───────────────────────────────────────────────────────

    private readonly HashSet<string> _excludedSet   = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string>    _excludedOrder = []; // most-recent-first

    // ── Playlist state ────────────────────────────────────────────────────────

    private string? _currentPlaylistName;
    private bool    _suppressRefresh;

    // ── Category & arcade filter ──────────────────────────────────────────────

    private CategoryIndex?  _categoryIndex;
    private HashSet<string> _arcadeNames = new(StringComparer.OrdinalIgnoreCase);

    private static string CategoryIniPath => ProviderHelpers.MameCategoryIniPath;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MamePlaylistWindow()
    {
        InitializeComponent();

        // Defaults — set before event handlers so no spurious RefreshCounts fires.
        SetLayoutBox.SelectedIndex       = 0;     // Non-merged (all sets)
        ArcadeOnlyCheck.IsChecked        = false;
        ExcludeDevicesCheck.IsChecked    = true;
        ExcludeMechanicalCheck.IsChecked = true;
        RunnableOnlyCheck.IsChecked      = false;
        IncludeBiosCheck.IsChecked       = true;

        ReloadCategoryIndex(log: false);

        ScanCachedVersions();

        ArcadeOnlyCheck.IsCheckedChanged        += (_, _) => { if (!_suppressRefresh) RefreshCounts(); };
        ExcludeDevicesCheck.IsCheckedChanged     += (_, _) => { if (!_suppressRefresh) RefreshCounts(); };
        ExcludeMechanicalCheck.IsCheckedChanged  += (_, _) => { if (!_suppressRefresh) RefreshCounts(); };
        RunnableOnlyCheck.IsCheckedChanged       += (_, _) => { if (!_suppressRefresh) RefreshCounts(); };
        IncludeBiosCheck.IsCheckedChanged        += (_, _) => { if (!_suppressRefresh) RefreshCounts(); };
        SetLayoutBox.SelectionChanged            += (_, _) => { if (!_suppressRefresh) RefreshCounts(); };

        UpdatePlaylistLabel();
        UpdateState();
    }

    // ── Version scanning ──────────────────────────────────────────────────────

    private void ScanCachedVersions()
    {
        var cacheRoot = ProviderHelpers.GetMameCacheRootDir();

        if (!Directory.Exists(cacheRoot))
        {
            VersionCountLabel.Text = "No cached versions.";
            return;
        }

        var dirs = Directory.GetDirectories(cacheRoot)
            .Where(d => File.Exists(Path.Combine(d, "meta.json")))
            .OrderByDescending(d => d)
            .ToList();

        if (dirs.Count == 0)
        {
            VersionCountLabel.Text = "No cached versions.";
            return;
        }

        VersionCountLabel.Text = $"{dirs.Count} cached version(s)";

        foreach (var dir in dirs)
        {
            var versionName = Path.GetFileName(dir);
            var cacheDir    = dir;

            var cb = new CheckBox
            {
                Content    = $"MAME {versionName}",
                FontSize   = 12,
                Foreground = new SolidColorBrush(Color.Parse("#CCCCDD")),
                Padding    = new Avalonia.Thickness(12, 5, 12, 5),
            };

            cb.IsCheckedChanged += (_, _) =>
            {
                if (_updatingVersion) return;

                if (cb.IsChecked == true)
                {
                    _updatingVersion = true;
                    try
                    {
                        foreach (var (_, other, _, _) in _versionRows)
                            if (!ReferenceEquals(other, cb))
                                other.IsChecked = false;
                        _selectedVersion  = versionName;
                        _selectedCacheDir = cacheDir;
                    }
                    finally { _updatingVersion = false; }

                    _ = LoadMachinesAsync(cacheDir, versionName);
                }
                else
                {
                    if (_selectedVersion == versionName)
                    {
                        _selectedVersion  = null;
                        _selectedCacheDir = null;
                        _machineLoadCts?.Cancel();
                        _cachedMachines   = null;
                        _cachedForVersion = null;
                        _allDriverInfos   = null;
                        _ruleCounts       = [];
                        RebuildRulePanel();
                        UpdatePreview();
                        RebuildDriverCards();
                    }
                }

                UpdateState();
            };

            var row = new Border
            {
                Child           = cb,
                BorderBrush     = new SolidColorBrush(Color.Parse("#141420")),
                BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            };

            VersionsPanel.Children.Add(row);
            _versionRows.Add((row, cb, versionName, cacheDir));
        }
    }

    // ── Machine loading ───────────────────────────────────────────────────────

    private async Task LoadMachinesAsync(string cacheDir, string version)
    {
        if (_cachedForVersion == version && _cachedMachines is not null)
        {
            RefreshCounts();
            return;
        }

        _machineLoadCts?.Cancel();
        _machineLoadCts   = new CancellationTokenSource();
        var cts           = _machineLoadCts;
        _cachedMachines   = null;
        _cachedForVersion = null;
        _allDriverInfos   = null;
        _ruleCounts       = [];

        var listxmlPath = Path.Combine(cacheDir, "listxml.xml");
        if (!File.Exists(listxmlPath))
        {
            PlaylistStatusText.Text = $"MAME {version} — listxml.xml not found.";
            UpdateState();
            return;
        }

        PlaylistStatusText.Text = $"Loading MAME {version} machine data…";
        DriverCountLabel.Text   = "Loading…";

        try
        {
            var machines = await Task.Run(
                () => ParseListXml(listxmlPath, cts.Token), cts.Token);

            if (cts.IsCancellationRequested) return;

            _cachedMachines   = machines;
            _cachedForVersion = version;
            RefreshCounts();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppendLog($"Failed to load machines: {ex.Message}", "#EF5350");
            UpdateState();
        }
    }

    // ── Sourcefile extraction (for dialog) ────────────────────────────────────

    private async Task<IReadOnlyList<string>> GetSourcefilesAsync()
    {
        if (_cachedMachines is not null)
            return _cachedMachines
                .Select(m => m.Sourcefile)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (_selectedCacheDir is null) return [];
        var listxmlPath = Path.Combine(_selectedCacheDir, "listxml.xml");
        if (!File.Exists(listxmlPath)) return [];
        return await Task.Run(() => ScanSourcefiles(listxmlPath));
    }

    // Sourcefiles currently assigned across all family rules.
    private HashSet<string> GetAssignedSourcefiles() =>
        new(_rules.SelectMany(r => r.RuleValues), StringComparer.OrdinalIgnoreCase);

    // Sourcefiles eligible for a new rule: all minus excluded minus already-assigned.
    // For editing an existing rule, pass that rule's current values via alsoInclude
    // so they remain visible even though they are "assigned" to this rule.
    private async Task<IReadOnlyList<string>> GetAvailableSourcefilesAsync(
        string[]? alsoInclude = null)
    {
        var all      = await GetSourcefilesAsync();
        var assigned = GetAssignedSourcefiles();
        if (alsoInclude is not null)
            foreach (var v in alsoInclude) assigned.Remove(v);

        return all
            .Where(sf => !_excludedSet.Contains(sf) && !assigned.Contains(sf))
            .ToList();
    }

    private static List<string> ScanSourcefiles(string listxmlPath)
    {
        var set      = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var settings = new XmlReaderSettings
        {
            DtdProcessing    = DtdProcessing.Ignore,
            ValidationType   = ValidationType.None,
            IgnoreComments   = true,
            IgnoreWhitespace = true,
        };
        using var reader = XmlReader.Create(listxmlPath, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "machine")
                continue;
            var sf = reader.GetAttribute("sourcefile");
            if (!string.IsNullOrEmpty(sf)) set.Add(sf);
            reader.Skip();
        }
        return [.. set];
    }

    // ── Filtering layers ──────────────────────────────────────────────────────

    // Global filters (device/mechanical/arcade/runnable/bios) — no exclusions, no set-layout.
    private IEnumerable<MameMachine> GetBaseFiltered()
    {
        if (_cachedMachines is null) return [];
        IEnumerable<MameMachine> q = _cachedMachines;
        if (ExcludeDevicesCheck.IsChecked    == true) q = q.Where(m => !m.IsDevice);
        if (ExcludeMechanicalCheck.IsChecked == true) q = q.Where(m => !m.IsMechanical);
        if (ArcadeOnlyCheck.IsChecked        == true && _arcadeNames.Count > 0)
            q = q.Where(m => _arcadeNames.Contains(m.Name));
        if (RunnableOnlyCheck.IsChecked      == true) q = q.Where(m => m.DriverStatus != "preliminary");
        if (IncludeBiosCheck.IsChecked       != true) q = q.Where(m => !m.IsBios);
        return q;
    }

    // Working pool: base-filtered + exclusions removed.
    private IEnumerable<MameMachine> GetWorkingPool()
    {
        var q = GetBaseFiltered();
        if (_excludedSet.Count > 0) q = q.Where(m => !_excludedSet.Contains(m.Sourcefile));
        return q;
    }

    // Final rule-partitioning set: working pool + optional set-layout filter.
    private IEnumerable<MameMachine> GetFilteredMachines()
    {
        var q = GetWorkingPool();
        if (SetLayoutBox.SelectedIndex == 1) q = q.Where(m => string.IsNullOrEmpty(m.Cloneof));
        return q;
    }

    // ── Driver info computation ───────────────────────────────────────────────

    private void RebuildAllDriverInfos()
    {
        if (_cachedMachines is null) { _allDriverInfos = null; return; }

        _allDriverInfos = GetBaseFiltered()
            .GroupBy(m => m.Sourcefile, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                var list    = g.ToList();
                var samples = list.Take(4).Select(m => m.Name).ToArray();
                var catSummary = _categoryIndex?.BuildSummary(list.Select(m => m.Name)) ?? "";
                return new DriverInfo(g.Key, list.Count, samples, catSummary);
            })
            .OrderBy(d => d.Sourcefile, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Count computation (ordered partitioning) ──────────────────────────────

    private void RecomputeCounts()
    {
        if (_cachedMachines is null || _rules.Count == 0)
        {
            _ruleCounts = [];
            return;
        }

        var filtered = GetFilteredMachines().ToList();
        var claimed  = new HashSet<string>(StringComparer.Ordinal);
        _ruleCounts  = new int[_rules.Count];

        for (int i = 0; i < _rules.Count; i++)
        {
            int count = 0;
            foreach (var m in filtered)
            {
                if (!claimed.Contains(m.Name) && MatchesRule(m, _rules[i], _categoryIndex))
                {
                    claimed.Add(m.Name);
                    count++;
                }
            }
            _ruleCounts[i] = count;
        }
    }

    private void RefreshCounts()
    {
        RebuildAllDriverInfos();
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateExcludedButton();
        UpdateState();
    }

    // ── Rule management ───────────────────────────────────────────────────────

    private async void OnAddRule(object? sender, RoutedEventArgs e)
    {
        var sourcefiles = await GetAvailableSourcefilesAsync();
        var dlg         = new FamilyRuleDialog(sourcefiles);
        await dlg.ShowDialog(this);
        if (dlg.Result is null) return;

        _rules.Add(dlg.Result);
        _selectedRuleIdx = _rules.Count - 1;
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateState();
    }

    private async void OnEditRule(object? sender, RoutedEventArgs e)
    {
        if (_selectedRuleIdx < 0 || _selectedRuleIdx >= _rules.Count) return;
        var currentRule = _rules[_selectedRuleIdx];
        if (currentRule.RuleType is FamilyRule.CategoryContains or FamilyRule.NonArcade)
        {
            AppendLog($"Editing \"{currentRule.DisplayName}\" ({currentRule.RuleType}) is not yet supported in the UI. Edit families.json directly.", "#888899");
            return;
        }
        // Available pool for editing = AvailableDrivers + this rule's own current values
        var sourcefiles = await GetAvailableSourcefilesAsync(currentRule.RuleValues);
        var dlg         = new FamilyRuleDialog(currentRule, sourcefiles);
        await dlg.ShowDialog(this);
        if (dlg.Result is null) return;

        _rules[_selectedRuleIdx] = dlg.Result;
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateState();
    }

    private void OnDeleteRule(object? sender, RoutedEventArgs e)
    {
        if (_selectedRuleIdx < 0 || _selectedRuleIdx >= _rules.Count) return;
        _rules.RemoveAt(_selectedRuleIdx);
        _selectedRuleIdx = Math.Min(_selectedRuleIdx, _rules.Count - 1);
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateState();
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        if (_selectedRuleIdx <= 0) return;
        (_rules[_selectedRuleIdx], _rules[_selectedRuleIdx - 1]) =
            (_rules[_selectedRuleIdx - 1], _rules[_selectedRuleIdx]);
        _selectedRuleIdx--;
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        UpdateState();
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        if (_selectedRuleIdx < 0 || _selectedRuleIdx >= _rules.Count - 1) return;
        (_rules[_selectedRuleIdx], _rules[_selectedRuleIdx + 1]) =
            (_rules[_selectedRuleIdx + 1], _rules[_selectedRuleIdx]);
        _selectedRuleIdx++;
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        UpdateState();
    }

    private void RebuildRulePanel()
    {
        RulesPanel.Children.Clear();
        for (int i = 0; i < _rules.Count; i++)
            RulesPanel.Children.Add(BuildRuleRow(i));

        RuleCountLabel.Text = _rules.Count == 0
            ? "No rules defined. Click \u201c+ Add Rule\u201d to begin."
            : $"{_rules.Count} rule(s) \u2014 executed top-to-bottom; Other.dat gets the rest.";
    }

    private Border BuildRuleRow(int idx)
    {
        var rule      = _rules[idx];
        bool selected = idx == _selectedRuleIdx;
        bool hasCount = _ruleCounts.Length > idx;

        var indexLabel = new TextBlock
        {
            Text      = $"{idx + 1}.",
            FontSize  = 11,
            Width     = 24,
            Foreground = new SolidColorBrush(Color.Parse("#555566")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameLabel = new TextBlock
        {
            Text       = rule.DisplayName,
            FontSize   = 12,
            Foreground = new SolidColorBrush(Color.Parse(selected ? "#E8E8F8" : "#CCCCDD")),
            FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var summaryLabel = new TextBlock
        {
            Text       = rule.Summary,
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.Parse("#7B68EE")),
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Avalonia.Thickness(8, 0, 0, 0),
        };
        var countLabel = new TextBlock
        {
            Text       = hasCount ? $"{_ruleCounts[idx]:N0}" : "\u2014",
            FontSize   = 11,
            Foreground = new SolidColorBrush(Color.Parse(hasCount ? "#4CAF50" : "#444455")),
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin     = new Avalonia.Thickness(12, 0, 0, 0),
            MinWidth   = 40,
            TextAlignment = Avalonia.Media.TextAlignment.Right,
        };

        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,Auto,Auto") };
        inner.Children.Add(indexLabel);
        Grid.SetColumn(nameLabel,    1); inner.Children.Add(nameLabel);
        Grid.SetColumn(summaryLabel, 2); inner.Children.Add(summaryLabel);
        Grid.SetColumn(countLabel,   3); inner.Children.Add(countLabel);

        var row = new Border
        {
            Child           = inner,
            Padding         = new Avalonia.Thickness(12, 8, 12, 8),
            Background      = new SolidColorBrush(Color.Parse(selected ? "#1A1A2E" : "#0F0F14")),
            BorderBrush     = new SolidColorBrush(Color.Parse(selected ? "#7B68EE" : "#141420")),
            BorderThickness = new Avalonia.Thickness(selected ? 2 : 0, 0, 0, 1),
            Cursor          = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        int capturedIdx = idx;
        row.PointerPressed += (_, _) =>
        {
            _selectedRuleIdx = capturedIdx;
            RebuildRulePanel();
            UpdatePreview();
            UpdateState();
        };
        return row;
    }

    // ── Preview panel ─────────────────────────────────────────────────────────

    private void UpdatePreview()
    {
        if (_selectedRuleIdx < 0 || _selectedRuleIdx >= _rules.Count || _cachedMachines is null)
        {
            PreviewBorder.IsVisible = false;
            return;
        }

        var rule     = _rules[_selectedRuleIdx];
        var filtered = GetFilteredMachines().ToList();
        var claimed  = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < _selectedRuleIdx; i++)
            foreach (var m in filtered)
                if (!claimed.Contains(m.Name) && MatchesRule(m, _rules[i], _categoryIndex))
                    claimed.Add(m.Name);

        var matched = filtered
            .Where(m => !claimed.Contains(m.Name) && MatchesRule(m, rule, _categoryIndex))
            .ToList();

        PreviewHeader.Text = $"PREVIEW \u2014 {rule.DisplayName}  ({matched.Count:N0} machines)";
        PreviewNamesPanel.Children.Clear();
        foreach (var m in matched)
            PreviewNamesPanel.Children.Add(new TextBlock
            {
                Text       = m.Name,
                FontSize   = 11,
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                Foreground = new SolidColorBrush(Color.Parse("#AAAACC")),
            });

        PreviewBorder.IsVisible = true;
    }

    // ── Driver Explorer ───────────────────────────────────────────────────────

    private void OnDriverSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _driverSearch = DriverSearchBox.Text ?? "";
        RebuildDriverCards();
    }

    private void RebuildDriverCards()
    {
        DriverCardsPanel.Children.Clear();

        if (_allDriverInfos is null)
        {
            DriverCountLabel.Text = _cachedMachines is null
                ? "No version loaded."
                : "Loading…";
            return;
        }

        var assigned  = GetAssignedSourcefiles();
        var available = _allDriverInfos
            .Where(d => !_excludedSet.Contains(d.Sourcefile) && !assigned.Contains(d.Sourcefile))
            .ToList();

        var visible = available
            .Where(d => _driverSearch.Length == 0 ||
                        d.Sourcefile.Contains(_driverSearch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalCount    = _allDriverInfos.Count;
        int excludedCount = _allDriverInfos.Count(d => _excludedSet.Contains(d.Sourcefile));
        int assignedCount = _allDriverInfos.Count(d => assigned.Contains(d.Sourcefile));
        var summary = $"{totalCount:N0} total · {available.Count:N0} available · {assignedCount:N0} assigned · {excludedCount:N0} excluded";
        if (_driverSearch.Length > 0) summary += $" · {visible.Count:N0} shown";
        DriverCountLabel.Text = summary;

        foreach (var info in visible)
            DriverCardsPanel.Children.Add(BuildDriverCard(info));
    }

    private Border BuildDriverCard(DriverInfo info)
    {
        var nameLabel = new TextBlock
        {
            Text      = info.Sourcefile,
            FontSize  = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#C8C8E8")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var addBtn = new Button { Content = "+ Add" };
        addBtn.Classes.Add("card-action");
        addBtn.Margin = new Avalonia.Thickness(4, 0, 0, 0);
        addBtn.Click += (_, _) => AddToSelectedRule(info.Sourcefile);

        var exclBtn = new Button { Content = "Excl" };
        exclBtn.Classes.Add("card-excl");
        exclBtn.Margin = new Avalonia.Thickness(4, 0, 0, 0);
        exclBtn.Click += (_, _) => ExcludeDriver(info.Sourcefile);

        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        topRow.Children.Add(nameLabel);
        Grid.SetColumn(addBtn,   1); topRow.Children.Add(addBtn);
        Grid.SetColumn(exclBtn,  2); topRow.Children.Add(exclBtn);

        var countLabel = new TextBlock
        {
            Text       = $"{info.MachineCount:N0} machine{(info.MachineCount == 1 ? "" : "s")}",
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.Parse("#555566")),
            Margin     = new Avalonia.Thickness(0, 3, 0, 0),
        };

        var samplesText = string.Join(", ", info.SampleNames);
        if (info.MachineCount > info.SampleNames.Length) samplesText += "\u2026";
        var samplesLabel = new TextBlock
        {
            Text        = samplesText,
            FontSize    = 10,
            FontFamily  = new FontFamily("Consolas,Courier New,monospace"),
            Foreground  = new SolidColorBrush(Color.Parse("#44445A")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin      = new Avalonia.Thickness(0, 2, 0, 0),
        };

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(topRow);
        body.Children.Add(countLabel);
        body.Children.Add(samplesLabel);

        if (!string.IsNullOrEmpty(info.CategorySummary))
            body.Children.Add(new TextBlock
            {
                Text        = info.CategorySummary,
                FontSize    = 10,
                Foreground  = new SolidColorBrush(Color.Parse("#3A4A7A")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin      = new Avalonia.Thickness(0, 3, 0, 0),
            });

        return new Border
        {
            Child           = body,
            Padding         = new Avalonia.Thickness(10, 8, 10, 8),
            Background      = new SolidColorBrush(Color.Parse("#0C0C16")),
            BorderBrush     = new SolidColorBrush(Color.Parse("#181826")),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
        };
    }

    // ── Add-to-family ─────────────────────────────────────────────────────────

    private void AddToSelectedRule(string sourcefile)
    {
        if (_selectedRuleIdx < 0 || _selectedRuleIdx >= _rules.Count)
        {
            AppendLog("Select a rule first.", "#FFA726");
            return;
        }

        var rule = _rules[_selectedRuleIdx];
        if (rule.RuleValues.Any(v => string.Equals(v, sourcefile, StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog($"{sourcefile} is already in \"{rule.DisplayName}\".", "#888899");
            return;
        }

        _rules[_selectedRuleIdx] = rule with { RuleValues = [.. rule.RuleValues, sourcefile] };
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateState();
        AppendLog($"Added {sourcefile} \u2192 \"{rule.DisplayName}\".", "#4CAF50");
    }

    // ── Exclude / restore ─────────────────────────────────────────────────────

    private void ExcludeDriver(string sourcefile)
    {
        if (_excludedSet.Contains(sourcefile)) return;
        _excludedSet.Add(sourcefile);
        _excludedOrder.Insert(0, sourcefile); // most-recent-first

        // Fast path: no need to rebuild all driver infos
        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateExcludedButton();
        UpdateState();
        AppendLog($"Excluded driver {sourcefile}.", "#FFA726");
    }

    private void RestoreDrivers(IEnumerable<string> sourcefiles)
    {
        bool any = false;
        foreach (var sf in sourcefiles)
        {
            if (!_excludedSet.Contains(sf)) continue;
            _excludedSet.Remove(sf);
            _excludedOrder.Remove(sf);
            any = true;
        }
        if (!any) return;

        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateExcludedButton();
        UpdateState();
    }

    private async void OnShowExcluded(object? sender, RoutedEventArgs e)
    {
        if (_excludedOrder.Count == 0)
        {
            AppendLog("No excluded drivers.", "#888899");
            return;
        }

        var items = _excludedOrder
            .Select(sf =>
            {
                var count = _allDriverInfos?.FirstOrDefault(d =>
                    string.Equals(d.Sourcefile, sf, StringComparison.OrdinalIgnoreCase))?.MachineCount ?? 0;
                return (sf, count);
            })
            .ToList();

        var dlg = new ExcludedDriversDialog(items);
        await dlg.ShowDialog(this);

        if (dlg.ToRestore.Count > 0)
        {
            RestoreDrivers(dlg.ToRestore);
            AppendLog($"Restored {dlg.ToRestore.Count} driver{(dlg.ToRestore.Count == 1 ? "" : "s")}.", "#4CAF50");
        }
    }

    private void UpdateExcludedButton()
    {
        ExcludedButton.Content = $"Excluded ({_excludedOrder.Count})";
    }

    // ── Category.ini download ─────────────────────────────────────────────────

    private async void OnDownloadCategoryIni(object? sender, RoutedEventArgs e)
    {
        DownloadCategoryIniButton.IsEnabled = false;
        AppendLog("Downloading category.ini from GitHub…");
        if (await ProviderHelpers.DownloadCategoryIniAsync(AppendLog))
            ReloadCategoryIndex(log: true);
        DownloadCategoryIniButton.IsEnabled = true;
    }

    private void ReloadCategoryIndex(bool log = true)
    {
        _categoryIndex = CategoryIndex.TryLoad(CategoryIniPath);
        _arcadeNames   = _categoryIndex?.GetArcadeNames()
                         ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ArcadeFilterNote.Text = _categoryIndex is not null
            ? $"Arcade filter active — {_arcadeNames.Count:N0} machine names from config/mame/category.ini."
            : "Arcade filter uses config/mame/category.ini (MAME Extras). File not found — filter inactive.";
        ArcadeOnlyCheck.IsEnabled = _categoryIndex is not null;

        if (log && _categoryIndex is not null)
            AppendLog($"Category index loaded — {_arcadeNames.Count:N0} arcade machines.", "#4CAF50");

        if (_cachedMachines is not null)
            RefreshCounts();
    }

    private void UpdatePlaylistLabel() =>
        CurrentPlaylistLabel.Text = _currentPlaylistName is not null
            ? $"Current: {_currentPlaylistName}"
            : "Current: (unsaved)";

    // ── Families save / load ──────────────────────────────────────────────────

    private void OnLoadFamilies(object? sender, RoutedEventArgs e)
    {
        var data = FamiliesStore.TryLoad(AppendLog);
        if (data is null) return; // parse failure already logged; keep current list

        if (data.Rules.Count == 0)
        {
            AppendLog("families.json contains no compatible rules.", "#888899");
            return;
        }

        _rules.Clear();
        _rules.AddRange(data.Rules);
        _selectedRuleIdx = -1;

        if (data.Defaults is JsonObject def)
        {
            _suppressRefresh = true;
            try
            {
                SetLayoutBox.SelectedIndex       = def["setLayout"]?.GetValue<int>()          ?? SetLayoutBox.SelectedIndex;
                if (ArcadeOnlyCheck.IsEnabled)
                    ArcadeOnlyCheck.IsChecked    = def["arcadeOnly"]?.GetValue<bool>()        ?? ArcadeOnlyCheck.IsChecked == true;
                ExcludeDevicesCheck.IsChecked    = def["excludeDevices"]?.GetValue<bool>()    ?? ExcludeDevicesCheck.IsChecked == true;
                ExcludeMechanicalCheck.IsChecked = def["excludeMechanical"]?.GetValue<bool>() ?? ExcludeMechanicalCheck.IsChecked == true;
                RunnableOnlyCheck.IsChecked      = def["runnableOnly"]?.GetValue<bool>()      ?? RunnableOnlyCheck.IsChecked == true;
                IncludeBiosCheck.IsChecked       = def["includeBios"]?.GetValue<bool>()       ?? IncludeBiosCheck.IsChecked == true;
            }
            catch (Exception ex)
            {
                AppendLog($"Warning: could not apply some defaults: {ex.Message}", "#FFA726");
            }
            finally
            {
                _suppressRefresh = false;
            }
        }

        RecomputeCounts();
        RebuildRulePanel();
        UpdatePreview();
        RebuildDriverCards();
        UpdateState();
        AppendLog($"Loaded {data.Rules.Count} famil{(data.Rules.Count == 1 ? "y" : "ies")} from families.json.", "#4CAF50");
    }

    private void OnSaveFamilies(object? sender, RoutedEventArgs e)
    {
        if (_rules.Count == 0)
        {
            AppendLog("No families to save.", "#888899");
            return;
        }
        try
        {
            var defaults = new JsonObject
            {
                ["setLayout"]         = SetLayoutBox.SelectedIndex,
                ["arcadeOnly"]        = ArcadeOnlyCheck.IsChecked        == true,
                ["excludeDevices"]    = ExcludeDevicesCheck.IsChecked    == true,
                ["excludeMechanical"] = ExcludeMechanicalCheck.IsChecked == true,
                ["runnableOnly"]      = RunnableOnlyCheck.IsChecked      == true,
                ["includeBios"]       = IncludeBiosCheck.IsChecked       == true,
            };
            FamiliesStore.Save(_rules, defaults);
            AppendLog($"Saved {_rules.Count} famil{(_rules.Count == 1 ? "y" : "ies")} to families.json.", "#4CAF50");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to save families.json: {ex.Message}", "#EF5350");
        }
    }

    // ── Playlist save / load ──────────────────────────────────────────────────

    private async void OnSavePlaylist(object? sender, RoutedEventArgs e)
    {
        if (_currentPlaylistName is not null)
        {
            try
            {
                SavePlaylist(_currentPlaylistName);
                AppendLog($"Playlist \"{_currentPlaylistName}\" saved.", "#4CAF50");
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to save playlist: {ex.Message}", "#EF5350");
            }
        }
        else
        {
            await SaveAsAsync();
        }
    }

    private async void OnSaveAsPlaylist(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task SaveAsAsync()
    {
        var dlg = new PlaylistNameDialog();
        await dlg.ShowDialog(this);
        if (dlg.Result is null) return;

        try
        {
            SavePlaylist(dlg.Result);
            _currentPlaylistName = dlg.Result;
            UpdatePlaylistLabel();
            AppendLog($"Playlist \"{dlg.Result}\" saved.", "#4CAF50");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to save playlist: {ex.Message}", "#EF5350");
        }
    }

    private async void OnLoadPlaylist(object? sender, RoutedEventArgs e)
    {
        var names = PlaylistStore.Load()
            .Select(p => p["name"]?.GetValue<string>() ?? "")
            .Where(n => n.Length > 0)
            .ToList();

        if (names.Count == 0) { AppendLog("No saved playlists found.", "#888899"); return; }

        var dlg = new PlaylistListDialog(names);
        await dlg.ShowDialog(this);

        if (dlg.Result is string nameToLoad)
        {
            if (LoadPlaylist(nameToLoad))
                AppendLog($"Playlist \"{nameToLoad}\" loaded.", "#4CAF50");
            else
                AppendLog($"Playlist \"{nameToLoad}\" not found.", "#EF5350");
        }
    }

    private void SavePlaylist(string name)
    {
        var playlists = PlaylistStore.Load();
        playlists.RemoveAll(p => (p["name"]?.GetValue<string>() ?? "") == name);

        playlists.Add(new JsonObject
        {
            ["name"]              = name,
            ["arcadeOnly"]        = ArcadeOnlyCheck.IsChecked        == true,
            ["excludeDevices"]    = ExcludeDevicesCheck.IsChecked    == true,
            ["excludeMechanical"] = ExcludeMechanicalCheck.IsChecked == true,
            ["runnableOnly"]      = RunnableOnlyCheck.IsChecked      == true,
            ["includeBios"]       = IncludeBiosCheck.IsChecked       == true,
            ["setLayout"]         = SetLayoutBox.SelectedIndex,
            ["rules"] = new JsonArray(_rules.Select(r => (JsonNode)new JsonObject
            {
                ["displayName"] = r.DisplayName,
                ["ruleType"]    = r.RuleType,
                ["ruleValues"]  = new JsonArray(r.RuleValues.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
            ["excludedSourcefiles"] = new JsonArray(
                _excludedOrder.Select(s => (JsonNode)s).ToArray()),
        });

        PlaylistStore.Save(playlists);
    }

    private bool LoadPlaylist(string name)
    {
        var entry = PlaylistStore.Load()
            .FirstOrDefault(p => (p["name"]?.GetValue<string>() ?? "") == name);
        if (entry is null) return false;

        _rules.Clear();
        _selectedRuleIdx = -1;
        _excludedSet.Clear();
        _excludedOrder.Clear();

        if (ArcadeOnlyCheck.IsEnabled)
            ArcadeOnlyCheck.IsChecked    = entry["arcadeOnly"]?.GetValue<bool>()        ?? false;
        ExcludeDevicesCheck.IsChecked    = entry["excludeDevices"]?.GetValue<bool>()    ?? true;
        ExcludeMechanicalCheck.IsChecked = entry["excludeMechanical"]?.GetValue<bool>() ?? true;
        RunnableOnlyCheck.IsChecked      = entry["runnableOnly"]?.GetValue<bool>()      ?? true;
        IncludeBiosCheck.IsChecked       = entry["includeBios"]?.GetValue<bool>()       ?? false;
        SetLayoutBox.SelectedIndex       = entry["setLayout"]?.GetValue<int>()          ?? 0;

        if (entry["rules"] is JsonArray rulesArr)
        {
            foreach (var ruleNode in rulesArr.OfType<JsonObject>())
            {
                var displayName = ruleNode["displayName"]?.GetValue<string>() ?? "";
                var ruleType    = ruleNode["ruleType"]?.GetValue<string>()    ?? FamilyRule.SourcefileIn;
                string[] ruleValues =
                    ruleNode["ruleValues"] is JsonArray va
                        ? [.. va.OfType<JsonValue>().Select(v => v.GetValue<string>())]
                    : ruleNode["ruleValue"]?.GetValue<string>() is { Length: > 0 } single
                        ? [single]
                    : [];

                if (displayName.Length > 0 && (ruleValues.Length > 0 || ruleType == FamilyRule.NonArcade))
                    _rules.Add(new FamilyRule(displayName, ruleType, ruleValues));
            }
        }

        if (entry["excludedSourcefiles"] is JsonArray excArr)
        {
            foreach (var v in excArr.OfType<JsonValue>())
            {
                var sf = v.GetValue<string>();
                if (sf.Length > 0 && _excludedSet.Add(sf))
                    _excludedOrder.Add(sf);
            }
        }

        _currentPlaylistName = name;
        UpdatePlaylistLabel();
        RefreshCounts();
        return true;
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    private async void OnGenerateDats(object? sender, RoutedEventArgs e) =>
        await GenerateDatsAsync();

    private async Task GenerateDatsAsync()
    {
        if (_selectedCacheDir is null || _rules.Count == 0 || _generating) return;

        _generating = true;
        GenerateButton.IsEnabled = false;
        PlaylistStatusText.Text  = "Generating\u2026";

        var cacheDir         = _selectedCacheDir;
        var version          = _selectedVersion!;
        var rules            = _rules.ToList();
        var excludedSfs      = new HashSet<string>(_excludedSet, StringComparer.OrdinalIgnoreCase);
        var arcadeOnly       = ArcadeOnlyCheck.IsChecked        == true;
        var excludeDevices   = ExcludeDevicesCheck.IsChecked    == true;
        var excludeMech      = ExcludeMechanicalCheck.IsChecked == true;
        var runnableOnly     = RunnableOnlyCheck.IsChecked      == true;
        var includeBios      = IncludeBiosCheck.IsChecked       == true;
        var splitParentsOnly = SetLayoutBox.SelectedIndex       == 1;
        var arcadeNames      = _arcadeNames;
        var categoryIndex    = _categoryIndex;
        var now              = DateTime.Now;

        try
        {
            List<MameMachine> allMachines;
            if (_cachedMachines is not null && _cachedForVersion == version)
            {
                allMachines = _cachedMachines;
                AppendLog($"Using {allMachines.Count:N0} cached machine entries.");
            }
            else
            {
                var listxmlPath = Path.Combine(cacheDir, "listxml.xml");
                if (!File.Exists(listxmlPath))
                    throw new FileNotFoundException($"listxml.xml not found in {cacheDir}");
                AppendLog($"Parsing listxml for MAME {version}\u2026");
                allMachines = await Task.Run(() => ParseListXml(listxmlPath, CancellationToken.None));
                AppendLog($"Parsed {allMachines.Count:N0} machine entries.");
            }

            var machines = allMachines.ToList();

            if (excludeDevices)
            {
                int r = machines.RemoveAll(m => m.IsDevice);
                if (r > 0) AppendLog($"Exclude devices: {r:N0} removed, {machines.Count:N0} remain.");
            }
            if (excludeMech)
            {
                int r = machines.RemoveAll(m => m.IsMechanical);
                if (r > 0) AppendLog($"Exclude mechanical: {r:N0} removed, {machines.Count:N0} remain.");
            }
            if (arcadeOnly)
            {
                if (arcadeNames.Count > 0)
                {
                    int r = machines.RemoveAll(m => !arcadeNames.Contains(m.Name));
                    AppendLog($"Arcade-only: {r:N0} non-arcade removed, {machines.Count:N0} remain.");
                }
                else
                    AppendLog("Arcade-only filter skipped — category.ini not loaded.", "#FFA726");
            }
            if (runnableOnly)
            {
                int r = machines.RemoveAll(m => m.DriverStatus == "preliminary");
                if (r > 0) AppendLog($"Runnable-only: {r:N0} preliminary removed, {machines.Count:N0} remain.");
            }
            if (!includeBios)
            {
                int r = machines.RemoveAll(m => m.IsBios);
                if (r > 0) AppendLog($"No-BIOS: {r:N0} BIOS removed, {machines.Count:N0} remain.");
            }
            if (excludedSfs.Count > 0)
            {
                int r = machines.RemoveAll(m => excludedSfs.Contains(m.Sourcefile));
                if (r > 0) AppendLog($"Excluded drivers: {r:N0} machines removed ({excludedSfs.Count} drivers).");
            }
            if (splitParentsOnly)
            {
                int r = machines.RemoveAll(m => !string.IsNullOrEmpty(m.Cloneof));
                if (r > 0) AppendLog($"Parents-only: {r:N0} clones removed, {machines.Count:N0} remain.");
            }

            var (outputDir, runIndex) = GetPlaylistOutputDir(cacheDir, now);
            Directory.CreateDirectory(outputDir);
            AppendLog($"Output: {Path.GetRelativePath(AppContext.BaseDirectory, outputDir)}");

            var remainder  = machines.ToList();
            var datEntries = new List<(string File, int Count)>();

            foreach (var rule in rules)
            {
                var matched = remainder.Where(m => MatchesRule(m, rule, categoryIndex)).ToList();
                foreach (var m in matched) remainder.Remove(m);

                var safeName = SanitizeFileName(rule.DisplayName);
                var datPath  = Path.Combine(outputDir, $"{safeName}.dat");
                await Task.Run(() => WriteDat(datPath, $"MAME {version} - {rule.DisplayName}", version, matched, categoryIndex));
                AppendLog($"  {safeName}.dat  \u2014  {matched.Count:N0} machine(s)");
                datEntries.Add(($"{safeName}.dat", matched.Count));
            }

            var otherPath = Path.Combine(outputDir, "Other.dat");
            await Task.Run(() => WriteDat(otherPath, $"MAME {version} - Other", version, remainder, categoryIndex));
            AppendLog($"  Other.dat  \u2014  {remainder.Count:N0} machine(s)");
            datEntries.Add(("Other.dat", remainder.Count));

            WriteManifest(outputDir, version, now, runIndex, rules,
                arcadeOnly, excludeDevices, excludeMech, includeBios, runnableOnly,
                splitParentsOnly, [.. excludedSfs], datEntries);
            AppendLog("  manifest.json written.");

            var relOut = Path.GetRelativePath(AppContext.BaseDirectory, outputDir);
            AppendLog($"Done \u2014 {datEntries.Count} DAT(s) written to {relOut}", "#4CAF50");
            PlaylistStatusText.Text = $"Generated {datEntries.Count} DAT(s) in {Path.GetFileName(outputDir)}";
        }
        catch (Exception ex)
        {
            AppendLog($"Failed: {ex.Message}", "#EF5350");
            PlaylistStatusText.Text = "Generation failed. See log.";
        }
        finally
        {
            _generating = false;
            UpdateState();
        }
    }

    private static bool MatchesRule(MameMachine m, FamilyRule rule, CategoryIndex? categoryIndex) =>
        rule.RuleType switch
        {
            FamilyRule.SourcefileIn     =>
                rule.RuleValues.Any(v => string.Equals(m.Sourcefile, v, StringComparison.OrdinalIgnoreCase)),
            FamilyRule.CategoryContains =>
                categoryIndex?.MatchesCategoryContains(m.Name, rule.RuleValues) ?? false,
            FamilyRule.NonArcade        =>
                categoryIndex?.IsNonArcade(m.Name) ?? false,
            _ => false,
        };

    // ── listxml parser ────────────────────────────────────────────────────────

    private static List<MameMachine> ParseListXml(string path, CancellationToken ct)
    {
        var machines = new List<MameMachine>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing    = DtdProcessing.Ignore,
            ValidationType   = ValidationType.None,
            IgnoreComments   = true,
            IgnoreWhitespace = true,
        };

        using var reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "machine") continue;

            var name         = reader.GetAttribute("name")         ?? "";
            var cloneof      = reader.GetAttribute("cloneof")      ?? "";
            var romof        = reader.GetAttribute("romof")        ?? "";
            var sourcefile   = reader.GetAttribute("sourcefile")   ?? "";
            var isBios       = reader.GetAttribute("isbios")       == "yes";
            var isDevice     = reader.GetAttribute("isdevice")     == "yes";
            var isMechanical = reader.GetAttribute("ismechanical") == "yes";

            string description  = "";
            string driverStatus = "";
            var    roms         = new List<MameRom>();
            var    disks        = new List<MameDisk>();

            using var sub = reader.ReadSubtree();
            sub.Read();
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element) continue;
                switch (sub.LocalName)
                {
                    case "description":
                        description = sub.ReadElementContentAsString();
                        break;
                    case "driver":
                        driverStatus = sub.GetAttribute("status") ?? "";
                        break;
                    case "rom":
                        roms.Add(new MameRom(
                            sub.GetAttribute("name")   ?? "",
                            sub.GetAttribute("size"),
                            sub.GetAttribute("crc"),
                            sub.GetAttribute("sha1"),
                            sub.GetAttribute("merge"),
                            sub.GetAttribute("status"),
                            sub.GetAttribute("region"),
                            sub.GetAttribute("offset")));
                        break;
                    case "disk":
                        disks.Add(new MameDisk(
                            sub.GetAttribute("name")   ?? "",
                            sub.GetAttribute("sha1"),
                            sub.GetAttribute("merge"),
                            sub.GetAttribute("status"),
                            sub.GetAttribute("region")));
                        break;
                }
            }

            machines.Add(new MameMachine(
                name, cloneof, romof, description, sourcefile,
                driverStatus, isBios, isDevice, isMechanical,
                roms.Count  > 0 ? roms.ToArray()  : [],
                disks.Count > 0 ? disks.ToArray() : []));
        }

        return machines;
    }

    // ── DAT writer ────────────────────────────────────────────────────────────

    private static void WriteDat(
        string path, string datName, string version, IEnumerable<MameMachine> machines,
        CategoryIndex? categoryIndex = null)
    {
        var xmlSettings = new XmlWriterSettings
        {
            Indent      = true,
            IndentChars = "\t",
            Encoding    = new UTF8Encoding(false),
        };

        using var writer = XmlWriter.Create(path, xmlSettings);
        writer.WriteStartDocument();
        writer.WriteDocType("datafile",
            "-//Logiqx//DTD ROM Management Datafile//EN",
            "http://www.logiqx.com/Dtd/datafile.dtd", null);

        writer.WriteStartElement("datafile");
        writer.WriteStartElement("header");
        writer.WriteElementString("name",        datName);
        writer.WriteElementString("description", datName);
        writer.WriteElementString("version",     version);
        writer.WriteElementString("date",        DateTime.Now.ToString("yyyy-MM-dd"));
        writer.WriteElementString("author",      "Arkadia");
        writer.WriteElementString("url",         "https://www.mamedev.org/");
        writer.WriteEndElement();

        foreach (var m in machines)
        {
            writer.WriteStartElement("machine");
            writer.WriteAttributeString("name", m.Name);
            if (!string.IsNullOrEmpty(m.Cloneof)) writer.WriteAttributeString("cloneof", m.Cloneof);
            if (!string.IsNullOrEmpty(m.Romof) && m.Romof != m.Name)
                writer.WriteAttributeString("romof", m.Romof);

            writer.WriteElementString("description", m.Description);

            writer.WriteStartElement("info");
            writer.WriteAttributeString("name",  "arkadia:working_state");
            writer.WriteAttributeString("value", WorkingState.FromMameDriverStatus(m.DriverStatus));
            writer.WriteEndElement();

            if (!string.IsNullOrEmpty(m.Sourcefile))
            {
                writer.WriteStartElement("info");
                writer.WriteAttributeString("name",  "arkadia:sourcefile");
                writer.WriteAttributeString("value", m.Sourcefile);
                writer.WriteEndElement();
            }

            var category = categoryIndex?.GetCategory(m.Name);
            if (category is not null)
            {
                writer.WriteStartElement("info");
                writer.WriteAttributeString("name",  "arkadia:category");
                writer.WriteAttributeString("value", category);
                writer.WriteEndElement();
            }

            foreach (var rom in m.Roms)
            {
                writer.WriteStartElement("rom");
                writer.WriteAttributeString("name", rom.Name);
                if (rom.Size   is not null) writer.WriteAttributeString("size",   rom.Size);
                if (rom.Crc    is not null) writer.WriteAttributeString("crc",    rom.Crc);
                if (rom.Sha1   is not null) writer.WriteAttributeString("sha1",   rom.Sha1);
                if (rom.Merge  is not null) writer.WriteAttributeString("merge",  rom.Merge);
                if (rom.Status is not null) writer.WriteAttributeString("status", rom.Status);
                if (rom.Region is not null) writer.WriteAttributeString("region", rom.Region);
                if (rom.Offset is not null) writer.WriteAttributeString("offset", rom.Offset);
                writer.WriteEndElement();
            }

            foreach (var disk in m.Disks)
            {
                writer.WriteStartElement("disk");
                writer.WriteAttributeString("name", disk.Name);
                if (disk.Sha1   is not null) writer.WriteAttributeString("sha1",   disk.Sha1);
                if (disk.Merge  is not null) writer.WriteAttributeString("merge",  disk.Merge);
                if (disk.Status is not null) writer.WriteAttributeString("status", disk.Status);
                if (disk.Region is not null) writer.WriteAttributeString("region", disk.Region);
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // machine
        }

        writer.WriteEndElement(); // datafile
        writer.WriteEndDocument();
    }

    // ── manifest.json ─────────────────────────────────────────────────────────

    private static void WriteManifest(
        string outputDir, string version, DateTime runTime, int runIndex,
        List<FamilyRule> rules,
        bool arcadeOnly, bool excludeDevices, bool excludeMech,
        bool includeBios, bool runnableOnly, bool splitParentsOnly,
        List<string> excludedSourcefiles,
        List<(string File, int Count)> dats)
    {
        var obj = new JsonObject
        {
            ["mameVersion"]  = version,
            ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["runDate"]      = runTime.ToString("yyyy-MM-dd"),
            ["runIndex"]     = runIndex,
            ["exportStyle"]  = "split",
            ["filters"] = new JsonObject
            {
                ["arcadeOnly"]        = arcadeOnly,
                ["excludeDevices"]    = excludeDevices,
                ["excludeMechanical"] = excludeMech,
                ["runnableOnly"]      = runnableOnly,
                ["includeBios"]       = includeBios,
                ["setLayout"]         = splitParentsOnly ? "split" : "non-merged",
            },
            ["excludedSourcefiles"] = new JsonArray(
                excludedSourcefiles.Select(s => (JsonNode)s).ToArray()),
            ["familyRules"] = new JsonArray(rules.Select(r => (JsonNode)new JsonObject
            {
                ["displayName"] = r.DisplayName,
                ["ruleType"]    = r.RuleType,
                ["ruleValues"]  = new JsonArray(r.RuleValues.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
            ["dats"] = new JsonArray(dats.Select(d => (JsonNode)new JsonObject
            {
                ["file"]         = d.File,
                ["machineCount"] = d.Count,
            }).ToArray()),
        };

        File.WriteAllText(
            Path.Combine(outputDir, "manifest.json"),
            obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Output directory ──────────────────────────────────────────────────────

    private static (string Dir, int Index) GetPlaylistOutputDir(string versionCacheDir, DateTime now)
    {
        var datStr = now.ToString("yyyy-MM-dd");
        for (int i = 1; i <= 999; i++)
        {
            var dir = Path.Combine(versionCacheDir, $"playlist_{datStr}_{i}");
            if (!Directory.Exists(dir)) return (dir, i);
        }
        return (Path.Combine(versionCacheDir, $"playlist_{datStr}_{now:HHmmss}"), -1);
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    private void UpdateState()
    {
        bool hasVersion = _selectedVersion is not null;
        bool hasRules   = _rules.Count > 0;
        bool hasRule    = _selectedRuleIdx >= 0 && _selectedRuleIdx < _rules.Count;

        GenerateButton.IsEnabled     = hasVersion && hasRules && !_generating;
        EditRuleButton.IsEnabled     = hasRule;
        DeleteRuleButton.IsEnabled   = hasRule;
        MoveUpButton.IsEnabled       = hasRule && _selectedRuleIdx > 0;
        MoveDownButton.IsEnabled     = hasRule && _selectedRuleIdx < _rules.Count - 1;
        SavePlaylistButton.IsEnabled  = hasRules || _excludedOrder.Count > 0;
        SaveFamiliesButton.IsEnabled  = hasRules;

        GenerateInfoText.Text =
            !hasVersion  ? "Select a cached version from the left."
            : !hasRules  ? "Add at least one family rule."
            : _generating ? "Generating\u2026"
            : $"Ready \u2014 {_rules.Count} rule(s), MAME {_selectedVersion}.";

        PlaylistStatusText.Text = hasVersion
            ? $"MAME {_selectedVersion} selected"
            : "No version selected.";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void AppendLog(string text, string color = "#888899") =>
        ProviderHelpers.AppendLog(PlaylistLogPanel, PlaylistLogScrollViewer, text, color);
}

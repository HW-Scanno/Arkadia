using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Arkadia.GroupDats;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Arkadia;

/// <summary>
/// Configure a whole Group DAT with ONE uniform configuration (File Handling + transform strategy + folder
/// transform), applied to every leaf. The Group persists no configuration of its own — the values live on
/// the individual <c>dat_lines</c> (overwrite-all). Validation is read-only and runs off the UI thread; the
/// atomic apply is performed by the caller via <see cref="CatalogService.ApplyDatGroupConfiguration"/> using
/// the frozen <see cref="Plan"/>. This dialog never mutates the catalog, leaf DBs, or the filesystem.
/// </summary>
public partial class ConfigureGroupDialog : Window
{
    private readonly string          _groupId;
    private readonly CatalogService  _catalog;
    private readonly string          _dataDir;
    private readonly List<DatLineRecord>   _leaves;
    private readonly List<TransformRecord> _folderXforms;
    private readonly List<TransformRecord> _allTransforms;

    private static readonly string[] FhValues    = { "archives_pre_extraction", "all_files" };
    private static readonly string[] FhLabels    = { "Archives Pre-Extraction", "All Files" };
    private static readonly string[] StratValues = { "none", "release_folder" };
    private static readonly string[] StratLabels = { "None", "Per release folder" };

    private GroupConfigurePlan? _validatedPlan;

    /// <summary>The frozen plan to apply; non-null only after a fully-clean validation and Apply.</summary>
    public GroupConfigurePlan? Plan { get; private set; }

    // Parameterless ctor for the Avalonia XAML compiler.
    public ConfigureGroupDialog() : this("", "Group", "", new CatalogService(""), "") { }

    public ConfigureGroupDialog(string groupId, string displayName, string authority, CatalogService catalog, string dataDir)
    {
        InitializeComponent();
        _groupId = groupId;
        _catalog = catalog;
        _dataDir = dataDir;

        _leaves        = groupId.Length > 0 ? _catalog.GetLeavesForGroup(groupId).Select(l => l.DatLine).ToList() : new();
        _allTransforms = _catalog.LoadTransforms();
        _folderXforms  = _allTransforms.Where(t => t.IsFolderStrategy && t.IsEnabled).ToList();

        HeaderName.Text = $"Configure Group — {displayName}";
        HeaderSub.Text  = $"{(authority.Length > 0 ? authority + " · " : "")}{_leaves.Count} leaf DAT{(_leaves.Count == 1 ? "" : "s")}";

        var preview = GroupConfigure.BuildPreview(_leaves);

        FhBox.ItemsSource    = FhLabels;
        FhBox.SelectedIndex  = IndexOf(FhValues, preview.CommonFileHandling);
        FhCurrent.Text       = "Current: " + Describe(preview.CommonFileHandling, FhValues, FhLabels);

        StratBox.ItemsSource   = StratLabels;
        StratBox.SelectedIndex = IndexOf(StratValues, preview.CommonTransformStrategy);
        StratCurrent.Text      = "Current: " + Describe(preview.CommonTransformStrategy, StratValues, StratLabels);

        FolderBox.ItemsSource = _folderXforms.Select(t => t.Name).ToList();
        if (preview.CommonFolderTransformId is { Length: > 0 } fid)
            FolderBox.SelectedIndex = _folderXforms.FindIndex(t => t.Id == fid);
        FolderCurrent.Text = "Current: " + (preview.FolderTransformMixed
            ? "Mixed"
            : preview.CommonFolderTransformId is { Length: > 0 } cf
                ? (_folderXforms.FirstOrDefault(t => t.Id == cf)?.Name ?? cf)
                : "none");

        UpdateFolderVisibility();
        ResetValidation("Choose a configuration, then Validate.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int IndexOf(string[] values, string? common)
        => common is null ? -1 : Array.IndexOf(values, common);

    private static string Describe(string? common, string[] values, string[] labels)
    {
        if (common is null) return "Mixed";
        var i = Array.IndexOf(values, common);
        return i >= 0 ? labels[i] : common;
    }

    private string SelectedStrategy => StratBox.SelectedIndex >= 0 ? StratValues[StratBox.SelectedIndex] : "";
    private string SelectedFileHandling => FhBox.SelectedIndex >= 0 ? FhValues[FhBox.SelectedIndex] : "";
    private string? SelectedFolderTransformId =>
        SelectedStrategy == "release_folder" && FolderBox.SelectedIndex >= 0 && FolderBox.SelectedIndex < _folderXforms.Count
            ? _folderXforms[FolderBox.SelectedIndex].Id : null;

    private void UpdateFolderVisibility() => FolderPanel.IsVisible = SelectedStrategy == "release_folder";

    private void OnStrategyChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateFolderVisibility();
        OnConfigChanged(sender, e);
    }

    // Any config change invalidates a prior validation — Apply must be re-earned.
    private void OnConfigChanged(object? sender, SelectionChangedEventArgs e) => ResetValidation("Configuration changed — Validate again.");

    private void ResetValidation(string status)
    {
        _validatedPlan          = null;
        ApplyButton.IsEnabled   = false;
        ValidateSpinner.IsVisible = false;
        ValidateStatus.Text     = status;
    }

    // ── Validate (read-only, off the UI thread) ─────────────────────────────

    private void OnValidate(object? sender, RoutedEventArgs e)
    {
        var fh    = SelectedFileHandling;
        var strat = SelectedStrategy;
        var ftId  = SelectedFolderTransformId;

        if (fh.Length == 0 || strat.Length == 0)
        {
            ResetValidation("Select File Handling and Transform Strategy first.");
            return;
        }
        if (GroupConfigure.ValidateConfigShape(strat, ftId) is { } shapeError)
        {
            ResetValidation(shapeError);
            return;
        }
        if (_leaves.Count == 0)
        {
            ResetValidation("This group has no leaves to configure.");
            return;
        }

        // Freeze the plan candidate NOW so a later combo change can't silently drift the applied config.
        var planned = new GroupConfigurePlan(
            _groupId,
            _leaves.Select(l => l.Id).ToImmutableArray(),
            fh, strat, ftId);

        var folderXform = ftId is { Length: > 0 }
            ? _allTransforms.FirstOrDefault(t => t.Id == ftId) : null;

        ValidateButton.IsEnabled  = false;
        ApplyButton.IsEnabled     = false;
        ValidateSpinner.IsVisible = true;
        ValidateStatus.Text       = $"Validating 0 / {_leaves.Count}…";

        var leaves    = _leaves.ToList();
        var dataDir   = _dataDir;
        var transforms= _allTransforms;
        var progress  = new Progress<int>(done => ValidateStatus.Text = $"Validating {done} / {leaves.Count}…");

        var work = System.Threading.Tasks.Task.Run(() =>
        {
            var results = new List<GroupConfigureLeafValidation>(leaves.Count);
            for (int i = 0; i < leaves.Count; i++)
            {
                // A leaf that cannot be opened/validated (missing/unreadable DB, load failure) → Error,
                // never Clean. Any Error blocks the whole Group Apply (AllClean requires every leaf Clean).
                results.Add(GroupConfigureLeafValidator.ValidateLeaf(leaves[i], dataDir, strat, folderXform, transforms));
                ((IProgress<int>)progress).Report(i + 1);
            }
            return results;
        });

        _ = work.ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            ValidateButton.IsEnabled  = true;
            ValidateSpinner.IsVisible = false;

            if (!t.IsCompletedSuccessfully)
            {
                ResetValidation("Validation failed unexpectedly.");
                ValidateButton.IsEnabled = true;
                return;
            }

            var results  = t.Result;
            if (GroupConfigure.AllClean(results))
            {
                _validatedPlan        = planned;
                ApplyButton.IsEnabled = true;
                ValidateStatus.Text   = $"{results.Count} / {results.Count} clean — ready to apply.";
            }
            else
            {
                var bad = results.Count(r => r.State != GroupConfigureLeafValidationState.Clean);
                _validatedPlan        = null;
                ApplyButton.IsEnabled = false;
                ValidateStatus.Text   = $"Validation failed — {bad} leaf DAT(s) require review. Apply is blocked.";
            }
        }), System.Threading.Tasks.TaskContinuationOptions.None);
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_validatedPlan is null) return;   // Apply is only enabled after an all-clean validation
        Plan = _validatedPlan;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

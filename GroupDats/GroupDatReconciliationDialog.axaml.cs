using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.GroupDats;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Arkadia;

/// <summary>
/// Non-mutating manual Group-DAT reconciliation preview. Receives ONLY an immutable catalog
/// snapshot — never a CatalogService/DatLineStore/connection string/data dir/write callback — and
/// uses the pure Phase-3A discovery service. It never writes to the DB or filesystem; Abort discards
/// only in-memory state. On Continue it produces a frozen <see cref="GroupDatReconciliationPlan"/>
/// (exposed via <see cref="Plan"/>) but executes nothing.
/// </summary>
public partial class GroupDatReconciliationDialog : Window
{
    private readonly GroupDatCatalogPreviewData _catalog;
    private readonly DatGroupSourceDiscoveryService _discoveryService = new();
    private DatGroupDiscoveryResult? _discovery;
    private GroupDatReconciliationSession _session = null!;
    private bool _building;

    /// <summary>The frozen plan, set only after a successful Continue. Not executed.</summary>
    public GroupDatReconciliationPlan? Plan { get; private set; }

    public GroupDatReconciliationDialog() : this(GroupDatCatalogPreviewData.Empty) { }

    public GroupDatReconciliationDialog(GroupDatCatalogPreviewData catalog)
    {
        InitializeComponent();
        _catalog = catalog;

        _building = true;
        // Target combo: new group + each existing group.
        ModeCombo.Items.Add(new ComboBoxItem { Content = "(New Group DAT)", Tag = null });
        foreach (var g in _catalog.ExistingGroups)
            ModeCombo.Items.Add(new ComboBoxItem { Content = $"{g.Id} — {g.DisplayName}", Tag = g.Id });
        ModeCombo.SelectedIndex = 0;

        FillOptionCombo(NewGroupFamilyCombo, _catalog.HardwareFamilies);
        FillOptionCombo(NewGroupAuthorityCombo, _catalog.Authorities);
        FillOptionCombo(MediaTypeCombo, _catalog.MediaTypes);
        _building = false;

        RebuildSession();
    }

    private static void FillOptionCombo(ComboBox combo, IEnumerable<GroupDatOption> options)
    {
        combo.Items.Clear();
        foreach (var o in options)
            combo.Items.Add(new ComboBoxItem { Content = o.Name, Tag = o.Id });
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? SelectedId(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    // ── Session lifecycle ────────────────────────────────────────────────────

    private void RebuildSession()
    {
        // Changing the target discards prior decisions (a fresh session) and clears editable fields
        // so no stale new-group / new-leaf input carries over.
        _building = true;
        NewGroupIdField.Text = ""; NewGroupDisplayField.Text = "";
        DatTokenField.Text = ""; FinalIdField.Text = ""; IdStatusText.Text = "";
        _building = false;

        var groupId = SelectedId(ModeCombo);
        if (groupId is null)
        {
            _session = GroupDatReconciliationSession.ForNewGroup(_catalog);
            NewGroupPanel.IsVisible = true;
        }
        else
        {
            var group = _catalog.ExistingGroups.First(g => g.Id == groupId);
            _session = GroupDatReconciliationSession.ForExistingGroup(_catalog, group);
            NewGroupPanel.IsVisible = false;
        }
        if (_discovery is not null) _session.SetDiscovery(_discovery);
        SyncNewGroupFields();
        RefreshAll();
    }

    private void SyncNewGroupFields()
    {
        if (_session.Mode != GroupDatReconciliationMode.NewGroup) return;
        _session.NewGroupId              = NewGroupIdField.Text?.Trim() ?? "";
        _session.NewGroupDisplayName     = NewGroupDisplayField.Text?.Trim() ?? "";
        _session.NewGroupHardwareFamilyId = SelectedId(NewGroupFamilyCombo) ?? "";
        _session.NewGroupAuthority        = SelectedId(NewGroupAuthorityCombo) ?? "";
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private async void OnPickSource(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Group DAT source folder",
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not string path) return;

        SourcePathText.Text = path;
        try
        {
            _discovery = _discoveryService.Discover(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Discovery failed: " + ex.Message;
            return;
        }
        _session.SetDiscovery(_discovery);
        RefreshAll();
    }

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        RebuildSession();
    }

    private void OnAnyChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building) return;
        SyncNewGroupFields();
        RefreshValidation();
        RefreshIdStatus();
    }

    private void OnAnyChangedSel(object? sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        SyncNewGroupFields();
        RefreshValidation();
    }

    private void OnDatSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        var dat = SelectedDat();
        if (dat is not null)
        {
            _building = true;
            DatTokenField.Text = dat.DatToken;
            FinalIdField.Text  = _session.EffectiveIdFor(dat);   // manual override wins, else auto proposal
            _building = false;
        }
        RefreshComparison();
        RefreshIdStatus();
    }

    private void OnFinalIdChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building) return;
        var dat = SelectedDat();
        if (dat is not null)
            _session.SetManualFinalId(dat.CandidateId, FinalIdField.Text?.Trim() ?? "");   // mark manual
        RefreshIdStatus();
    }

    private void OnLeafSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        RefreshComparison();
    }

    private void OnDatTokenChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building) return;
        var dat = SelectedDat();
        if (dat is null) return;
        dat.DatToken = DatTokenField.Text?.Trim() ?? "";
        _building = true;
        FinalIdField.Text = _session.EffectiveIdFor(dat);   // auto recomputes; a manual override stays
        _building = false;
        RefreshIdStatus();
    }

    private void OnAssociate(object? sender, RoutedEventArgs e)
    {
        var dat = SelectedDat(); var leaf = SelectedLeaf();
        if (dat is null || leaf is null) { SummaryText.Text = "Select one DAT and one leaf to associate."; return; }
        try { _session.AssociateUpdate(dat.CandidateId, leaf.DatLineId); }
        catch (Exception ex) { SummaryText.Text = ex.Message; return; }
        RefreshAll();
    }

    private void OnCreateNewLeaf(object? sender, RoutedEventArgs e)
    {
        var dat = SelectedDat();
        if (dat is null) { SummaryText.Text = "Select a DAT to create a new leaf."; return; }
        try { _session.CreateNewLeaf(dat.CandidateId, FinalIdField.Text?.Trim() ?? "", SelectedId(MediaTypeCombo) ?? ""); }
        catch (Exception ex) { SummaryText.Text = ex.Message; return; }
        RefreshAll();
    }

    private void OnMarkAbsent(object? sender, RoutedEventArgs e)
    {
        var leaf = SelectedLeaf();
        if (leaf is null) { SummaryText.Text = "Select a leaf to mark absent."; return; }
        try { _session.MarkLeafAbsent(leaf.DatLineId); }
        catch (Exception ex) { SummaryText.Text = ex.Message; return; }
        RefreshAll();
    }

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if ((DecisionsList.SelectedItem as ListBoxItem)?.Tag is not string decisionId) return;
        _session.Undo(decisionId);
        RefreshAll();
    }

    private void OnAbort(object? sender, RoutedEventArgs e) => Close(false);   // discards in-memory state only

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        SyncNewGroupFields();
        if (!_session.CanBuildPlan) { RefreshValidation(); return; }
        Plan = _session.BuildPlan();
        Close(true);
    }

    // ── Selection helpers ────────────────────────────────────────────────────

    private IncomingDatCandidate? SelectedDat() =>
        (DatsList.SelectedItem as ListBoxItem)?.Tag as IncomingDatCandidate;

    private ExistingGroupLeafCandidate? SelectedLeaf() =>
        (LeavesList.SelectedItem as ListBoxItem)?.Tag as ExistingGroupLeafCandidate;

    // ── Rendering ────────────────────────────────────────────────────────────

    private void RefreshAll()
    {
        _building = true;
        DatsList.Items.Clear();
        foreach (var c in _session.AvailableIncoming)
            DatsList.Items.Add(new ListBoxItem
            {
                Tag = c,
                Content = $"{c.RelativePath}   ·   {c.HeaderName} v{c.Version}   ·   {c.ReleaseCount} rel   ·   {c.Date}/{c.Author}",
            });

        LeavesList.Items.Clear();
        foreach (var l in _session.AvailableLeaves)
            LeavesList.Items.Add(new ListBoxItem
            {
                Tag = l,
                Content = $"{l.DatLineId}   ·   {l.Leaf.MediaTypeId}   ·   {l.Leaf.ReleaseCount} rel   ·   rev {l.Leaf.LastSeenGroupRevision?.ToString() ?? "—"}",
            });

        DecisionsList.Items.Clear();
        foreach (var d in _session.Decisions)
            DecisionsList.Items.Add(new ListBoxItem { Tag = d.DecisionId, Content = DecisionLine(d) });
        _building = false;

        RefreshComparison();
        RefreshValidation();
        RefreshIdStatus();
    }

    private static string DecisionLine(GroupDatDecision d) => d.Kind switch
    {
        GroupDatDecisionKind.Update  => $"UPDATE   {d.Leaf!.DatLineId}  ←  {d.Dat!.RelativePath}",
        GroupDatDecisionKind.NewLeaf => $"NEW      {d.FinalId}  ←  {d.Dat!.RelativePath}  [{d.MediaTypeId}]",
        _                            => $"ABSENT   {d.Leaf!.DatLineId}",
    };

    private void RefreshComparison()
    {
        var dat = SelectedDat(); var leaf = SelectedLeaf();
        if (dat is null && leaf is null)
        {
            ComparisonText.Text = "Select a DAT (left) and a leaf (right) to compare.";
            return;
        }
        static string L(string s) => (s ?? "").PadRight(34);
        var d = dat; var l = leaf?.Leaf;
        ComparisonText.Text = string.Join('\n', new[]
        {
            $"{L("NEW DAT"),-34}EXISTING LEAF",
            $"{L(d?.RelativePath ?? "—")}{l?.DatLineId ?? "—"}",
            $"{L("name: " + (d?.HeaderName ?? "—"))}source: {l?.SourceDatName ?? "—"}",
            $"{L("version: " + (d?.Version ?? "—"))}version: {l?.Version ?? "—"}",
            $"{L("date: " + (d?.Date ?? "—"))}date: not available",
            $"{L("author: " + (d?.Author ?? "—"))}author: not available",
            $"{L("releases: " + (d?.ReleaseCount.ToString() ?? "—"))}releases: {l?.ReleaseCount.ToString() ?? "—"}",
            $"{L("media: " + (SelectedId(MediaTypeCombo) ?? "—"))}media: {l?.MediaTypeId ?? "—"}",
        });
    }

    private void RefreshIdStatus()
    {
        if (_session is null) return;
        var id = FinalIdField.Text?.Trim() ?? "";
        if (id.Length == 0) { IdStatusText.Text = ""; return; }
        var e = _session.EvaluateNewLeafId(id);
        IdStatusText.Text = e.IsValid
            ? (e.ExceedsRecommendedLength ? "id valid (exceeds 48-char recommendation)" : "id valid")
            : "id invalid — " + (e.Reason ?? "");
        IdStatusText.Foreground = e.IsValid
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4CAF50"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EF5350"));
    }

    private void RefreshValidation()
    {
        if (_session is null) return;
        var reasons = _session.BlockingReasons();
        SummaryText.Text = reasons.Count == 0
            ? "Ready. Plan validated — execution will be enabled in a later phase."
            : "Cannot continue yet:\n• " + string.Join("\n• ", reasons);
        SummaryText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(reasons.Count == 0 ? "#4CAF50" : "#FFA726"));
        ContinueButton.IsEnabled = reasons.Count == 0;
    }
}

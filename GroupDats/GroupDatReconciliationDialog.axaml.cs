using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.GroupDats;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Arkadia;

/// <summary>
/// Non-mutating manual Group-DAT reconciliation preview with a <b>caller-fixed mode</b>.
/// <b>Create</b> (<c>catalog, systemId, systemName</c>): a new group under the selected System — the
/// System is read-only, while Authority / Group Name / Group ID are editable (Group ID validated and
/// case-insensitively collision-checked before creation, immutable after). <b>Update</b>
/// (<c>catalog, existingGroupId</c>): all identity + existing leaves are loaded from the catalog
/// snapshot and are read-only. Both modes drive the same sequential one-DAT-at-a-time flow. The
/// window receives only an immutable snapshot and the pure Phase-3A discovery; it writes nothing.
/// </summary>
public partial class GroupDatReconciliationDialog : Window
{
    private readonly GroupDatCatalogPreviewData _catalog;
    private readonly DatGroupSourceDiscoveryService _discoveryService = new();
    private DatGroupDiscoveryResult? _discovery;
    private GroupDatReconciliationSession _session = null!;
    private bool _building;
    private string? _lastBulkMediaTypeId;   // last "Apply to remaining DATs" default (for Reset leaf proposal)

    public GroupDatReconciliationPlan? Plan { get; private set; }

    public GroupDatReconciliationDialog() : this(GroupDatCatalogPreviewData.Empty, "system", "System") { }

    /// <summary>Create mode — a new group under the given System context.</summary>
    public GroupDatReconciliationDialog(
        GroupDatCatalogPreviewData catalog, string systemId, string systemName, string manufacturer = "")
    {
        InitializeComponent();
        _catalog = catalog;
        var name = string.IsNullOrWhiteSpace(systemName) ? systemId : systemName;

        TitleText.Text         = "Create Group DAT";
        SystemContextText.Text = $"{name}   ·   {systemId}";
        AuthorityCombo.IsVisible = true;
        AuthorityText.IsVisible  = false;

        _building = true;
        FillOptionCombo(AuthorityCombo, _catalog.Authorities);
        FillOptionCombo(MediaTypeCombo, _catalog.MediaTypes, selectFirst: false);
        _building = false;

        var authority   = SelectedId(AuthorityCombo) ?? "";
        var authDisplay = _catalog.Authorities.FirstOrDefault(a => a.Id == authority)?.Name ?? authority;
        var groupId     = GroupDatReconciliationSession.SuggestGroupId(systemId, authority);
        var groupName   = GroupDatReconciliationSession.ComposeGroupName(manufacturer, name, authDisplay);
        _session = GroupDatReconciliationSession.ForNewGroup(
            _catalog, systemId, name, authority, groupName, groupId, manufacturer);

        InitCommonForMode();
    }

    /// <summary>Update mode — bound to a specific existing Group ID (identity read-only).</summary>
    public GroupDatReconciliationDialog(GroupDatCatalogPreviewData catalog, string existingGroupId)
    {
        InitializeComponent();
        _catalog = catalog;
        _session = GroupDatReconciliationSession.ForExistingGroup(_catalog, existingGroupId);

        TitleText.Text         = "Update Group DAT";
        SystemContextText.Text = $"{_session.SystemName}   ·   {_session.SystemId}";
        AuthorityCombo.IsVisible = false;
        AuthorityText.IsVisible  = true;
        AuthorityText.Text       = _catalog.Authorities.FirstOrDefault(a => a.Id == _session.Authority)?.Name
                                   ?? _session.Authority;

        _building = true;
        FillOptionCombo(MediaTypeCombo, _catalog.MediaTypes, selectFirst: false);
        _building = false;

        InitCommonForMode();
    }

    private bool NewMode => _session.Mode == GroupDatReconciliationMode.NewGroup;

    private void InitCommonForMode()
    {
        _building = true;
        GroupNameField.Text   = _session.GroupName;
        GroupIdField.Text     = _session.GroupId;
        GroupNameField.IsEnabled = NewMode;   // read-only in Update mode
        GroupIdField.IsEnabled   = NewMode;
        _building = false;

        // Create mode: every discovered DAT is an implicit proposal — no per-DAT decision UI.
        // Update mode: keep the manual decision workflow (associate/create/absent/undo).
        UpdateActionsPanel.IsVisible  = !NewMode;
        DecisionsPanel.IsVisible      = !NewMode;
        CreateLeafButton.IsVisible    = !NewMode;
        ResetProposalButton.IsVisible = NewMode;
        LeftHeader.Text = NewMode ? "DISCOVERED DATS" : "DISCOVERED DATS (AVAILABLE)";

        RefreshGroupIdStatus();
        RefreshAll();
    }

    private static void FillOptionCombo(ComboBox combo, IEnumerable<GroupDatOption> options, bool selectFirst = true)
    {
        combo.Items.Clear();
        foreach (var o in options) combo.Items.Add(new ComboBoxItem { Content = o.Name, Tag = o.Id });
        if (selectFirst && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? SelectedId(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    // ── Identity edits (Create mode only) ───────────────────────────────────────

    private void OnAuthorityChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Real selection change only (programmatic combo init runs under _building). Selecting the
        // same authority raises no SelectionChanged, so nothing needs simulating here.
        if (_building || !NewMode) return;
        _session.SetAuthority(SelectedId(AuthorityCombo) ?? "");
        _building = true;
        GroupNameField.Text = _session.GroupName;   // re-suggested when not manually overridden
        GroupIdField.Text   = _session.GroupId;
        _building = false;
        RefreshGroupIdStatus();
        RefreshAll();   // Group ID feeds leaf-id proposals
    }

    private void OnGroupNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building || !NewMode) return;      // programmatic assignment never marks manual
        _session.SetGroupName(GroupNameField.Text?.Trim() ?? "");
        RefreshValidation();   // Group Name never affects leaf ids
        RefreshIdentityState();
    }

    private void OnGroupIdChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building || !NewMode) return;      // programmatic assignment never marks manual
        _session.SetGroupId(GroupIdField.Text?.Trim() ?? "");
        GroupIdText.Text = _session.GroupId;
        RefreshGroupIdStatus();
        RefreshDatsList();
        RefreshDetailAndBuilder();   // recomputes non-overridden proposals from the new prefix
        RefreshValidation();
        RefreshIdentityState();
    }

    private void OnResetIdentity(object? sender, RoutedEventArgs e)
    {
        if (!NewMode || !_session.CanResetIdentity) return;
        _session.ResetIdentityToSuggested();   // clears manual overrides, recomputes from System + Authority
        _building = true;
        GroupNameField.Text = _session.GroupName;
        GroupIdField.Text   = _session.GroupId;
        _building = false;
        GroupIdText.Text = _session.GroupId;
        RefreshGroupIdStatus();
        RefreshDatsList();
        RefreshDetailAndBuilder();   // re-prefix non-overridden leaf proposals; manual Final IDs preserved
        RefreshValidation();
        RefreshIdentityState();
    }

    private void RefreshIdentityState()
    {
        if (!NewMode)
        {
            // Update mode: identity is read-only — no reset button, no status line.
            ResetIdentityButton.IsVisible = false;
            IdentityStateText.IsVisible   = false;
            return;
        }
        ResetIdentityButton.IsVisible = true;
        IdentityStateText.IsVisible   = true;

        var custom = _session.IsIdentityCustom;
        ResetIdentityButton.IsEnabled = _session.CanResetIdentity && custom;
        IdentityStateText.Text = custom ? "Custom identity" : "Suggested from System and Authority";
        IdentityStateText.Foreground = new SolidColorBrush(Color.Parse(custom ? "#E8A000" : "#888899"));
    }

    // ── Apply selected leaf's media type to the remaining DATs ──────────────────

    private void OnApplyDefaultMedia(object? sender, RoutedEventArgs e)
    {
        // Value source is the selected leaf's Media Type combo — no separate default state.
        if (SelectedDat() is not { } dat || SelectedId(MediaTypeCombo) is not { } media) return;

        _lastBulkMediaTypeId = media;
        var (updated, preserved) = _session.ApplyDefaultMediaTypeToUnresolved(media, dat.CandidateId);
        var mediaName = (MediaTypeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? media;

        // Pre-filling media changes proposal validity → refresh markers + global validation immediately.
        RefreshDatsList();
        RefreshValidation();
        RefreshApplyDefaultMediaState();

        // Feedback (bottom validation/status area) above the refreshed summary. The source leaf keeps
        // its value and is excluded from both counts.
        var applied = preserved > 0
            ? $"Applied {mediaName} to {updated} other remaining DAT(s); {preserved} manual override(s) preserved."
            : $"Applied {mediaName} to {updated} other remaining DAT(s).";
        SummaryText.Text = applied + "\n" + SummaryText.Text;
        SummaryText.Foreground = new SolidColorBrush(Color.Parse("#4CAF50"));
    }

    private void RefreshApplyDefaultMediaState()
    {
        ApplyDefaultMediaButton.IsEnabled =
            SelectedDat() is not null
            && SelectedId(MediaTypeCombo) is not null
            && _session.AvailableIncoming.Count > 0;
    }

    // ── Source & selection ──────────────────────────────────────────────────────

    private async void OnPickSource(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Group DAT source folder" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not string path) return;
        SourcePathText.Text = path;
        try { _discovery = _discoveryService.Discover(path, CancellationToken.None); }
        catch (Exception ex) { SummaryText.Text = "Discovery failed: " + ex.Message; return; }
        _session.SetDiscovery(_discovery);
        RefreshAll();
    }

    private void OnDatSelected(object? sender, SelectionChangedEventArgs e)
    { if (!_building) RefreshDetailAndBuilder(); RefreshComparison(); }

    private void OnLeafSelected(object? sender, SelectionChangedEventArgs e) { if (!_building) RefreshComparison(); }

    private void OnDatTokenChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building) return;
        if (SelectedDat() is not { } dat) return;
        dat.DatToken = DatTokenField.Text?.Trim() ?? "";
        _building = true; FinalIdField.Text = _session.EffectiveIdFor(dat); _building = false;
        RefreshIdStatus(); RefreshDatsList(); RefreshValidation();
    }

    private void OnFinalIdChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building) return;
        if (SelectedDat() is { } dat) _session.SetManualFinalId(dat.CandidateId, FinalIdField.Text?.Trim() ?? "");
        RefreshIdStatus(); RefreshDatsList(); RefreshValidation();
    }

    private void OnMediaTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        if (SelectedDat() is { } dat && SelectedId(MediaTypeCombo) is { } media)
            _session.SetDraftMediaType(dat.CandidateId, media);
        RefreshApplyDefaultMediaState();   // Apply button depends on the current combo value
        RefreshDatsList();                 // media affects the proposal's validity marker
        RefreshValidation();
    }

    private void OnResetId(object? sender, RoutedEventArgs e)
    {
        if (SelectedDat() is not { } dat) return;
        _session.ClearManualFinalId(dat.CandidateId);
        _building = true; FinalIdField.Text = _session.EffectiveIdFor(dat); _building = false;
        RefreshIdStatus(); RefreshDatsList(); RefreshValidation();
    }

    // ── Create-mode implicit-proposal commands ──────────────────────────────────

    private void OnResetProposal(object? sender, RoutedEventArgs e)
    {
        if (!NewMode || SelectedDat() is not { } dat) return;
        _session.ResetProposal(dat.CandidateId, _lastBulkMediaTypeId);
        RefreshDetailAndBuilder();   // repaint suffix / auto Final ID / media for the selection
        RefreshDatsList();
        RefreshValidation();
    }

    private void OnSelectNextIssue(object? sender, RoutedEventArgs e)
    {
        if (_session.FirstInvalidProposal() is not { } bad) return;
        var item = DatsList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(i => (i.Tag as IncomingDatCandidate)?.CandidateId == bad.CandidateId);
        if (item is not null) { DatsList.SelectedItem = item; DatsList.ScrollIntoView(item); }
    }

    // ── Decisions ────────────────────────────────────────────────────────────

    private void OnCreateNewLeaf(object? sender, RoutedEventArgs e)
    {
        if (SelectedDat() is not { } dat) { SummaryText.Text = "Select a DAT."; return; }
        try { _session.CreateNewLeaf(dat.CandidateId, FinalIdField.Text?.Trim() ?? "", SelectedId(MediaTypeCombo) ?? ""); }
        catch (Exception ex) { SummaryText.Text = ex.Message; return; }
        RefreshAll();
    }

    private void OnAssociate(object? sender, RoutedEventArgs e)
    {
        if (SelectedDat() is not { } dat || SelectedLeaf() is not { } leaf) { SummaryText.Text = "Select one DAT and one leaf."; return; }
        try { _session.AssociateUpdate(dat.CandidateId, leaf.DatLineId); } catch (Exception ex) { SummaryText.Text = ex.Message; return; }
        RefreshAll();
    }

    private void OnMarkAbsent(object? sender, RoutedEventArgs e)
    {
        if (SelectedLeaf() is not { } leaf) { SummaryText.Text = "Select a leaf."; return; }
        try { _session.MarkLeafAbsent(leaf.DatLineId); } catch (Exception ex) { SummaryText.Text = ex.Message; return; }
        RefreshAll();
    }

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if ((DecisionsList.SelectedItem as ListBoxItem)?.Tag is not string id) return;
        _session.Undo(id); RefreshAll();
    }

    private void OnAbort(object? sender, RoutedEventArgs e) => Close(false);

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        if (!_session.CanBuildPlan) { RefreshValidation(); return; }
        Plan = _session.BuildPlan();
        Close(true);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    private IncomingDatCandidate? SelectedDat() => (DatsList.SelectedItem as ListBoxItem)?.Tag as IncomingDatCandidate;
    private ExistingGroupLeafCandidate? SelectedLeaf() => (LeavesList.SelectedItem as ListBoxItem)?.Tag as ExistingGroupLeafCandidate;

    // ── Rendering ────────────────────────────────────────────────────────────

    private void RefreshAll()
    {
        RefreshDatsList();
        _building = true;
        LeavesList.Items.Clear();
        if (!NewMode)
            foreach (var l in _session.AvailableLeaves)
                LeavesList.Items.Add(new ListBoxItem { Tag = l, Content = $"{l.DatLineId}   ·   {l.Leaf.MediaTypeId}   ·   {l.Leaf.ReleaseCount} rel" });
        DecisionsList.Items.Clear();
        foreach (var d in _session.Decisions)
            DecisionsList.Items.Add(new ListBoxItem { Tag = d.DecisionId, Content = DecisionLine(d) });
        _building = false;

        // Group ID / authority are locked once any leaf decision exists (avoids prefix drift).
        if (NewMode)
        {
            var noDecisions = _session.Decisions.Count == 0;
            AuthorityCombo.IsEnabled = noDecisions;
            GroupIdField.IsEnabled   = noDecisions;
        }

        RefreshDetailAndBuilder();
        RefreshComparison();
        RefreshApplyDefaultMediaState();
        RefreshIdentityState();
        RefreshValidation();
    }

    private void RefreshDatsList()
    {
        var prev = SelectedDat()?.CandidateId;
        _building = true;
        DatsList.Items.Clear();

        if (NewMode)
        {
            // Create: show ALL discovered DATs (implicit proposals) with a discreet issue marker.
            var counts = _session.ProposalIdCounts();
            foreach (var c in _session.Proposals)
            {
                var folder = c.FolderPath.Length == 0 ? "(root)" : c.FolderPath;
                var ok = _session.EvaluateProposal(c, counts) == GroupDatReconciliationSession.LeafProposalIssue.Valid;
                var mark = ok ? "" : "   ⚠";
                DatsList.Items.Add(new ListBoxItem { Tag = c, Content = $"{folder}  ▸  {c.FileName}   ·   {c.ReleaseCount} rel{mark}" });
            }
        }
        else
        {
            foreach (var c in _session.AvailableIncoming)
            {
                var folder = c.FolderPath.Length == 0 ? "(root)" : c.FolderPath;
                DatsList.Items.Add(new ListBoxItem { Tag = c, Content = $"{folder}  ▸  {c.FileName}   ·   {c.ReleaseCount} rel" });
            }
        }

        if (prev is not null)
            DatsList.SelectedItem = DatsList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => (i.Tag as IncomingDatCandidate)?.CandidateId == prev);
        _building = false;
    }

    private static string DecisionLine(GroupDatDecision d) => d.Kind switch
    {
        GroupDatDecisionKind.Update  => $"UPDATE   {d.Leaf!.DatLineId}  ←  {d.Dat!.RelativePath}",
        GroupDatDecisionKind.NewLeaf => $"NEW      {d.FinalId}  ←  {d.Dat!.RelativePath}  [{d.MediaTypeId}]",
        _                            => $"ABSENT   {d.Leaf!.DatLineId}",
    };

    private void RefreshDetailAndBuilder()
    {
        var dat = SelectedDat();
        if (dat is null)
        {
            DetailText.Text = "Select a DAT on the left.";
            IdBuilderPanel.IsVisible = false;
            CreateLeafButton.IsEnabled = false;
            ResetProposalButton.IsEnabled = false;
            RefreshApplyDefaultMediaState();   // no selection ⇒ Apply disabled
            return;
        }
        IdBuilderPanel.IsVisible      = true;
        CreateLeafButton.IsEnabled    = true;
        ResetProposalButton.IsEnabled = NewMode;
        DetailText.Text =
            $"relative path : {dat.RelativePath}\n" +
            $"header name   : {dat.HeaderName}\n" +
            $"version       : {dat.Version}\n" +
            $"date          : {dat.Date}\n" +
            $"author        : {dat.Author}\n" +
            $"releases      : {dat.ReleaseCount}";

        GroupIdText.Text = _session.GroupId;

        _building = true;
        BuildFolderTokenRows(dat);
        DatTokenField.Text = dat.DatToken;
        FinalIdField.Text  = _session.EffectiveIdFor(dat);
        MediaTypeCombo.SelectedItem = MediaTypeCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag as string) == dat.DraftMediaTypeId);
        _building = false;
        RefreshIdStatus();
        RefreshApplyDefaultMediaState();   // Apply availability tracks the selected leaf + combo
    }

    private void BuildFolderTokenRows(IncomingDatCandidate dat)
    {
        FolderTokensPanel.Children.Clear();
        if (_session.FolderTree is null) return;
        var segments = dat.RelativePath.Split('/');
        var pathSoFar = "";
        for (int i = 0; i < segments.Length - 1; i++)
        {
            pathSoFar = pathSoFar.Length == 0 ? segments[i] : pathSoFar + "/" + segments[i];
            var node = _session.FolderTree.NodeForFolder(pathSoFar);
            if (node is null) continue;
            // Match the shared builder grid (label 150 / content *) so every token TextBox starts on
            // the same content-column line as Dat Suffix, Media type, and Final id.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
            var seg = new TextBlock
            {
                Text = segments[i],
                Foreground = new SolidColorBrush(Color.Parse("#AAAACC")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(seg, 0);
            row.Children.Add(seg);
            var tb = new TextBox { Text = node.Token, Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Tag = node };
            tb.TextChanged += OnFolderTokenChanged;
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            FolderTokensPanel.Children.Add(row);
        }
    }

    private void OnFolderTokenChanged(object? sender, TextChangedEventArgs e)
    {
        if (_building) return;
        if (sender is not TextBox tb || tb.Tag is not FolderTokenNode node) return;
        node.Token = tb.Text?.Trim() ?? "";
        if (SelectedDat() is { } dat) { _building = true; FinalIdField.Text = _session.EffectiveIdFor(dat); _building = false; }
        RefreshIdStatus(); RefreshDatsList(); RefreshValidation();
    }

    private void RefreshComparison()
    {
        if (NewMode) { ComparisonText.Text = ""; return; }
        var dat = SelectedDat(); var l = SelectedLeaf()?.Leaf;
        if (dat is null && l is null) { ComparisonText.Text = "Select a DAT and a leaf to compare."; return; }
        static string L(string s) => (s ?? "").PadRight(32);
        ComparisonText.Text = string.Join('\n', new[]
        {
            $"{L("NEW DAT")}EXISTING LEAF",
            $"{L("name: " + (dat?.HeaderName ?? "—"))}source: {l?.SourceDatName ?? "—"}",
            $"{L("version: " + (dat?.Version ?? "—"))}version: {l?.Version ?? "—"}",
            $"{L("date: " + (dat?.Date ?? "—"))}date: not available",
            $"{L("author: " + (dat?.Author ?? "—"))}author: not available",
            $"{L("releases: " + (dat?.ReleaseCount.ToString() ?? "—"))}releases: {l?.ReleaseCount.ToString() ?? "—"}",
            $"{L("path: " + (dat?.RelativePath ?? "—"))}id: {l?.DatLineId ?? "—"}  media: {l?.MediaTypeId ?? "—"}",
        });
    }

    private void RefreshIdStatus()
    {
        if (_session is null || SelectedDat() is null) { IdStatusText.Text = ""; return; }
        var id = FinalIdField.Text?.Trim() ?? "";
        if (id.Length == 0) { IdStatusText.Text = ""; return; }
        var ev = _session.EvaluateNewLeafId(id);
        // Clear success indicator when valid; the single blocking reason (no duplication) when not.
        IdStatusText.Text = ev.IsValid
            ? (ev.ExceedsRecommendedLength ? "✓ Valid (exceeds 48-char recommendation)" : "✓ Valid")
            : "✗ " + (ev.Reason ?? "invalid id");
        IdStatusText.Foreground = new SolidColorBrush(Color.Parse(ev.IsValid ? "#4CAF50" : "#EF5350"));
    }

    private void RefreshGroupIdStatus()
    {
        if (_session is null) return;
        if (!NewMode) { GroupIdStatusText.Text = "Group ID is fixed for an existing group."; GroupIdStatusText.Foreground = new SolidColorBrush(Color.Parse("#7788AA")); return; }
        var reason = _session.GroupIdBlockingReason();
        GroupIdStatusText.Text = reason ?? "Group ID is available.";
        GroupIdStatusText.Foreground = new SolidColorBrush(Color.Parse(reason is null ? "#4CAF50" : "#EF5350"));
    }

    private void RefreshValidation()
    {
        if (_session is null) return;
        RefreshGroupIdStatus();
        var reasons = _session.BlockingReasons();
        ContinueButton.IsEnabled = reasons.Count == 0;

        if (NewMode)
        {
            // Create: compact proposal summary (never hundreds of individual errors) + issue navigator.
            var s = _session.SummarizeProposals();
            var sb = new System.Text.StringBuilder();
            sb.Append($"{s.Total} leaf proposal(s) — {s.Valid} valid, {s.RequiringAttention} requiring attention.");
            if (s.RequiringAttention > 0)
            {
                var cats = new List<string>();
                if (s.MissingMediaType > 0) cats.Add($"{s.MissingMediaType} missing media type");
                if (s.InvalidFinalId   > 0) cats.Add($"{s.InvalidFinalId} invalid Final ID");
                if (s.DuplicateFinalId > 0) cats.Add($"{s.DuplicateFinalId} duplicate Final ID");
                if (s.CatalogCollision > 0) cats.Add($"{s.CatalogCollision} catalog collision");
                if (cats.Count > 0) sb.Append("  (" + string.Join(", ", cats) + ")");
            }
            // Identity / discovery blockers that are not per-proposal still surface here.
            foreach (var r in reasons.Where(r => !r.Contains("leaf proposal(s) require attention")))
                sb.Append("\n• " + r);

            SummaryText.Text = sb.ToString();
            SummaryText.Foreground = new SolidColorBrush(Color.Parse(reasons.Count == 0 ? "#4CAF50" : "#FFA726"));
            SelectNextIssueButton.IsEnabled = _session.FirstInvalidProposal() is not null;
        }
        else
        {
            SummaryText.Text = reasons.Count == 0
                ? "Ready — all discovered DATs and existing leaves resolved. Plan validated; execution will be enabled in a later phase."
                : "Cannot continue yet:\n• " + string.Join("\n• ", reasons);
            SummaryText.Foreground = new SolidColorBrush(Color.Parse(reasons.Count == 0 ? "#4CAF50" : "#FFA726"));
            SelectNextIssueButton.IsEnabled = false;   // Update mode has no proposal navigator
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;

namespace Arkadia.GroupDats;

/// <summary>
/// Pure, DB-independent working state of the manual Group-DAT reconciliation (the "view-model
/// logic"). It never touches CatalogService, DatLineStore, the filesystem, or any DB — it operates
/// only on the immutable catalog snapshot and the pure Phase-3A discovery result. All consume/undo
/// invariants and completion gating live here, not in the UI. Producing the frozen plan is the only
/// output; nothing is executed.
/// </summary>
public sealed class GroupDatReconciliationSession
{
    public GroupDatReconciliationMode Mode    { get; }
    public GroupDatCatalogPreviewData Catalog { get; }
    public GroupDatExistingGroup?     TargetGroup { get; }

    // ── New-group fields (NewGroup mode) ──────────────────────────────────────
    public string NewGroupId               { get; set; } = "";
    public string NewGroupDisplayName       { get; set; } = "";
    public string NewGroupHardwareFamilyId  { get; set; } = "";
    public string NewGroupAuthority         { get; set; } = "";

    // ── Discovery-derived state ───────────────────────────────────────────────
    public string?                  SourceRoot { get; private set; }
    public DatGroupDiscoveryResult? Discovery  { get; private set; }
    public FolderTokenTree?         FolderTree { get; private set; }

    private readonly List<IncomingDatCandidate>       _incoming = new();
    private readonly List<ExistingGroupLeafCandidate> _leaves   = new();
    private readonly List<GroupDatDecision>           _decisions = new();
    private readonly HashSet<string> _consumedDats  = new(StringComparer.Ordinal);   // CandidateId
    private readonly HashSet<string> _consumedLeaves = new(StringComparer.Ordinal);  // DatLineId

    private GroupDatReconciliationSession(
        GroupDatReconciliationMode mode, GroupDatCatalogPreviewData catalog, GroupDatExistingGroup? group)
    {
        Mode = mode; Catalog = catalog; TargetGroup = group;
        if (group is not null)
            foreach (var leaf in group.Leaves)
                _leaves.Add(new ExistingGroupLeafCandidate(leaf));
    }

    public static GroupDatReconciliationSession ForNewGroup(GroupDatCatalogPreviewData catalog)
        => new(GroupDatReconciliationMode.NewGroup, catalog, null);

    public static GroupDatReconciliationSession ForExistingGroup(
        GroupDatCatalogPreviewData catalog, GroupDatExistingGroup group)
        => new(GroupDatReconciliationMode.UpdateGroup, catalog, group ?? throw new ArgumentNullException(nameof(group)));

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>Applies a discovery result: rebuilds the incoming set and folder tree, clears decisions.</summary>
    public void SetDiscovery(DatGroupDiscoveryResult discovery)
    {
        Discovery  = discovery ?? throw new ArgumentNullException(nameof(discovery));
        SourceRoot = discovery.SourceRoot;

        _incoming.Clear();
        _decisions.Clear();
        _consumedDats.Clear();
        _consumedLeaves.Clear();

        foreach (var leaf in discovery.Leaves.Where(l => l.Status == DiscoveredDatLeafStatus.Parsed))
        {
            var token = DatTechnicalIdPolicy.NormalizeSuggestion(
                System.IO.Path.GetFileNameWithoutExtension(leaf.FileName));
            _incoming.Add(new IncomingDatCandidate(Guid.NewGuid().ToString("N"), leaf, token));
        }

        FolderTree = FolderTokenTree.Build(_incoming.Select(c => c.RelativePath));
    }

    // ── Available sets & decisions ────────────────────────────────────────────

    public IReadOnlyList<IncomingDatCandidate> AvailableIncoming =>
        _incoming.Where(c => !_consumedDats.Contains(c.CandidateId)).ToList();

    public IReadOnlyList<ExistingGroupLeafCandidate> AvailableLeaves =>
        _leaves.Where(l => !_consumedLeaves.Contains(l.DatLineId)).ToList();

    public IReadOnlyList<GroupDatDecision> Decisions => _decisions.ToList();

    // ── Id proposal & collision ───────────────────────────────────────────────

    /// <summary>The proposed (composed) new-leaf id for a candidate, from folder tokens + its DAT token.</summary>
    public string ProposeIdFor(IncomingDatCandidate candidate)
    {
        var groupId = Mode == GroupDatReconciliationMode.NewGroup ? NewGroupId : TargetGroup!.Id;
        var folderTokens = FolderTree?.FolderTokensForFile(candidate.RelativePath) ?? Array.Empty<string>();
        return DatLineIdComposer.Compose(groupId, folderTokens, candidate.DatToken);
    }

    /// <summary>
    /// The effective new-leaf id for a candidate: the manual override when set, otherwise the live
    /// automatic proposal. An auto id recomputes as folder/DAT tokens change; a manual id does not.
    /// </summary>
    public string EffectiveIdFor(IncomingDatCandidate candidate) =>
        candidate.FinalIdOverride ?? ProposeIdFor(candidate);

    /// <summary>Records a user-typed final id as a manual override (stops auto-recompute for it).</summary>
    public void SetManualFinalId(string candidateId, string id)
    {
        var c = _incoming.FirstOrDefault(x => x.CandidateId == candidateId)
            ?? throw new InvalidOperationException("DAT candidate is not part of this session.");
        c.FinalIdOverride = id;
    }

    /// <summary>Clears a manual override so the id tracks the automatic proposal again.</summary>
    public void ClearManualFinalId(string candidateId)
    {
        var c = _incoming.FirstOrDefault(x => x.CandidateId == candidateId)
            ?? throw new InvalidOperationException("DAT candidate is not part of this session.");
        c.FinalIdOverride = null;
    }

    /// <summary>Case-insensitive collision against occupied catalog ids and other new-leaf ids in this session.</summary>
    public bool Collides(string id)
    {
        if (Catalog.OccupiedLeafIds.Contains(id)) return true;
        return _decisions.Any(d => d.Kind == GroupDatDecisionKind.NewLeaf
                                && string.Equals(d.FinalId, id, StringComparison.OrdinalIgnoreCase));
    }

    public DatLineIdEvaluation EvaluateNewLeafId(string id) => DatLineIdComposer.Evaluate(id, Collides);

    // ── Decisions ─────────────────────────────────────────────────────────────

    /// <summary>Associate a discovered DAT with an existing leaf (update). Consumes both.</summary>
    public GroupDatDecision AssociateUpdate(string datCandidateId, string leafDatLineId)
    {
        var dat  = RequireAvailableDat(datCandidateId);
        var leaf = RequireAvailableLeaf(leafDatLineId);
        var decision = new GroupDatDecision(GroupDatDecisionKind.Update, dat, leaf, null, leaf.Leaf.MediaTypeId);
        _decisions.Add(decision);
        _consumedDats.Add(dat.CandidateId);
        _consumedLeaves.Add(leaf.DatLineId);
        return decision;
    }

    /// <summary>Create a new leaf from a discovered DAT with a final id and media type. Consumes only the DAT.</summary>
    public GroupDatDecision CreateNewLeaf(string datCandidateId, string finalId, string mediaTypeId)
    {
        var dat = RequireAvailableDat(datCandidateId);
        var eval = EvaluateNewLeafId(finalId);
        if (!eval.IsValid)
            throw new InvalidOperationException($"Invalid new leaf id '{finalId}': {eval.Reason}");
        if (string.IsNullOrWhiteSpace(mediaTypeId) ||
            (!Catalog.MediaTypes.IsEmpty && !Catalog.MediaTypes.Any(m => m.Id == mediaTypeId)))
            throw new InvalidOperationException("A valid media type must be selected for a new leaf.");

        var decision = new GroupDatDecision(GroupDatDecisionKind.NewLeaf, dat, null, finalId, mediaTypeId);
        _decisions.Add(decision);
        _consumedDats.Add(dat.CandidateId);
        return decision;
    }

    /// <summary>Mark an existing leaf absent from the new revision (retained, never deleted). Consumes the leaf.</summary>
    public GroupDatDecision MarkLeafAbsent(string leafDatLineId)
    {
        var leaf = RequireAvailableLeaf(leafDatLineId);
        var decision = new GroupDatDecision(GroupDatDecisionKind.Absent, null, leaf, null, null);
        _decisions.Add(decision);
        _consumedLeaves.Add(leaf.DatLineId);
        return decision;
    }

    /// <summary>Remove a decision and return its items to the available sets.</summary>
    public void Undo(string decisionId)
    {
        var decision = _decisions.FirstOrDefault(d => d.DecisionId == decisionId)
            ?? throw new InvalidOperationException($"No decision '{decisionId}'.");
        _decisions.Remove(decision);
        if (decision.Dat is not null)  _consumedDats.Remove(decision.Dat.CandidateId);
        if (decision.Leaf is not null) _consumedLeaves.Remove(decision.Leaf.DatLineId);
    }

    private IncomingDatCandidate RequireAvailableDat(string candidateId)
    {
        var dat = _incoming.FirstOrDefault(c => c.CandidateId == candidateId)
            ?? throw new InvalidOperationException("DAT candidate is not part of this session.");
        if (_consumedDats.Contains(candidateId))
            throw new InvalidOperationException("DAT candidate is already resolved.");
        return dat;
    }

    private ExistingGroupLeafCandidate RequireAvailableLeaf(string datLineId)
    {
        var leaf = _leaves.FirstOrDefault(l => l.DatLineId == datLineId)
            ?? throw new InvalidOperationException("Leaf is not part of this session.");
        if (_consumedLeaves.Contains(datLineId))
            throw new InvalidOperationException("Leaf is already resolved.");
        return leaf;
    }

    // ── Completion gating ─────────────────────────────────────────────────────

    /// <summary>Human-readable list of reasons the plan cannot yet be produced (empty = ready).</summary>
    public IReadOnlyList<string> BlockingReasons()
    {
        var reasons = new List<string>();

        if (Discovery is null) { reasons.Add("No source folder selected."); return reasons; }
        if (Discovery.HasBlockingErrors) reasons.Add("Discovery has blocking errors (parse failures / path collisions).");
        if (Discovery.CandidateCount == 0) reasons.Add("No DAT files found under the source.");

        var incoming = AvailableIncoming.Count;
        var leaves   = AvailableLeaves.Count;
        if (incoming > 0) reasons.Add($"{incoming} discovered DAT(s) not yet resolved.");
        if (leaves   > 0) reasons.Add($"{leaves} existing leaf(s) not yet resolved.");

        if (Mode == GroupDatReconciliationMode.NewGroup)
        {
            if (!DatGroupId.TryCreateNew(NewGroupId, out _, out _, out _))
                reasons.Add("Group id is invalid.");
            else if (Catalog.ExistingGroups.Any(g => string.Equals(g.Id, NewGroupId, StringComparison.OrdinalIgnoreCase)))
                reasons.Add("Group id already exists.");
            if (string.IsNullOrWhiteSpace(NewGroupDisplayName)) reasons.Add("Group display name is required.");
            if (string.IsNullOrWhiteSpace(NewGroupHardwareFamilyId) ||
                (!Catalog.HardwareFamilies.IsEmpty && !Catalog.HardwareFamilies.Any(h => h.Id == NewGroupHardwareFamilyId)))
                reasons.Add("A valid hardware family is required.");
            if (string.IsNullOrWhiteSpace(NewGroupAuthority)) reasons.Add("An authority is required.");
        }

        return reasons;
    }

    public bool CanBuildPlan => BlockingReasons().Count == 0;

    // ── Frozen plan ───────────────────────────────────────────────────────────

    /// <summary>Builds the immutable plan. Throws if <see cref="CanBuildPlan"/> is false.</summary>
    public GroupDatReconciliationPlan BuildPlan()
    {
        var reasons = BlockingReasons();
        if (reasons.Count > 0)
            throw new InvalidOperationException("Reconciliation is not complete: " + string.Join("; ", reasons));

        var updates = _decisions.Where(d => d.Kind == GroupDatDecisionKind.Update)
            .Select(d => new GroupDatUpdateActionPlan(
                d.Leaf!.DatLineId, d.Dat!.RelativePath, d.Dat.SourcePath, d.Dat.HeaderName, d.Dat.ReleaseCount))
            .OrderBy(u => u.ExistingLeafId, StringComparer.Ordinal)
            .ToImmutableArray();

        var newLeaves = _decisions.Where(d => d.Kind == GroupDatDecisionKind.NewLeaf)
            .Select(d => new GroupDatNewLeafPlan(
                d.FinalId!, d.MediaTypeId!, d.Dat!.RelativePath, d.Dat.SourcePath,
                d.Dat.HeaderName, d.Dat.Version, d.Dat.ReleaseCount))
            .OrderBy(n => n.LeafId, StringComparer.Ordinal)
            .ToImmutableArray();

        var absent = _decisions.Where(d => d.Kind == GroupDatDecisionKind.Absent)
            .Select(d => new GroupDatAbsentLeafPlan(d.Leaf!.DatLineId))
            .OrderBy(a => a.ExistingLeafId, StringComparer.Ordinal)
            .ToImmutableArray();

        return new GroupDatReconciliationPlan(
            Mode,
            Discovery!.SourceRoot,
            Mode == GroupDatReconciliationMode.NewGroup ? NewGroupId : null,
            Mode == GroupDatReconciliationMode.NewGroup ? NewGroupDisplayName : null,
            Mode == GroupDatReconciliationMode.NewGroup ? NewGroupHardwareFamilyId : null,
            Mode == GroupDatReconciliationMode.NewGroup ? NewGroupAuthority : null,
            Mode == GroupDatReconciliationMode.UpdateGroup ? TargetGroup!.Id : null,
            updates, newLeaves, absent,
            Discovery.Leaves.ToImmutableArray());
    }
}

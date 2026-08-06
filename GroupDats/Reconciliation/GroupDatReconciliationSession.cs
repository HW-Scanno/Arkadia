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
/// only on the immutable catalog snapshot and the pure Phase-3A discovery result.
///
/// <para><b>Identity model (explicit, no longer inferred).</b> The <b>mode is fixed by the caller</b>
/// via <see cref="ForNewGroup"/> / <see cref="ForExistingGroup"/> — it is never derived from
/// System + authority. <b>System id</b> is caller-supplied and immutable. <b>Group Name</b>
/// (persists as <c>display_name</c>) and <b>Group ID</b> (the stable technical key and leaf-id
/// prefix) are two distinct fields. In Create mode Authority / Group Name / Group ID are editable
/// (Group ID validated + case-insensitively collision-checked before creation); in Update mode all
/// identity comes from the catalog snapshot and is read-only. Multiple groups per System and per
/// authority are allowed.</para>
///
/// All consume/undo invariants and completion gating live here, not in the UI. Producing the frozen
/// plan is the only output; nothing is executed.
/// </summary>
public sealed class GroupDatReconciliationSession
{
    public GroupDatReconciliationMode Mode    { get; }
    public GroupDatCatalogPreviewData Catalog { get; }
    public GroupDatExistingGroup?     TargetGroup { get; }

    // ── System context (from the caller; never editable in the window) ──────────
    public string SystemId         { get; }
    public string SystemName       { get; }
    public string HardwareFamilyId { get; }

    /// <summary>System manufacturer (from the caller) — a descriptive input for the Group Name suggestion only.</summary>
    public string Manufacturer { get; }

    // ── Group identity ──────────────────────────────────────────────────────────
    private string  _authority;
    private string  _groupName;
    private string  _groupId;
    private bool    _groupNameManual;
    private bool    _groupIdManual;

    /// <summary>Group authority (metadata). Editable in Create mode, read-only in Update mode.</summary>
    public string Authority => _authority;
    /// <summary>Human-readable Group Name shown in the System view (persists as display_name).</summary>
    public string GroupName => _groupName;
    /// <summary>Stable technical Group ID (the leaf-id prefix). Immutable once the group exists.</summary>
    public string GroupId   => _groupId;

    /// <summary>True when identity is fixed by an existing group (Update mode).</summary>
    public bool IsIdentityLocked => Mode == GroupDatReconciliationMode.UpdateGroup;

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
        GroupDatReconciliationMode mode, GroupDatCatalogPreviewData catalog,
        string systemId, string systemName, string hardwareFamilyId, string manufacturer,
        string authority, string groupName, string groupId, GroupDatExistingGroup? targetGroup)
    {
        Mode             = mode;
        Catalog          = catalog          ?? throw new ArgumentNullException(nameof(catalog));
        SystemId         = systemId         ?? throw new ArgumentNullException(nameof(systemId));
        SystemName       = string.IsNullOrWhiteSpace(systemName) ? systemId : systemName;
        HardwareFamilyId = hardwareFamilyId ?? systemId;
        Manufacturer     = manufacturer ?? "";
        _authority       = authority ?? "";
        _groupName       = groupName ?? "";
        _groupId         = groupId   ?? "";
        TargetGroup      = targetGroup;

        if (targetGroup is not null)
            foreach (var leaf in targetGroup.Leaves)
                _leaves.Add(new ExistingGroupLeafCandidate(leaf));
    }

    /// <summary>
    /// Create mode: a brand-new group under the given System context. The caller supplies the initial
    /// suggestions (<paramref name="groupName"/> from the authority display name, <paramref name="proposedGroupId"/>
    /// = <see cref="SuggestGroupId"/>); all three of Authority / Group Name / Group ID remain editable
    /// until the group is created.
    /// </summary>
    public static GroupDatReconciliationSession ForNewGroup(
        GroupDatCatalogPreviewData catalog, string systemId, string systemName,
        string authority, string groupName, string proposedGroupId, string manufacturer = "")
        => new(GroupDatReconciliationMode.NewGroup, catalog, systemId, systemName,
               hardwareFamilyId: systemId, manufacturer, authority, groupName, proposedGroupId, targetGroup: null);

    /// <summary>
    /// Update mode: bound to one specific already-existing Group ID. System / Authority / Group Name /
    /// Group ID and the existing leaves are all loaded from the catalog snapshot and are read-only —
    /// nothing here is taken from alternative UI values.
    /// </summary>
    public static GroupDatReconciliationSession ForExistingGroup(
        GroupDatCatalogPreviewData catalog, string existingGroupId)
    {
        var group = catalog.ExistingGroups.FirstOrDefault(
                        g => string.Equals(g.Id, existingGroupId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No existing group '{existingGroupId}' in the catalog snapshot.");

        var systemName = catalog.HardwareFamilies
            .FirstOrDefault(h => string.Equals(h.Id, group.HardwareFamilyId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? group.HardwareFamilyId;

        return new(GroupDatReconciliationMode.UpdateGroup, catalog,
                   systemId: group.HardwareFamilyId, systemName, hardwareFamilyId: group.HardwareFamilyId,
                   manufacturer: "", group.Authority, group.DisplayName, group.Id, targetGroup: group);
    }

    /// <summary>The initial Group ID suggestion for a new group: <c>&lt;systemId&gt;-&lt;authority&gt;</c>.</summary>
    public static string SuggestGroupId(string systemId, string authority) => $"{systemId}-{authority}";

    /// <summary>
    /// Best-effort Group Name from the descriptive System fields + authority display name:
    /// <c>&lt;Manufacturer&gt; &lt;Model&gt; &lt;Authority&gt;</c>, joined by single spaces with whitespace
    /// normalized. Empty components are dropped (so the fallbacks are Manufacturer+Model+Authority →
    /// Model+Authority → Authority). When the Model already starts with the Manufacturer
    /// (case-insensitive) the Manufacturer is not repeated (avoids "Commodore Commodore 64"). No generic
    /// words (DAT/Group/Collection) are ever added.
    /// </summary>
    public static string ComposeGroupName(string? manufacturer, string? model, string? authorityDisplayName)
    {
        var mfr  = (manufacturer ?? "").Trim();
        var mdl  = (model ?? "").Trim();
        var auth = (authorityDisplayName ?? "").Trim();

        if (mfr.Length > 0 && mdl.Length > 0 && mdl.StartsWith(mfr, StringComparison.OrdinalIgnoreCase))
            mfr = "";   // model already carries the manufacturer prefix

        var joined = string.Join(" ", new[] { mfr, mdl, auth }.Where(p => p.Length > 0));
        return System.Text.RegularExpressions.Regex.Replace(joined, @"\s+", " ").Trim();
    }

    /// <summary>
    /// The initial Group Name suggestion for a new group, composed from the caller's Manufacturer +
    /// Model (<see cref="SystemName"/>) and the authority's display name via <see cref="ComposeGroupName"/>.
    /// </summary>
    public string SuggestGroupName(string authority)
    {
        var authDisplay = Catalog.Authorities
            .FirstOrDefault(a => string.Equals(a.Id, authority, StringComparison.OrdinalIgnoreCase))?.Name
            ?? authority;
        return ComposeGroupName(Manufacturer, SystemName, authDisplay);
    }

    // ── Create-mode identity edits ──────────────────────────────────────────────

    private void RequireCreatable()
    {
        if (Mode != GroupDatReconciliationMode.NewGroup)
            throw new InvalidOperationException("Group identity is read-only for an existing group.");
    }

    /// <summary>Sets the authority (Create only); re-suggests Group ID / Group Name when not manually overridden.</summary>
    public void SetAuthority(string authority)
    {
        RequireCreatable();
        _authority = authority ?? "";
        if (!_groupIdManual)   _groupId   = SuggestGroupId(SystemId, _authority);
        if (!_groupNameManual) _groupName = SuggestGroupName(_authority);
    }

    /// <summary>Sets the Group Name (Create only); marks it manually overridden.</summary>
    public void SetGroupName(string groupName)
    {
        RequireCreatable();
        _groupName = groupName ?? "";
        _groupNameManual = true;
    }

    /// <summary>Sets the Group ID (Create only); marks it manual so authority changes stop re-suggesting it.</summary>
    public void SetGroupId(string groupId)
    {
        RequireCreatable();
        _groupId = groupId ?? "";
        _groupIdManual = true;
    }

    /// <summary>True when the Group Name currently carries a manual override.</summary>
    public bool IsGroupNameManual => _groupNameManual;
    /// <summary>True when the Group ID currently carries a manual override.</summary>
    public bool IsGroupIdManual   => _groupIdManual;
    /// <summary>True when either identity field has a manual override (i.e. no longer auto-managed).</summary>
    public bool IsIdentityCustom  => _groupNameManual || _groupIdManual;

    /// <summary>
    /// True when identity may still be reset to the suggestion — Create mode and before any leaf
    /// decision has locked the Group ID prefix.
    /// </summary>
    public bool CanResetIdentity => Mode == GroupDatReconciliationMode.NewGroup && _decisions.Count == 0;

    /// <summary>
    /// Re-applies the automatic Group Name + Group ID suggestions from the current System + Authority,
    /// discarding any manual overrides and returning both fields to auto-managed. Create mode only.
    /// Leaf Final-ID overrides live on the candidates and are unaffected.
    /// </summary>
    public void ResetIdentityToSuggested()
    {
        RequireCreatable();
        _groupNameManual = false;
        _groupIdManual   = false;
        _groupId   = SuggestGroupId(SystemId, _authority);
        _groupName = SuggestGroupName(_authority);
    }

    /// <summary>True when the Group ID is a valid, collision-free new id (Create-mode gate).</summary>
    public string? GroupIdBlockingReason()
    {
        if (string.IsNullOrWhiteSpace(_groupId)) return "Group id is required.";
        if (!DatGroupId.TryCreateNew(_groupId, out _, out _, out _)) return $"Group id '{_groupId}' is invalid.";
        if (GroupIdCollides(_groupId)) return $"Group id '{_groupId}' already exists — choose a different id.";
        return null;
    }

    /// <summary>Case-insensitive collision of a candidate Group ID against existing group ids.</summary>
    public bool GroupIdCollides(string id) =>
        Catalog.ExistingGroups.Any(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

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
            // DAT suffix starts EMPTY — ids are proposed from the folder hierarchy, not the (long
            // TOSEC) filename. A suffix is only added manually to disambiguate.
            _incoming.Add(new IncomingDatCandidate(Guid.NewGuid().ToString("N"), leaf, datToken: ""));
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

    /// <summary>
    /// The proposed new-leaf id: <c>&lt;group-id&gt;-&lt;folder tokens&gt;-&lt;optional DAT suffix&gt;</c>.
    /// The Group ID is the prefix; media type, hardware family, filename, TOSEC version/date, hashes,
    /// and random suffixes are never added automatically.
    /// </summary>
    public string ProposeIdFor(IncomingDatCandidate candidate)
    {
        var folderTokens = FolderTree?.FolderTokensForFile(candidate.RelativePath) ?? Array.Empty<string>();
        return DatLineIdComposer.Compose(GroupId, folderTokens, candidate.DatToken);
    }

    /// <summary>
    /// The effective new-leaf id for a candidate: the manual override when set, otherwise the live
    /// automatic proposal. An auto id recomputes as the Group ID / folder / DAT tokens change; a
    /// manual id does not.
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

    // ── Create-mode implicit leaf proposals ─────────────────────────────────────
    // In Create mode every discovered DAT is implicitly a new-leaf proposal — editable in memory,
    // never a persisted decision, never consumed. There is no per-DAT "create leaf" step.

    /// <summary>Every discovered DAT as an implicit new-leaf proposal (Create mode; empty in Update).</summary>
    public IReadOnlyList<IncomingDatCandidate> Proposals =>
        Mode == GroupDatReconciliationMode.NewGroup
            ? _incoming.ToList()
            : (IReadOnlyList<IncomingDatCandidate>)Array.Empty<IncomingDatCandidate>();

    /// <summary>Why a single Create-mode proposal is not yet valid (same contracts as single-leaf creation).</summary>
    public enum LeafProposalIssue { Valid, MissingMediaType, InvalidFinalId, DuplicateFinalId, CatalogCollision }

    /// <summary>Case-insensitive count of each effective proposal id (for duplicate detection / rendering).</summary>
    public IReadOnlyDictionary<string, int> ProposalIdCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _incoming)
        {
            var id = EffectiveIdFor(c);
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        return counts;
    }

    /// <summary>Validation state of one proposal. Reuses the same id policy + collision rules as leaf creation.</summary>
    public LeafProposalIssue EvaluateProposal(
        IncomingDatCandidate candidate, IReadOnlyDictionary<string, int>? idCounts = null)
    {
        if (string.IsNullOrWhiteSpace(candidate.DraftMediaTypeId) ||
            (!Catalog.MediaTypes.IsEmpty && !Catalog.MediaTypes.Any(m => m.Id == candidate.DraftMediaTypeId)))
            return LeafProposalIssue.MissingMediaType;

        var id = EffectiveIdFor(candidate);
        if (!DatTechnicalIdPolicy.IsValidNew(id))  return LeafProposalIssue.InvalidFinalId;
        if (Catalog.OccupiedLeafIds.Contains(id))  return LeafProposalIssue.CatalogCollision;

        var counts = idCounts ?? ProposalIdCounts();
        if (counts.TryGetValue(id, out var n) && n > 1) return LeafProposalIssue.DuplicateFinalId;

        return LeafProposalIssue.Valid;
    }

    /// <summary>Aggregate validation of all Create-mode proposals, categorized for a compact summary.</summary>
    public sealed record LeafProposalSummary(
        int Total, int Valid, int MissingMediaType, int InvalidFinalId,
        int DuplicateFinalId, int CatalogCollision)
    {
        public int RequiringAttention => Total - Valid;
    }

    public LeafProposalSummary SummarizeProposals()
    {
        var counts = ProposalIdCounts();
        int total = 0, valid = 0, mm = 0, inv = 0, dup = 0, col = 0;
        foreach (var c in _incoming)
        {
            total++;
            switch (EvaluateProposal(c, counts))
            {
                case LeafProposalIssue.Valid:            valid++; break;
                case LeafProposalIssue.MissingMediaType: mm++;    break;
                case LeafProposalIssue.InvalidFinalId:   inv++;   break;
                case LeafProposalIssue.DuplicateFinalId: dup++;   break;
                case LeafProposalIssue.CatalogCollision: col++;   break;
            }
        }
        return new LeafProposalSummary(total, valid, mm, inv, dup, col);
    }

    /// <summary>The first proposal (discovery order) that is not valid, or null when all are valid.</summary>
    public IncomingDatCandidate? FirstInvalidProposal()
    {
        var counts = ProposalIdCounts();
        return _incoming.FirstOrDefault(c => EvaluateProposal(c, counts) != LeafProposalIssue.Valid);
    }

    /// <summary>
    /// Resets one proposal to its automatic suggestion: empty Dat Suffix, automatic Final ID, and
    /// (optionally) the last bulk-default media type. Folder tokens are shared across siblings and are
    /// intentionally not altered here. Create mode only.
    /// </summary>
    public void ResetProposal(string candidateId, string? defaultMediaTypeId = null)
    {
        RequireCreatable();
        var c = _incoming.FirstOrDefault(x => x.CandidateId == candidateId)
            ?? throw new InvalidOperationException("DAT candidate is not part of this session.");
        c.DatToken          = "";
        c.FinalIdOverride   = null;
        c.IsMediaTypeManual = false;
        if (!string.IsNullOrWhiteSpace(defaultMediaTypeId)) c.DraftMediaTypeId = defaultMediaTypeId;
    }

    /// <summary>
    /// Records the media type chosen for one not-yet-confirmed candidate as a <b>manual override</b>
    /// (a later "Apply to remaining DATs" default will not overwrite it). Scratch state only — it
    /// becomes a decision only when <see cref="CreateNewLeaf"/> is pressed.
    /// </summary>
    public void SetDraftMediaType(string candidateId, string mediaTypeId)
    {
        var c = _incoming.FirstOrDefault(x => x.CandidateId == candidateId)
            ?? throw new InvalidOperationException("DAT candidate is not part of this session.");
        c.DraftMediaTypeId  = mediaTypeId;
        c.IsMediaTypeManual = true;
    }

    /// <summary>
    /// Propagates <paramref name="mediaTypeId"/> (the media type shown for the currently-selected leaf)
    /// onto every OTHER <i>unresolved</i> incoming candidate that has no manual override, leaving
    /// manual overrides and already-confirmed decisions untouched. The source candidate
    /// (<paramref name="sourceCandidateId"/>) is excluded entirely — it already shows the value and is
    /// counted as neither updated nor preserved. This only sets scratch state — it creates no decision,
    /// consumes no candidate, selects no leaf, validates no plan, and does not enable Continue. Returns
    /// the count of other candidates updated and the count of other manual overrides preserved.
    /// </summary>
    public (int Updated, int PreservedManual) ApplyDefaultMediaTypeToUnresolved(
        string mediaTypeId, string? sourceCandidateId = null)
    {
        if (string.IsNullOrWhiteSpace(mediaTypeId) ||
            (!Catalog.MediaTypes.IsEmpty && !Catalog.MediaTypes.Any(m => m.Id == mediaTypeId)))
            throw new InvalidOperationException("A valid media type must be selected.");

        int updated = 0, preserved = 0;
        foreach (var c in _incoming)
        {
            if (_consumedDats.Contains(c.CandidateId)) continue;         // resolved ⇒ never touched
            if (c.CandidateId == sourceCandidateId) continue;           // the source leaf ⇒ not counted
            if (c.IsMediaTypeManual) { preserved++; continue; }         // manual override ⇒ preserved
            c.DraftMediaTypeId = mediaTypeId;                            // propagated value (stays non-manual)
            updated++;
        }
        return (updated, preserved);
    }

    /// <summary>Case-insensitive collision against occupied catalog leaf ids and other new-leaf ids in this session.</summary>
    public bool Collides(string id)
    {
        if (Catalog.OccupiedLeafIds.Contains(id)) return true;
        return _decisions.Any(d => d.Kind == GroupDatDecisionKind.NewLeaf
                                && string.Equals(d.FinalId, id, StringComparison.OrdinalIgnoreCase));
    }

    public DatLineIdEvaluation EvaluateNewLeafId(string id) => DatLineIdComposer.Evaluate(id, Collides);

    // ── Decisions (sequential: one DAT at a time in BOTH modes) ─────────────────

    private void RequireUpdateMode()
    {
        if (Mode == GroupDatReconciliationMode.NewGroup)
            throw new InvalidOperationException("Associate/absent apply only when updating an existing group.");
    }

    /// <summary>Associate a discovered DAT with an existing leaf (update). Consumes both. Update mode only.</summary>
    public GroupDatDecision AssociateUpdate(string datCandidateId, string leafDatLineId)
    {
        RequireUpdateMode();
        var dat  = RequireAvailableDat(datCandidateId);
        var leaf = RequireAvailableLeaf(leafDatLineId);
        var decision = new GroupDatDecision(GroupDatDecisionKind.Update, dat, leaf, null, leaf.Leaf.MediaTypeId);
        _decisions.Add(decision);
        _consumedDats.Add(dat.CandidateId);
        _consumedLeaves.Add(leaf.DatLineId);
        return decision;
    }

    /// <summary>
    /// Create a new leaf from a discovered DAT with a final id and media type. Consumes only the DAT.
    /// <b>Update mode only</b> — in Create mode every discovered DAT is already an implicit new-leaf
    /// proposal (see <see cref="Proposals"/>), so there is no per-DAT create decision. New leaves are
    /// prefixed by the existing Group ID.
    /// </summary>
    public GroupDatDecision CreateNewLeaf(string datCandidateId, string finalId, string mediaTypeId)
    {
        RequireUpdateMode();
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
        RequireUpdateMode();
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

        // A restored new-leaf candidate keeps the media type of the undone decision, treated as a
        // preserved manual choice (a later default apply will not overwrite it).
        if (decision.Kind == GroupDatDecisionKind.NewLeaf && decision.Dat is not null)
        {
            decision.Dat.DraftMediaTypeId  = decision.MediaTypeId;
            decision.Dat.IsMediaTypeManual = true;
        }
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

        if (Mode == GroupDatReconciliationMode.NewGroup)
        {
            // Create mode requires an explicit, valid Group Name and Group ID.
            if (string.IsNullOrWhiteSpace(_groupName)) reasons.Add("Group name is required.");
            if (GroupIdBlockingReason() is { } gidReason) reasons.Add(gidReason);

            // Every discovered DAT is an implicit proposal — all must be valid (no per-DAT step).
            var summary = SummarizeProposals();
            if (summary.Total == 0) reasons.Add("No leaf proposals.");
            else if (summary.RequiringAttention > 0)
                reasons.Add($"{summary.RequiringAttention} leaf proposal(s) require attention.");
        }
        else
        {
            var incoming = AvailableIncoming.Count;
            var leaves   = AvailableLeaves.Count;
            if (incoming > 0) reasons.Add($"{incoming} discovered DAT(s) not yet resolved.");
            if (leaves   > 0) reasons.Add($"{leaves} existing leaf(s) not yet resolved.");
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

        // New leaves: Create mode ⇒ one per implicit proposal (every discovered DAT); Update mode ⇒
        // only the explicit new-leaf decisions.
        var newLeaves = (Mode == GroupDatReconciliationMode.NewGroup
                ? _incoming.Select(c => new GroupDatNewLeafPlan(
                    EffectiveIdFor(c), c.DraftMediaTypeId!, c.RelativePath, c.SourcePath,
                    c.HeaderName, c.Version, c.ReleaseCount))
                : _decisions.Where(d => d.Kind == GroupDatDecisionKind.NewLeaf)
                    .Select(d => new GroupDatNewLeafPlan(
                        d.FinalId!, d.MediaTypeId!, d.Dat!.RelativePath, d.Dat.SourcePath,
                        d.Dat.HeaderName, d.Dat.Version, d.Dat.ReleaseCount)))
            .OrderBy(n => n.LeafId, StringComparer.Ordinal)
            .ToImmutableArray();

        var absent = _decisions.Where(d => d.Kind == GroupDatDecisionKind.Absent)
            .Select(d => new GroupDatAbsentLeafPlan(d.Leaf!.DatLineId))
            .OrderBy(a => a.ExistingLeafId, StringComparer.Ordinal)
            .ToImmutableArray();

        return new GroupDatReconciliationPlan(
            Mode,
            Discovery!.SourceRoot,
            SystemId, SystemName, Authority,
            GroupId, GroupName, HardwareFamilyId,
            updates, newLeaves, absent,
            Discovery.Leaves.ToImmutableArray());
    }
}

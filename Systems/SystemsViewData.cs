using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Systems;

/// <summary>What one targeted Group update touched: the Group card and, when its System exists, that System's row.</summary>
public sealed record SystemsGroupUpdate(string GroupId, string? PlatformId);

/// <summary>
/// The data behind the Systems view: one <see cref="SystemPlatform"/> per System and one
/// <see cref="GroupDatCardInfo"/> per Group DAT, plus the Group membership maps the view needs.
///
/// <para><b>One calculation, three scopes.</b> <see cref="Load"/> is the complete refresh (every System, every
/// Single DAT, every Group card). The two targeted updates reuse the very same per-System and per-Group functions on
/// a narrower scope, so they cannot drift from the complete refresh:</para>
/// <list type="bullet">
///   <item><see cref="TryApplyGroupCoverage"/> — a Group's coverage state changed in the
///   <see cref="GroupCoverageQueue"/> (computed, failed, invalidated). Nothing else changed, so its card and its
///   System's row are recomposed from the facts already gathered: no catalog query, no leaf database, no filesystem.</item>
///   <item><see cref="TryRefreshPlatform"/> — something changed inside ONE System (a release status, an artifact, an
///   assignment, staging). Everything that System shows is gathered again exactly as the complete refresh gathers it;
///   every other System keeps its row.</item>
/// </list>
///
/// <para><b>Assignments.</b> The complete volume-artifact assignment map is never loaded here. Only Single DATs need
/// their assigned set on this path (Group leaves get theirs inside the off-thread coverage calculation), so the
/// refresh asks the catalog for exactly those dat lines (<see cref="CatalogService.GetAssignedDerivedIdsByDatLines"/>).</para>
///
/// <para>The gathered facts are not a cache with a life of its own: they are replaced by every complete refresh,
/// exactly like the rows the view already keeps on screen between two refreshes, and every existing workflow that
/// refreshes Systems still performs the complete refresh. No Avalonia, no threading — all calls are made on the UI
/// thread by MainWindow, and the class opens Group-leaf databases in none of its paths.</para>
/// </summary>
public sealed class SystemsViewData
{
    /// <summary>The leaf-database + catalog + filesystem half of one System row (its Group half comes from the coverage queue).</summary>
    private sealed record PlatformFacts(
        HardwareFamilyRecord            Platform,
        IReadOnlyCollection<string>     DatLineIds,
        int                             TotalTitles,
        int                             Present,
        int                             Outdated,
        int                             Unwanted,
        int                             Lost,
        IReadOnlyList<ArchiveLifecycle> SingleLifecycles,
        bool                            HasStaging);

    /// <summary>The catalog + filesystem half of one Group card (its leaf-database half comes from the coverage queue).</summary>
    private sealed record GroupFacts(
        string                      GroupId,
        GroupMeta                   Meta,
        IReadOnlyCollection<string> LeafIds,
        int                         ReleaseSum,
        bool                        AnyLocalVolume,
        bool                        HasStaging);

    private readonly Dictionary<string, PlatformFacts> _platformFacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupFacts>    _groupFacts    = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string>                      _groupOrder    = [];   // the catalog's Group order

    private SystemsViewData() { }

    // ── What the view reads ───────────────────────────────────────────────────

    /// <summary>id → display name maps, read by the complete refresh.</summary>
    public Dictionary<string, string> HardwareTypeNames { get; private set; } = [];
    public Dictionary<string, string> StrategyNames     { get; private set; } = [];
    public Dictionary<string, string> AuthorityNames    { get; private set; } = [];

    /// <summary>One row per System, in the catalog's order. Targeted updates replace a row at its own index.</summary>
    public List<SystemPlatform> Platforms { get; } = [];

    /// <summary>One card per Group DAT, in the catalog's order. Targeted updates replace a card at its own index.</summary>
    public List<GroupDatCardInfo> GroupCards { get; } = [];

    /// <summary>Group-leaf dat_line id → owning Group id.</summary>
    public Dictionary<string, string> LeafToGroup { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Group id → its leaves' database locations, for the lazy coverage calculation. A list, once published, is never
    /// mutated (a refresh swaps in a new one), so the off-thread calculation can keep reading the one it was given.
    /// </summary>
    public Dictionary<string, List<(string DatLineId, string DataStorePath, int ReleaseCount)>> GroupLeafDbInfo { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    // ── Complete refresh ──────────────────────────────────────────────────────

    /// <summary>
    /// The complete Systems refresh. Opens every existing Single-DAT leaf database once (status counts + present
    /// derived-artifact assignment stats in the same open) and NO Group-leaf database: Group figures come from
    /// <paramref name="coverage"/> when already computed, otherwise the Group — and the System it belongs to — is
    /// reported pending (or failed), never as a partial figure.
    /// </summary>
    public static SystemsViewData Load(CatalogService catalog, string dataDir, string appRoot, GroupCoverageQueue coverage)
    {
        var view = new SystemsViewData
        {
            HardwareTypeNames = catalog.LoadHardwareTypes().ToDictionary(h => h.Id, h => h.Name),
            StrategyNames     = catalog.LoadStorageStrategies().ToDictionary(s => s.Id, s => s.Name),
            AuthorityNames    = catalog.LoadAuthorities().ToDictionary(a => a.Id, a => a.Name),
        };

        var allDatLines = catalog.LoadDatLines();

        // Group membership (read-only, catalog-only — NO leaf DB opens). Group leaves stay real dat_lines; they are
        // hidden from the main list and rolled up into one Group card.
        var groupLeaves = new Dictionary<string, List<GroupLeafRecord>>(StringComparer.OrdinalIgnoreCase);
        var groups      = new Dictionary<string, DatGroupRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in catalog.LoadDatGroups())
        {
            var gid = g.Id.Value;
            if (!groups.ContainsKey(gid)) view._groupOrder.Add(gid);
            groups[gid] = g;
            var leaves = catalog.GetLeavesForGroup(gid);
            groupLeaves[gid] = leaves;
            foreach (var leaf in leaves) view.LeafToGroup[leaf.DatLine.Id] = gid;
        }

        // Storage-lifecycle inputs that need NO leaf DB open: LOCAL-workspace volume owners (catalog) and the assigned
        // artifacts of the Single DATs (catalog, targeted — never the whole assignment table).
        var owners   = LocalOwners.Read(catalog);
        var assigned = catalog.GetAssignedDerivedIdsByDatLines(
            allDatLines.Where(dl => !view.LeafToGroup.ContainsKey(dl.Id)).Select(dl => dl.Id));

        // One live staging probe per System, reused for the System row and its Group cards (STAGING is a filesystem flag).
        var platforms = catalog.LoadPlatforms();
        var staging   = platforms.Select(p => p.Id).Distinct(StringComparer.Ordinal).ToDictionary(
            pid => pid, pid => StagingSnapshot.LeafIdsWithStaging(appRoot, pid), StringComparer.Ordinal);
        HashSet<string> StagingFor(string pid) => staging.TryGetValue(pid, out var set) ? set : NoIds;

        foreach (var gid in view._groupOrder)
        {
            var g = groups[gid];
            view.SetGroup(view.GatherGroup(gid, g, groupLeaves[gid], owners, StagingFor(g.HardwareFamilyId)), groupLeaves[gid]);
        }

        foreach (var platform in platforms)
        {
            var platformDatLines = allDatLines.Where(dl => dl.HardwareFamilyId == platform.Id).ToList();
            view._platformFacts[platform.Id] = GatherPlatform(
                platform, platformDatLines, view.LeafToGroup, assigned, owners, StagingFor(platform.Id), dataDir);
        }

        foreach (var platform in platforms)
            view.Platforms.Add(view.ComposePlatform(view._platformFacts[platform.Id], coverage));
        foreach (var gid in view._groupOrder)
            view.GroupCards.Add(ComposeGroupCard(view._groupFacts[gid], coverage));

        return view;
    }

    // ── Targeted updates ──────────────────────────────────────────────────────

    /// <summary>
    /// A Group's coverage state changed in <paramref name="coverage"/> (its calculation completed or failed, or it was
    /// invalidated): recompose its card and its System's row from the facts already gathered. Pure — no catalog query,
    /// no leaf database, no filesystem. The result is what the complete refresh would compose for those two items;
    /// every other row and card is left untouched. Returns null (changing nothing) when the Group is not part of this
    /// data — the caller then performs the complete refresh.
    /// </summary>
    public SystemsGroupUpdate? TryApplyGroupCoverage(string groupId, GroupCoverageQueue coverage)
    {
        if (!_groupFacts.TryGetValue(groupId, out var group)) return null;
        int cardIndex = GroupCards.FindIndex(c => string.Equals(c.GroupId, group.GroupId, StringComparison.Ordinal));
        if (cardIndex < 0) return null;

        GroupCards[cardIndex] = ComposeGroupCard(group, coverage);

        string? platformId = null;
        if (_platformFacts.TryGetValue(group.Meta.HardwareFamilyId, out var platform))
        {
            int platformIndex = IndexOfPlatform(platform.Platform.Id);
            if (platformIndex >= 0)
            {
                Platforms[platformIndex] = ComposePlatform(platform, coverage);
                platformId = platform.Platform.Id;
            }
        }
        return new SystemsGroupUpdate(group.GroupId, platformId);
    }

    /// <summary>
    /// Something changed inside ONE System (a release status, a derived artifact, a volume assignment, staging):
    /// gather that System again exactly as <see cref="Load"/> gathers it — its Single-DAT leaf databases, their
    /// assigned artifacts, the LOCAL-volume owners, its staging probe, its Group cards — and replace its row and its
    /// cards in place. No other System's leaf database is opened and no other row changes.
    ///
    /// <para>Returns false, changing nothing, when the result could not be proven equal to the complete refresh: the
    /// System is unknown or gone, or its DAT lines / Groups / Group leaves are no longer the ones this data was
    /// gathered for (a structural change, which a targeted update does not cover). The caller then performs the
    /// complete refresh.</para>
    /// </summary>
    public bool TryRefreshPlatform(
        string platformId, CatalogService catalog, string dataDir, string appRoot, GroupCoverageQueue coverage)
    {
        int index = IndexOfPlatform(platformId);
        if (index < 0 || !_platformFacts.TryGetValue(platformId, out var previous)) return false;

        var platform = catalog.LoadPlatforms().FirstOrDefault(p => p.Id == platformId);
        if (platform is null) return false;

        var platformDatLines = catalog.LoadDatLines().Where(dl => dl.HardwareFamilyId == platformId).ToList();
        if (!SameIds(platformDatLines.Select(dl => dl.Id), previous.DatLineIds, StringComparer.Ordinal)) return false;

        // The System's Groups and their leaves must be the ones already known (otherwise membership changed).
        var groups = catalog.LoadDatGroups().Where(g => g.HardwareFamilyId == platformId).ToList();
        var known  = _groupOrder.Where(gid => _groupFacts[gid].Meta.HardwareFamilyId == platformId).ToList();
        if (!SameIds(groups.Select(g => g.Id.Value), known, StringComparer.Ordinal)) return false;

        var leavesByGroup = new Dictionary<string, List<GroupLeafRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var leaves = catalog.GetLeavesForGroup(g.Id.Value);
            if (!SameIds(leaves.Select(l => l.DatLine.Id), _groupFacts[g.Id.Value].LeafIds, StringComparer.Ordinal)) return false;
            leavesByGroup[g.Id.Value] = leaves;
        }

        // Gather (same functions as the complete refresh), then commit — nothing above has changed any state.
        var owners   = LocalOwners.Read(catalog);
        var staging  = StagingSnapshot.LeafIdsWithStaging(appRoot, platformId);
        var assigned = catalog.GetAssignedDerivedIdsByDatLines(
            platformDatLines.Where(dl => !LeafToGroup.ContainsKey(dl.Id)).Select(dl => dl.Id));
        var facts    = GatherPlatform(platform, platformDatLines, LeafToGroup, assigned, owners, staging, dataDir);

        foreach (var g in groups)
            SetGroup(GatherGroup(_groupFacts[g.Id.Value].GroupId, g, leavesByGroup[g.Id.Value], owners, staging), leavesByGroup[g.Id.Value]);
        _platformFacts[platformId] = facts;

        Platforms[index] = ComposePlatform(facts, coverage);
        foreach (var g in groups)
        {
            int cardIndex = GroupCards.FindIndex(c => string.Equals(c.GroupId, g.Id.Value, StringComparison.Ordinal));
            if (cardIndex >= 0) GroupCards[cardIndex] = ComposeGroupCard(_groupFacts[g.Id.Value], coverage);
        }
        return true;
    }

    // ── Gathering (catalog + leaf databases + filesystem) ─────────────────────

    private static readonly HashSet<string> NoIds = new(StringComparer.Ordinal);

    /// <summary>Owners of the volumes whose current location is the local workspace (one catalog query).</summary>
    private sealed record LocalOwners(HashSet<string> DatLines, HashSet<string> Groups)
    {
        public static LocalOwners Read(CatalogService catalog)
        {
            var owners = catalog.GetLocalWorkspaceVolumeOwners();
            return new LocalOwners(
                new HashSet<string>(owners.Where(o => o.DatLineId is not null).Select(o => o.DatLineId!), StringComparer.Ordinal),
                new HashSet<string>(owners.Where(o => o.GroupId is not null).Select(o => o.GroupId!), StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The Single-DAT half of one System. Single (non-group) leaves keep the exact historical behaviour: each existing
    /// database is opened once for its status counts and, in the SAME open, its present-derived assignment stats for
    /// the per-DAT lifecycle. Group leaves are never opened here.
    /// </summary>
    private static PlatformFacts GatherPlatform(
        HardwareFamilyRecord                         platform,
        IReadOnlyList<DatLineRecord>                 platformDatLines,
        IReadOnlyDictionary<string, string>          leafToGroup,
        IReadOnlyDictionary<string, HashSet<string>> assignedByDatLine,
        LocalOwners                                  owners,
        HashSet<string>                              stagingLeaves,
        string                                       dataDir)
    {
        int total   = platformDatLines.Sum(dl => dl.ReleaseCount);
        int present = 0, outdated = 0, unwanted = 0, lost = 0;
        var lifecycles = new List<ArchiveLifecycle>();

        foreach (var dl in platformDatLines)
        {
            if (leafToGroup.ContainsKey(dl.Id)) continue;   // group leaf — never opened on this path
            if (dl.DataStorePath.Length == 0) continue;
            var absPath = Path.Combine(dataDir, dl.DataStorePath);
            if (!File.Exists(absPath)) continue;
            var store = new DatLineStore(absPath);
            var c = store.GetAllStatusCounts();
            present  += c.Present;
            outdated += c.Outdated;
            unwanted += c.Unwanted;
            lost     += c.Lost;

            var assigned = assignedByDatLine.TryGetValue(dl.Id, out var s) ? s : NoIds;
            var (ownedDl, unassignedDl) = store.GetPresentDerivedAssignmentStats(assigned);
            lifecycles.Add(StorageLifecycle.Derive(ownedDl, unassignedDl, owners.DatLines.Contains(dl.Id)));
        }

        return new PlatformFacts(
            platform, platformDatLines.Select(dl => dl.Id).ToList(), total, present, outdated, unwanted, lost,
            lifecycles, stagingLeaves.Count > 0);
    }

    /// <summary>The catalog + filesystem half of one Group card (display metadata, leaf totals, LOCAL flag, staging flag).</summary>
    private GroupFacts GatherGroup(
        string gid, DatGroupRecord group, IReadOnlyList<GroupLeafRecord> leaves, LocalOwners owners, HashSet<string> stagingLeaves)
    {
        var leafIds = leaves.Select(l => l.DatLine.Id).ToList();
        return new GroupFacts(
            gid,
            new GroupMeta(group.DisplayName, AuthorityName(group.Authority), group.HardwareFamilyId),
            leafIds,
            leaves.Sum(l => l.DatLine.ReleaseCount),
            owners.Groups.Contains(gid),
            leafIds.Any(stagingLeaves.Contains));
    }

    private void SetGroup(GroupFacts facts, IReadOnlyList<GroupLeafRecord> leaves)
    {
        _groupFacts[facts.GroupId]     = facts;
        GroupLeafDbInfo[facts.GroupId] = leaves
            .Select(l => (l.DatLine.Id, l.DatLine.DataStorePath, l.DatLine.ReleaseCount)).ToList();
    }

    private string AuthorityName(string id)
        => id.Length == 0 ? id : AuthorityNames.TryGetValue(id, out var name) ? name : id;

    // ── Composition (pure: gathered facts + the coverage queue's current state) ──

    /// <summary>
    /// One System row: its Single-DAT facts folded with its Groups' coverage. Group leaves count ONLY from an already
    /// computed aggregate; otherwise coverage is pending (spinner) or, if the calculation faulted, failed (warning
    /// marker). Pending wins over failed.
    /// </summary>
    private SystemPlatform ComposePlatform(PlatformFacts facts, GroupCoverageQueue coverage)
    {
        var platform = facts.Platform;
        int present  = facts.Present, unwanted = facts.Unwanted;
        var childLifecycles = new List<ArchiveLifecycle>(facts.SingleLifecycles);

        bool coveragePending = false;
        bool anyGroupFailed  = false;
        foreach (var gid in _groupOrder)
        {
            var group = _groupFacts[gid];
            if (group.Meta.HardwareFamilyId != platform.Id) continue;

            if (coverage.TryGet(gid, out var cov))
            {
                present  += cov.Present;
                unwanted += cov.Unwanted;
                childLifecycles.Add(StorageLifecycle.Derive(cov.OwnedPresentDerived, cov.Unassigned, group.AnyLocalVolume));
            }
            else if (coverage.IsFailed(gid))
            {
                anyGroupFailed = true;    // its compute faulted — the System aggregate is incomplete
            }
            else
            {
                coveragePending = true;   // its leaves are still loading off the UI thread
            }
        }

        int wanted = Math.Max(0, facts.TotalTitles - unwanted);
        return new SystemPlatform
        {
            Id               = platform.Id,
            Name             = platform.Name,
            Manufacturer     = platform.Manufacturer,
            HardwareType     = HardwareTypeNames.TryGetValue(platform.HardwareTypeId, out var ht) ? ht : "",
            DatLines         = facts.DatLineIds.Count,
            TotalTitles      = facts.TotalTitles,
            Present          = present,
            Outdated         = facts.Outdated,
            Missing          = Math.Max(0, wanted - present - facts.Outdated - facts.Lost),
            Lost             = facts.Lost,
            Unwanted         = unwanted,
            CoveragePending  = coveragePending,
            CoverageFailed   = !coveragePending && anyGroupFailed,
            // Lifecycle is the fold of known child states; omit while any group is pending OR failed so a
            // partial load never renders a false ARCHIVED/NEEDS VOLUME.
            LifecyclePending = coveragePending || anyGroupFailed,
            Lifecycle        = (coveragePending || anyGroupFailed) ? ArchiveLifecycle.None : StorageLifecycle.Fold(childLifecycles),
            HasStaging       = facts.HasStaging,
        };
    }

    /// <summary>
    /// One Group card: computed from the coverage aggregate when available; failed → N/A (0,0) with the warning
    /// marker; otherwise pending (spinner) — never a false 0%.
    /// </summary>
    private static GroupDatCardInfo ComposeGroupCard(GroupFacts facts, GroupCoverageQueue coverage)
    {
        var m      = facts.Meta;
        int leaves = facts.LeafIds.Count;
        if (coverage.TryGet(facts.GroupId, out var cov))
        {
            int wanted = Math.Max(0, facts.ReleaseSum - cov.Unwanted);
            return new GroupDatCardInfo(facts.GroupId, m.DisplayName, m.Authority, m.HardwareFamilyId, leaves, cov.Present, wanted)
            {
                OwnedPresentDerived = cov.OwnedPresentDerived,
                Unassigned          = cov.Unassigned,
                AnyLocalVolume      = facts.AnyLocalVolume,
                HasStaging          = facts.HasStaging,
            };
        }
        if (coverage.IsFailed(facts.GroupId))
            return new GroupDatCardInfo(facts.GroupId, m.DisplayName, m.Authority, m.HardwareFamilyId, leaves, 0, 0)
                { CoverageFailed = true, HasStaging = facts.HasStaging };   // → warning marker, retry on select
        return new GroupDatCardInfo(facts.GroupId, m.DisplayName, m.Authority, m.HardwareFamilyId, leaves, 0, 0)
            { CoveragePending = true, HasStaging = facts.HasStaging };
    }

    private int IndexOfPlatform(string platformId) => Platforms.FindIndex(p => p.Id == platformId);

    private static bool SameIds(IEnumerable<string> now, IReadOnlyCollection<string> before, StringComparer comparer)
    {
        var set = new HashSet<string>(before, comparer);
        int count = 0;
        foreach (var id in now)
        {
            if (!set.Contains(id)) return false;
            count++;
        }
        return count == before.Count && set.Count == before.Count;
    }
}

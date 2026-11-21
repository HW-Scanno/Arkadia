using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Activities;
using Arkadia.Data;
using Arkadia.Systems;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// PERFORMANCE-02 — Systems &amp; Group coverage. The Systems view no longer loads the complete volume-artifact
/// assignment map, a completed Group coverage calculation updates only that Group's card and its System's row
/// (<see cref="SystemsViewData.TryApplyGroupCoverage"/>), and a release mutation refreshes only its own System
/// (<see cref="SystemsViewData.TryRefreshPlatform"/>).
///
/// <para>The correctness reference throughout is <see cref="LegacyFullRefresh"/>: the complete refresh exactly as
/// MainWindow.RefreshSystems computed it before this milestone, including the complete assignment map. Every optimized
/// result must be indistinguishable from it. Real <see cref="CatalogService"/>, real leaf databases, the real
/// <see cref="GroupCoverageQueue"/> / <see cref="GroupCoverageDrainer"/> / <see cref="GroupCoverageLoader"/> over a
/// temp directory — never the runtime data.</para>
/// </summary>
public sealed class SystemsTargetedRefreshTests : IDisposable
{
    private const string C64Single   = "c64-nointro-cart";
    private const string PsxCd       = "psx-redump-cd";
    private const string PsxExtra    = "psx-redump-extra";
    private const string NesSingle   = "nes-nointro";
    private const string NesNoStore  = "nes-nostore";
    private const string NesMissing  = "nes-missingdb";
    private const string GroupTosec  = "c64-tosec";
    private const string GroupMisc   = "c64-misc";
    private const string GroupAmiga  = "amiga-tosec";
    private const string LeafA       = "c64-tosec-games-d64";
    private const string LeafB       = "c64-tosec-demos-d64";
    private const string LeafC       = "c64-tosec-apps-d64";
    private const string LeafD       = "c64-misc-one";
    private const string LeafE       = "c64-misc-two";
    private const int    AmigaLeaves = 16;

    private static readonly string[] AllGroups = [GroupTosec, GroupMisc, GroupAmiga];

    private readonly string         _dir;       // application root (staging lives here)
    private readonly string         _dataDir;
    private readonly CatalogService _catalog;
    private readonly GroupCoverageQueue _queue = new();
    private readonly Dictionary<string, List<string>> _artifacts = new(StringComparer.Ordinal);   // leaf id → present derived ids

    public SystemsTargetedRefreshTests()
    {
        _dir     = Path.Combine(Path.GetTempPath(), "ArkPerf02_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataDir);
        _catalog = Seed();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static string PlatformOf(string datLineId) => datLineId[..datLineId.IndexOf('-')];
    private static string RelPath(string datLineId)    => $"systems/{PlatformOf(datLineId)}/{datLineId}.db";
    private string        DbPath(string datLineId)     => Path.Combine(_dataDir, RelPath(datLineId));
    private DatLineStore  Store(string datLineId)      => new(DbPath(datLineId));
    private static string AmigaLeaf(int i)             => $"amiga-tosec-set{i:D2}";

    private static string Sha1Of(params object[] parts)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes(string.Join('/', parts)))).ToLowerInvariant();

    private static List<DatParser.ParsedGame> Games(string prefix, int n) =>
        Enumerable.Range(0, n).Select(i => new DatParser.ParsedGame
        {
            Name = $"{prefix} Game {i:D2}",
            Roms = new List<DatParser.ParsedRom> { new() { Name = $"{prefix}-{i:D2}.bin", Size = "1", Sha1 = Sha1Of(prefix, i) } },
        }).ToList();

    private static DatLineRecord Line(string id, int releases, string authority, string media, bool withStore = true) => new()
    {
        Id = id, HardwareFamilyId = PlatformOf(id), Name = media, Authority = authority, MediaTypeId = media,
        Version = "1", DataStorePath = withStore ? RelPath(id) : "", ReleaseCount = releases,
        ImportedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    private void BuildLeafDb(string id, int releases)
        => LeafDatDatabaseBuilder.Build(DbPath(id), id, Games(id, releases), null, CancellationToken.None);

    private void CreateGroup(CatalogService c, string groupId, string name, IEnumerable<(string Id, int Releases)> leaves)
    {
        var creates = new List<GroupDatCatalogLeafCreate>();
        int n = 0;
        foreach (var (id, releases) in leaves)
        {
            n++;
            BuildLeafDb(id, releases);
            creates.Add(new GroupDatCatalogLeafCreate
            {
                DatLine = Line(id, releases, "tosec", "floppy"),
                RelativeDatPath = $"Set{n:D3}/g.dat", SourceDatName = "g.dat",
                SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
            });
        }
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = groupId, DisplayName = name, HardwareFamilyId = PlatformOf(groupId), Authority = "tosec", Leaves = creates,
        });
    }

    /// <summary>Marks the first releases present (each with a present derived artifact), then pending / unwanted / outdated / lost.</summary>
    private void SetStatuses(string leafId, int present = 0, int pending = 0, int unwanted = 0, int outdated = 0, int lost = 0)
    {
        var store = Store(leafId);
        var rel   = store.LoadReleases();
        int i     = 0;
        var ids   = _artifacts.TryGetValue(leafId, out var known) ? known : _artifacts[leafId] = [];
        for (int k = 0; k < present; k++, i++)
        {
            store.UpdateReleaseStatus(rel[i].Id, "present");
            ids.Add(store.IngestDerivedArtifact(
                "sha1:" + Sha1Of(leafId, i, "cik"), "", "chd", $"{leafId}-{i}.chd", $"archive/x/{leafId}/{leafId}-{i}.chd",
                4096, Sha1Of(leafId, i, "derived")));
        }
        for (int k = 0; k < pending;  k++, i++) store.UpdateReleaseStatus(rel[i].Id, "pending");
        for (int k = 0; k < unwanted; k++, i++) store.UpdateReleaseStatus(rel[i].Id, "unwanted");
        for (int k = 0; k < outdated; k++, i++) store.UpdateReleaseStatus(rel[i].Id, "outdated");
        for (int k = 0; k < lost;     k++, i++) store.UpdateReleaseStatus(rel[i].Id, "lost");
    }

    private void AddVolume(string id, string platformId, string? datLineId, string? groupId, string locationType)
    {
        _catalog.SaveVolume(new VolumeRecord
        {
            Id = id, Label = id.ToUpperInvariant(), PlatformId = platformId, DatLineId = datLineId, GroupId = groupId,
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        });
        MoveVolume(id, locationType);
    }

    /// <summary>What Move Volume commits: the volume's current location (workspace = LOCAL, disk = archived).</summary>
    private void MoveVolume(string volumeId, string locationType)
        => _catalog.SetCurrentLocation(new VolumeLocationRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = volumeId, LocationType = locationType,
            IsCurrent = true, CreatedAt = DateTime.UtcNow,
        });

    /// <summary>What Append / Build commit after a verified copy: one volume_artifacts row.</summary>
    private void Assign(string volumeId, string leafId, string derivedId)
        => _catalog.AddVolumeArtifactAndIncrementSize(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = volumeId, DatLineId = leafId, DerivedArtifactId = derivedId,
            ContentIdentityKey = "", Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        }, 4096);

    /// <summary>
    /// Five Systems. c64: one Single DAT + two Groups (3 and 2 leaves). psx: two Single DATs. amiga: one Group with 16
    /// leaves. nes: one Single DAT plus a DAT line without a store and one whose database file is missing. snes: empty.
    /// Mixed statuses, present derived artifacts, and volumes in every lifecycle state.
    /// </summary>
    private CatalogService Seed()
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareType(new HardwareTypeRecord { Id = "perf02-computer", Name = "Computer", SortOrder = 900 });
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = "c64",   Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "perf02-computer" },
            new() { Id = "psx",   Name = "PlayStation",  Manufacturer = "Sony",      HardwareTypeId = "" },
            new() { Id = "amiga", Name = "Amiga",        Manufacturer = "Commodore", HardwareTypeId = "perf02-computer" },
            new() { Id = "nes",   Name = "NES",          Manufacturer = "Nintendo",  HardwareTypeId = "" },
            new() { Id = "snes",  Name = "Super NES",    Manufacturer = "Nintendo",  HardwareTypeId = "" },
        });

        foreach (var (id, n) in new[] { (C64Single, 8), (PsxCd, 12), (PsxExtra, 5), (NesSingle, 6) }) BuildLeafDb(id, n);
        c.SaveDatLines(new List<DatLineRecord>
        {
            Line(C64Single, 8, "nointro", "cartridge"),
            Line(PsxCd, 12, "redump", "cd"),
            Line(PsxExtra, 5, "redump", "dvd"),
            Line(NesSingle, 6, "nointro", "cartridge"),
            Line(NesNoStore, 4, "nointro", "rom", withStore: false),   // counted in the totals, never opened
            Line(NesMissing, 3, "nointro", "other"),                    // store path set, database file absent
        });

        CreateGroup(c, GroupTosec, "Commodore 64 TOSEC", new[] { (LeafA, 7), (LeafB, 8), (LeafC, 9) });
        CreateGroup(c, GroupMisc,  "Commodore 64 Misc",  new[] { (LeafD, 4), (LeafE, 5) });
        CreateGroup(c, GroupAmiga, "Amiga TOSEC", Enumerable.Range(0, AmigaLeaves).Select(i => (AmigaLeaf(i), 3)));
        return c;
    }

    /// <summary>Statuses, artifacts and volumes — applied after the constructor so helpers can use <see cref="_catalog"/>.</summary>
    private void SeedState()
    {
        SetStatuses(C64Single, present: 3, pending: 1, unwanted: 1);
        SetStatuses(PsxCd,     present: 4, unwanted: 2, outdated: 1, lost: 1);
        SetStatuses(PsxExtra,  present: 1);
        SetStatuses(NesSingle, present: 2, pending: 1);
        SetStatuses(LeafA,     present: 2, unwanted: 1);
        SetStatuses(LeafB,     present: 1, pending: 2);
        SetStatuses(LeafC,     unwanted: 3);
        SetStatuses(LeafD,     present: 1);
        for (int i = 0; i < AmigaLeaves; i += 4) SetStatuses(AmigaLeaf(i), present: 1, unwanted: i % 8 == 0 ? 1 : 0);

        // c64 Single: every artifact on an archived (disk) volume → ARCHIVED.
        AddVolume("v-c64-single", "c64", C64Single, null, "disk");
        foreach (var da in _artifacts[C64Single]) Assign("v-c64-single", C64Single, da);

        // psx CD: on a LOCAL (workspace) volume, one artifact left unassigned → NEEDS VOLUME. psx extra: unassigned.
        AddVolume("v-psx-cd", "psx", PsxCd, null, "workspace");
        foreach (var da in _artifacts[PsxCd].Take(3)) Assign("v-psx-cd", PsxCd, da);

        // c64 TOSEC Group volume, LOCAL: leaf A fully assigned, leaf B's artifact assigned → READY TO MOVE.
        AddVolume("v-c64-tosec", "c64", null, GroupTosec, "workspace");
        foreach (var da in _artifacts[LeafA]) Assign("v-c64-tosec", LeafA, da);
        foreach (var da in _artifacts[LeafB]) Assign("v-c64-tosec", LeafB, da);

        // Amiga Group volume on disk: some leaves assigned, the others not → NEEDS VOLUME.
        AddVolume("v-amiga", "amiga", null, GroupAmiga, "disk");
        for (int i = 0; i < AmigaLeaves; i += 8) Assign("v-amiga", AmigaLeaf(i), _artifacts[AmigaLeaf(i)][0]);
    }

    // ── The reference: the complete refresh as it was before PERFORMANCE-02 ────

    /// <summary>
    /// MainWindow.RefreshSystems' data half, verbatim from before this milestone (complete assignment map included).
    /// This is the oracle: it must never be "fixed" to follow the optimized code.
    /// </summary>
    private (List<SystemPlatform> Platforms, List<GroupDatCardInfo> Cards) LegacyFullRefresh()
    {
        var hardwareTypeMap  = _catalog.LoadHardwareTypes().ToDictionary(h => h.Id, h => h.Name);
        var authorityNameMap = _catalog.LoadAuthorities().ToDictionary(a => a.Id, a => a.Name);
        string GetAuthorityName(string id) => id.Length == 0 ? id : authorityNameMap.TryGetValue(id, out var name) ? name : id;

        var allDatLines = _catalog.LoadDatLines();

        var leafToGroup     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupMeta       = new Dictionary<string, GroupMeta>(StringComparer.OrdinalIgnoreCase);
        var groupLeafCount  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupReleaseSum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupLeafIdList = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _catalog.LoadDatGroups())
        {
            groupMeta[g.Id.Value] = new GroupMeta(g.DisplayName, GetAuthorityName(g.Authority), g.HardwareFamilyId);
            var idList = new List<string>();
            int relSum = 0;
            foreach (var leaf in _catalog.GetLeavesForGroup(g.Id.Value))
            {
                leafToGroup[leaf.DatLine.Id] = g.Id.Value;
                idList.Add(leaf.DatLine.Id);
                relSum += leaf.DatLine.ReleaseCount;
            }
            groupLeafCount[g.Id.Value]  = idList.Count;
            groupReleaseSum[g.Id.Value] = relSum;
            groupLeafIdList[g.Id.Value] = idList;
        }

        var appRoot           = _dir;
        var localOwners       = _catalog.GetLocalWorkspaceVolumeOwners();
        var localDatLines     = new HashSet<string>(
            localOwners.Where(o => o.DatLineId is not null).Select(o => o.DatLineId!), StringComparer.Ordinal);
        var localGroups       = new HashSet<string>(
            localOwners.Where(o => o.GroupId is not null).Select(o => o.GroupId!), StringComparer.OrdinalIgnoreCase);
        var assignedByDatLine = _catalog.GetAllAssignedDerivedIdsByDatLine();
        var emptyAssigned     = new HashSet<string>(StringComparer.Ordinal);

        var platformIdsForStaging = _catalog.LoadPlatforms().Select(p => p.Id).Distinct(StringComparer.Ordinal);
        var stagingByPlatform     = platformIdsForStaging.ToDictionary(
            pid => pid, pid => StagingSnapshot.LeafIdsWithStaging(appRoot, pid), StringComparer.Ordinal);
        HashSet<string> StagingFor(string pid) =>
            stagingByPlatform.TryGetValue(pid, out var set) ? set : new HashSet<string>(StringComparer.Ordinal);

        var groupLifecycle = new Dictionary<string, ArchiveLifecycle>(StringComparer.OrdinalIgnoreCase);
        foreach (var gid in groupMeta.Keys)
            if (_queue.TryGet(gid, out var gf))
                groupLifecycle[gid] = StorageLifecycle.Derive(
                    gf.OwnedPresentDerived, gf.Unassigned, localGroups.Contains(gid));

        var systemsPlatforms = _catalog.LoadPlatforms()
            .Select(platform =>
            {
                var platformDatLines = allDatLines.Where(dl => dl.HardwareFamilyId == platform.Id).ToList();
                var total   = platformDatLines.Sum(dl => dl.ReleaseCount);
                int present = 0, outdated = 0, unwanted = 0, lost = 0;

                var stagingLeaves = StagingFor(platform.Id);
                var childLifecycles = new List<ArchiveLifecycle>();

                foreach (var dl in platformDatLines)
                {
                    if (leafToGroup.ContainsKey(dl.Id)) continue;
                    if (dl.DataStorePath.Length == 0) continue;
                    var absPath = Path.Combine(_dataDir, dl.DataStorePath);
                    if (!File.Exists(absPath)) continue;
                    var store = new DatLineStore(absPath);
                    var c = store.GetAllStatusCounts();
                    present  += c.Present;
                    outdated += c.Outdated;
                    unwanted += c.Unwanted;
                    lost     += c.Lost;

                    var assigned = assignedByDatLine.TryGetValue(dl.Id, out var s) ? s : emptyAssigned;
                    var (ownedDl, unassignedDl) = store.GetPresentDerivedAssignmentStats(assigned);
                    childLifecycles.Add(StorageLifecycle.Derive(ownedDl, unassignedDl, localDatLines.Contains(dl.Id)));
                }

                bool coveragePending = false;
                bool anyGroupFailed  = false;
                foreach (var gid in groupMeta.Keys.Where(k => groupMeta[k].HardwareFamilyId == platform.Id))
                {
                    if (_queue.TryGet(gid, out var cov))
                    {
                        present  += cov.Present;
                        unwanted += cov.Unwanted;
                        childLifecycles.Add(groupLifecycle[gid]);
                    }
                    else if (_queue.IsFailed(gid))
                    {
                        anyGroupFailed = true;
                    }
                    else
                    {
                        coveragePending = true;
                    }
                }

                int wanted = Math.Max(0, total - unwanted);
                return new SystemPlatform
                {
                    Id               = platform.Id,
                    Name             = platform.Name,
                    Manufacturer     = platform.Manufacturer,
                    HardwareType     = hardwareTypeMap.TryGetValue(platform.HardwareTypeId, out var ht) ? ht : "",
                    DatLines         = platformDatLines.Count,
                    TotalTitles      = total,
                    Present          = present,
                    Outdated         = outdated,
                    Missing          = Math.Max(0, wanted - present - outdated - lost),
                    Lost             = lost,
                    Unwanted         = unwanted,
                    CoveragePending  = coveragePending,
                    CoverageFailed   = !coveragePending && anyGroupFailed,
                    LifecyclePending = coveragePending || anyGroupFailed,
                    Lifecycle        = (coveragePending || anyGroupFailed) ? ArchiveLifecycle.None : StorageLifecycle.Fold(childLifecycles),
                    HasStaging       = stagingLeaves.Count > 0,
                };
            })
            .ToList();

        var groupCards = groupMeta.Select(kv =>
        {
            var (gid, m) = (kv.Key, kv.Value);
            int leaves = groupLeafCount.TryGetValue(gid, out var lc) ? lc : 0;
            var stagingSet = StagingFor(m.HardwareFamilyId);
            bool hasStaging = groupLeafIdList.TryGetValue(gid, out var ids) && ids.Any(stagingSet.Contains);
            if (_queue.TryGet(gid, out var cov))
            {
                int wanted = Math.Max(0, (groupReleaseSum.TryGetValue(gid, out var rs) ? rs : 0) - cov.Unwanted);
                return new GroupDatCardInfo(gid, m.DisplayName, m.Authority, m.HardwareFamilyId, leaves, cov.Present, wanted)
                {
                    OwnedPresentDerived = cov.OwnedPresentDerived,
                    Unassigned          = cov.Unassigned,
                    AnyLocalVolume      = localGroups.Contains(gid),
                    HasStaging          = hasStaging,
                };
            }
            if (_queue.IsFailed(gid))
                return new GroupDatCardInfo(gid, m.DisplayName, m.Authority, m.HardwareFamilyId, leaves, 0, 0)
                    { CoverageFailed = true, HasStaging = hasStaging };
            return new GroupDatCardInfo(gid, m.DisplayName, m.Authority, m.HardwareFamilyId, leaves, 0, 0)
                { CoveragePending = true, HasStaging = hasStaging };
        }).ToList();

        return (systemsPlatforms, groupCards);
    }

    /// <summary>The Group aggregate as the worker computed it before this milestone: with the COMPLETE assignment map.</summary>
    private GroupStorageFacts LegacyGroupFacts(string groupId)
        => GroupCoverageLoader.Compute(LeavesOf(groupId), _dataDir, _catalog.GetAllAssignedDerivedIdsByDatLine());

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SystemsViewData Load() => SystemsViewData.Load(_catalog, _dataDir, _dir, _queue);

    private List<(string DatLineId, string DataStorePath, int ReleaseCount)> LeavesOf(string groupId)
        => _catalog.GetLeavesForGroup(groupId)
            .Select(l => (l.DatLine.Id, l.DatLine.DataStorePath, l.DatLine.ReleaseCount)).ToList();

    /// <summary>One Group through the queue exactly as the worker does it: Begin → the worker's compute → Complete.</summary>
    private void ComputeGroup(string groupId)
    {
        _queue.Enqueue(groupId, priority: true, _ => true);
        var started = _queue.Begin(_ => true)!.Value;
        Assert.Equal(groupId, started.GroupId);
        Assert.True(_queue.Complete(groupId, started.Generation, GroupCoverageLoader.Compute(LeavesOf(groupId), _dataDir, _catalog)));
    }

    private void ComputeAllGroups() { foreach (var g in AllGroups) ComputeGroup(g); }

    private static string Snapshot(IEnumerable<SystemPlatform> platforms, IEnumerable<GroupDatCardInfo> cards)
    {
        var sb = new StringBuilder();
        foreach (var p in platforms)
            sb.AppendJoin('|', "SYS", p.Id, p.Name, p.Manufacturer, p.HardwareType, p.DatLines, p.TotalTitles, p.Present,
                p.Outdated, p.Missing, p.Lost, p.Unwanted, p.WantedTitles, p.WantedCoverage, p.UnwantedShare,
                p.CoveragePending, p.CoverageFailed, p.LifecyclePending, p.Lifecycle, p.HasStaging).Append('\n');
        foreach (var c in cards)
            sb.Append("GRP|").Append(c).Append('\n');   // record: every property, computed ones included
        return sb.ToString();
    }

    private static string Snapshot(SystemsViewData v) => Snapshot(v.Platforms, v.GroupCards);

    /// <summary>The optimized state must equal BOTH a fresh complete refresh and the pre-milestone reference.</summary>
    private void AssertConverged(SystemsViewData view)
    {
        var legacy = LegacyFullRefresh();
        var expected = Snapshot(legacy.Platforms, legacy.Cards);
        Assert.Equal(expected, Snapshot(Load()));
        Assert.Equal(expected, Snapshot(view));
    }

    private static SystemPlatform   Sys(SystemsViewData v, string id)  => v.Platforms.Single(p => p.Id == id);
    private static GroupDatCardInfo Card(SystemsViewData v, string id) => v.GroupCards.Single(c => c.GroupId == id);

    private string ReleaseId(string leafId, int index) => Store(leafId).LoadReleases()[index].Id;

    private void MarkUnwanted(string leafId, int index)
    {
        // The exact statements of the "Mark as Unwanted" handler.
        var store = Store(leafId);
        var id    = ReleaseId(leafId, index);
        store.UpdateReleaseStatus(id, "unwanted");
        store.SetShowInCatalog(id, false);
    }

    private void RestoreWanted(string leafId, int index) => Store(leafId).RestoreWantedRelease(ReleaseId(leafId, index));

    /// <summary>MainWindow.ConvergeSystemsAfterLeafReleaseMutation's data half, statement for statement.</summary>
    private bool ConvergeAfterLeafMutation(SystemsViewData view, string leafId)
    {
        string? groupId = view.LeafToGroup.TryGetValue(leafId, out var gid) ? gid : null;
        if (groupId is not null) _queue.Invalidate(groupId);
        return (groupId is null || view.TryApplyGroupCoverage(groupId, _queue) is not null)
            && view.TryRefreshPlatform(PlatformOf(leafId), _catalog, _dataDir, _dir, _queue);
    }

    private void AddStaging(string leafId)
    {
        var dir = Path.Combine(_dir, "staging", PlatformOf(leafId), leafId, "Some Release");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "part.bin"), [1, 2, 3]);
    }

    private static string Region(string source, string start, string end)
    {
        int a = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"start marker not found: {start}");
        int b = source.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"end marker not found: {end}");
        return source[a..b];
    }

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }

    /// <summary>The real worker with a hand-pumped "UI thread"; its refresh callback is the targeted Group update.</summary>
    private sealed class Worker
    {
        private readonly ConcurrentQueue<Action> _ui = new();
        public readonly List<string> Refreshed = [];
        public readonly List<string> Computed  = [];
        public GroupCoverageDrainer Drainer { get; }

        public Worker(SystemsTargetedRefreshTests t, SystemsViewData view,
                      Func<IReadOnlyList<(string, string, int)>, Task<GroupStorageFacts>>? compute = null)
        {
            string current = "";
            Drainer = new GroupCoverageDrainer(
                t._queue,
                gid => view.GroupLeafDbInfo.ContainsKey(gid),
                gid => { current = gid; return view.GroupLeafDbInfo[gid]; },
                leaves =>
                {
                    Computed.Add(current);
                    // The production compute, run synchronously here so leaf-database opens are counted on this thread.
                    return compute?.Invoke(leaves) ?? Task.FromResult(GroupCoverageLoader.Compute(leaves, t._dataDir, t._catalog));
                },
                _ui.Enqueue,
                gid =>
                {
                    Refreshed.Add(gid);
                    Assert.NotNull(view.TryApplyGroupCoverage(gid, t._queue));   // the targeted update — never a complete refresh
                },
                new ActivityMonitor());
        }

        /// <summary>Runs posted UI work until the worker is idle.</summary>
        public void Pump()
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                while (_ui.TryDequeue(out var a)) a();
                if (!Drainer.IsBusy && _ui.IsEmpty) return;
                Assert.True(DateTime.UtcNow < deadline, "the coverage worker did not become idle");
                Thread.Sleep(1);
            }
        }

        public void PumpOnce()
        {
            Assert.True(SpinWait.SpinUntil(() => !_ui.IsEmpty, TimeSpan.FromSeconds(60)), "no continuation was posted");
            while (_ui.TryDequeue(out var a)) a();
        }
    }

    // ── The fixture itself is meaningful ──────────────────────────────────────

    [Fact]
    public void Fixture_CoversEveryLifecycleAndStatus()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();

        Assert.Equal(5, v.Platforms.Count);
        Assert.Equal(3, v.GroupCards.Count);
        Assert.Equal(new[] { "c64", "psx", "amiga", "nes", "snes" }.OrderBy(x => x), v.Platforms.Select(p => p.Id).OrderBy(x => x));

        Assert.Equal("Computer", Sys(v, "c64").HardwareType);
        Assert.Equal(1 + 3 + 2, Sys(v, "c64").DatLines);
        Assert.Equal(8 + 7 + 8 + 9 + 4 + 5, Sys(v, "c64").TotalTitles);
        Assert.Equal(3 + 2 + 1 + 0 + 1 + 0, Sys(v, "c64").Present);
        Assert.Equal(1 + 1 + 0 + 3, Sys(v, "c64").Unwanted);
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Sys(v, "c64").Lifecycle);          // the Misc group's artifact is unassigned

        Assert.Equal(ArchiveLifecycle.ReadyToMove, Card(v, GroupTosec).Lifecycle);    // everything assigned, volume still LOCAL
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Card(v, GroupMisc).Lifecycle);
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Card(v, GroupAmiga).Lifecycle);
        Assert.Equal(AmigaLeaves, Card(v, GroupAmiga).LeafCount);

        Assert.Equal(1, Sys(v, "psx").Outdated);
        Assert.Equal(1, Sys(v, "psx").Lost);
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Sys(v, "psx").Lifecycle);
        Assert.Equal(3, Sys(v, "nes").DatLines);                                       // store-less and missing-db lines still count
        Assert.Equal(6 + 4 + 3, Sys(v, "nes").TotalTitles);
        Assert.Equal(0, Sys(v, "snes").DatLines);
        Assert.Equal("N/A", Sys(v, "snes").WantedCoverage);
    }

    // ── 1. The complete refresh is unchanged ──────────────────────────────────

    [Fact]
    public void CompleteRefresh_MatchesTheReference_WithNoGroupComputed()
    {
        SeedState();
        var v = Load();
        AssertConverged(v);
        Assert.True(Sys(v, "c64").CoveragePending);
        Assert.True(Sys(v, "amiga").CoveragePending);
        Assert.False(Sys(v, "psx").CoveragePending);       // Single-DAT Systems are final at once
        Assert.All(v.GroupCards, c => Assert.True(c.CoveragePending));
    }

    [Fact]
    public void CompleteRefresh_MatchesTheReference_WithEveryGroupComputed()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        AssertConverged(v);
        Assert.All(v.Platforms, p => Assert.False(p.CoveragePending || p.CoverageFailed || p.LifecyclePending));
    }

    [Fact]
    public void CompleteRefresh_MatchesTheReference_WithMixedPendingFailedAndComputedGroups()
    {
        SeedState();
        ComputeGroup(GroupTosec);                                   // computed
        _queue.Enqueue(GroupAmiga, false, _ => true);
        var b = _queue.Begin(_ => true)!.Value;
        Assert.True(_queue.Fail(GroupAmiga, b.Generation));         // failed; Misc stays pending

        var v = Load();
        AssertConverged(v);
        Assert.True(Sys(v, "c64").CoveragePending);                 // pending wins on c64 (Misc)
        Assert.True(Sys(v, "amiga").CoverageFailed);
        Assert.True(Card(v, GroupAmiga).CoverageFailed);
        Assert.Equal(ArchiveLifecycle.None, Sys(v, "amiga").Lifecycle);
    }

    [Fact]
    public void CompleteRefresh_OpensEachSingleDatOnce_AndNoGroupLeaf()
    {
        SeedState();
        DatLineStore.ConstructionCount = 0;
        Load();
        Assert.Equal(4, DatLineStore.ConstructionCount);   // c64 cart, psx cd, psx extra, nes — none of the 21 Group leaves
    }

    // ── 2. Volume-artifact assignments: targeted instead of the complete map ───

    [Fact]
    public void TargetedAssignments_EqualTheCompleteMap_ForTheRequestedDatLines()
    {
        SeedState();
        // The same artifact on a second volume (duplicate across volumes) must still be one id in the set.
        AddVolume("v-psx-copy", "psx", PsxCd, null, "disk");
        Assign("v-psx-copy", PsxCd, _artifacts[PsxCd][0]);

        var complete = _catalog.GetAllAssignedDerivedIdsByDatLine();
        var allIds   = _catalog.LoadDatLines().Select(dl => dl.Id).ToList();

        var everything = _catalog.GetAssignedDerivedIdsByDatLines(allIds);
        Assert.Equal(complete.Keys.OrderBy(k => k, StringComparer.Ordinal), everything.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (id, set) in complete) Assert.True(set.SetEquals(everything[id]), id);

        var some = _catalog.GetAssignedDerivedIdsByDatLines(new[] { PsxCd, PsxCd, NesSingle, "no-such-dat-line", LeafA });
        Assert.Equal(new[] { LeafA, PsxCd }, some.Keys.OrderBy(k => k, StringComparer.Ordinal));   // unassigned / unknown lines are absent
        Assert.True(complete[PsxCd].SetEquals(some[PsxCd]));
        Assert.True(complete[LeafA].SetEquals(some[LeafA]));
        Assert.Equal(3, some[PsxCd].Count);                                                         // the duplicate collapsed

        Assert.Empty(_catalog.GetAssignedDerivedIdsByDatLines(Array.Empty<string>()));
        Assert.Empty(_catalog.GetAssignedDerivedIdsByDatLines(new[] { PsxCd.ToUpperInvariant() }));  // ids match case-sensitively, like the map
    }

    [Fact]
    public void GroupCompute_WithTargetedAssignments_EqualsTheCompleteMapCompute()
    {
        SeedState();
        foreach (var g in AllGroups)
            Assert.Equal(LegacyGroupFacts(g), GroupCoverageLoader.Compute(LeavesOf(g), _dataDir, _catalog));

        // …and after assignment changes of every kind.
        Assign("v-c64-tosec", LeafD, _artifacts[LeafD][0]);                                    // cross-owner assignment
        _catalog.RemoveVolumeArtifacts("v-amiga", new List<string> { _artifacts[AmigaLeaf(0)][0] });
        foreach (var g in AllGroups)
            Assert.Equal(LegacyGroupFacts(g), GroupCoverageLoader.Compute(LeavesOf(g), _dataDir, _catalog));
    }

    [Fact]
    public void GroupCompute_OpensExactlyTheGroupsLeaves()
    {
        SeedState();
        DatLineStore.ConstructionCount = 0;
        GroupCoverageLoader.Compute(LeavesOf(GroupTosec), _dataDir, _catalog);
        Assert.Equal(3, DatLineStore.ConstructionCount);
    }

    // ── 3. Group coverage completion: only the Group and its System ───────────

    [Fact]
    public void GroupCompletion_UpdatesOnlyItsCardAndItsSystem_AndMatchesTheCompleteRefresh()
    {
        SeedState();
        var v = Load();
        var platformsBefore = v.Platforms.ToList();
        var cardsBefore     = v.GroupCards.ToList();

        ComputeGroup(GroupAmiga);
        DatLineStore.ConstructionCount = 0;
        var update = v.TryApplyGroupCoverage(GroupAmiga, _queue);

        Assert.Equal(new SystemsGroupUpdate(GroupAmiga, "amiga"), update);
        Assert.Equal(0, DatLineStore.ConstructionCount);                                 // no leaf database at all
        AssertConverged(v);

        // Same lists, same order; every other row/card is the very same object.
        Assert.Equal(platformsBefore.Select(p => p.Id), v.Platforms.Select(p => p.Id));
        Assert.Equal(cardsBefore.Select(c => c.GroupId), v.GroupCards.Select(c => c.GroupId));
        foreach (var p in platformsBefore.Where(p => p.Id != "amiga")) Assert.Same(p, Sys(v, p.Id));
        foreach (var c in cardsBefore.Where(c => c.GroupId != GroupAmiga)) Assert.Same(c, Card(v, c.GroupId));
        Assert.NotSame(platformsBefore.Single(p => p.Id == "amiga"), Sys(v, "amiga"));

        Assert.False(Sys(v, "amiga").CoveragePending);
        Assert.False(Card(v, GroupAmiga).CoveragePending);
        Assert.True(Sys(v, "c64").CoveragePending);                                      // c64's groups are still loading
    }

    [Fact]
    public void GroupCompletion_SystemStaysPendingUntilItsLastGroupLands()
    {
        SeedState();
        var v = Load();

        ComputeGroup(GroupTosec);
        v.TryApplyGroupCoverage(GroupTosec, _queue);
        AssertConverged(v);
        Assert.False(Card(v, GroupTosec).CoveragePending);
        Assert.True(Sys(v, "c64").CoveragePending);                                      // Misc not computed yet → no partial figure
        Assert.Equal(ArchiveLifecycle.None, Sys(v, "c64").Lifecycle);

        ComputeGroup(GroupMisc);
        v.TryApplyGroupCoverage(GroupMisc, _queue);
        AssertConverged(v);
        Assert.False(Sys(v, "c64").CoveragePending);
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Sys(v, "c64").Lifecycle);
    }

    [Fact]
    public void GroupCompletion_InAnyOrder_ConvergesToTheSameState()
    {
        SeedState();
        string? first = null;
        foreach (var order in new[] { new[] { GroupTosec, GroupMisc, GroupAmiga }, new[] { GroupAmiga, GroupMisc, GroupTosec } })
        {
            foreach (var g in AllGroups) _queue.Invalidate(g);
            var v = Load();
            foreach (var g in order) { ComputeGroup(g); v.TryApplyGroupCoverage(g, _queue); }
            AssertConverged(v);
            first ??= Snapshot(v);
            Assert.Equal(first, Snapshot(v));
        }
    }

    [Fact]
    public void GroupFailure_Retry_AndInvalidation_AreReflectedExactly()
    {
        SeedState();
        ComputeGroup(GroupMisc);
        var v = Load();

        // failed
        _queue.Enqueue(GroupTosec, false, _ => true);
        var b = _queue.Begin(_ => true)!.Value;
        Assert.True(_queue.Fail(GroupTosec, b.Generation));
        v.TryApplyGroupCoverage(GroupTosec, _queue);
        AssertConverged(v);
        Assert.True(Card(v, GroupTosec).CoverageFailed);
        Assert.True(Sys(v, "c64").CoverageFailed);
        Assert.True(Sys(v, "c64").LifecyclePending);

        // retry (selection) → pending again
        _queue.Retry(GroupTosec);
        v.TryApplyGroupCoverage(GroupTosec, _queue);
        AssertConverged(v);
        Assert.True(Card(v, GroupTosec).CoveragePending);
        Assert.False(Sys(v, "c64").CoverageFailed);

        // computed
        ComputeGroup(GroupTosec);
        v.TryApplyGroupCoverage(GroupTosec, _queue);
        AssertConverged(v);
        Assert.Equal(ArchiveLifecycle.ReadyToMove, Card(v, GroupTosec).Lifecycle);

        // invalidated → pending, and the System with it
        _queue.Invalidate(GroupTosec);
        v.TryApplyGroupCoverage(GroupTosec, _queue);
        AssertConverged(v);
        Assert.True(Card(v, GroupTosec).CoveragePending);
        Assert.True(Sys(v, "c64").CoveragePending);
    }

    [Fact]
    public void UnknownGroup_IsNotApplied_SoTheCallerFallsBackToTheCompleteRefresh()
    {
        SeedState();
        var v = Load();
        var before = Snapshot(v);
        Assert.Null(v.TryApplyGroupCoverage("no-such-group", _queue));
        Assert.Equal(before, Snapshot(v));
    }

    // ── 4. The real worker: 3 Groups converge with 3 targeted updates ─────────

    [Fact]
    public void Worker_ConvergesEveryGroup_WithOneTargetedUpdateEach_AndNoCompleteRefresh()
    {
        SeedState();
        var v = Load();
        var w = new Worker(this, v);

        foreach (var c in v.GroupCards) _queue.Enqueue(c.GroupId, priority: false, v.GroupLeafDbInfo.ContainsKey);
        DatLineStore.ConstructionCount = 0;
        w.Drainer.Drain();
        w.Pump();

        Assert.Equal(AllGroups.OrderBy(x => x), w.Refreshed.OrderBy(x => x));             // exactly one update per Group
        Assert.Equal(3 + 2 + AmigaLeaves, DatLineStore.ConstructionCount);              // each Group leaf opened once; no Single reopened
        AssertConverged(v);
        Assert.All(v.Platforms, p => Assert.False(p.CoveragePending));
    }

    [Fact]
    public void Worker_FailedGroup_ShowsTheWarning_AndIsNotRetriedByOtherCompletions()
    {
        SeedState();
        var v = Load();
        var w = new Worker(this, v, leaves => leaves.Count == 3
            ? Task.FromException<GroupStorageFacts>(new IOException("leaf DB unreadable"))     // c64-tosec (3 leaves) faults
            : Task.FromResult(GroupCoverageLoader.Compute(leaves, _dataDir, _catalog)));

        foreach (var c in v.GroupCards) _queue.Enqueue(c.GroupId, priority: false, v.GroupLeafDbInfo.ContainsKey);
        w.Drainer.Drain();
        w.Pump();

        Assert.Equal(1, w.Computed.Count(g => g == GroupTosec));                          // one attempt, no retry storm
        Assert.True(_queue.IsFailed(GroupTosec));
        AssertConverged(v);
        Assert.True(Card(v, GroupTosec).CoverageFailed);
        Assert.True(Sys(v, "c64").CoverageFailed);
        Assert.False(Sys(v, "amiga").CoveragePending);
    }

    [Fact]
    public void ManyGroups_AndALargeGroup_ConvergeWithoutACompleteRefresh_OrAnyExtraLeafOpen()
    {
        SeedState();
        // A 150-leaf Group plus six more small Groups spread over three Systems.
        const int bigLeaves = 150;
        CreateGroup(_catalog, "amiga-big", "Amiga Big", Enumerable.Range(0, bigLeaves).Select(i => ($"amiga-big-leaf{i:D3}", 2)));
        foreach (var sys in new[] { "psx", "nes", "snes" })
            for (int g = 0; g < 2; g++)
                CreateGroup(_catalog, $"{sys}-extra{g}", $"{sys} extra {g}", new[] { ($"{sys}-extra{g}-a", 2), ($"{sys}-extra{g}-b", 3) });
        for (int i = 0; i < bigLeaves; i += 7) SetStatuses($"amiga-big-leaf{i:D3}", present: 1, unwanted: i % 2);
        SetStatuses("snes-extra1-b", present: 2);
        AddVolume("v-amiga-big", "amiga", null, "amiga-big", "workspace");
        for (int i = 0; i < bigLeaves; i += 14) Assign("v-amiga-big", $"amiga-big-leaf{i:D3}", _artifacts[$"amiga-big-leaf{i:D3}"][0]);

        DatLineStore.ConstructionCount = 0;
        var v = Load();
        Assert.Equal(4, DatLineStore.ConstructionCount);            // still only the four Single DATs
        Assert.Equal(10, v.GroupCards.Count);
        AssertConverged(v);
        Assert.All(v.GroupCards, c => Assert.True(c.CoveragePending));

        var w = new Worker(this, v);
        foreach (var c in v.GroupCards) _queue.Enqueue(c.GroupId, priority: false, v.GroupLeafDbInfo.ContainsKey);
        DatLineStore.ConstructionCount = 0;
        w.Drainer.Drain();
        w.Pump();

        Assert.Equal(10, w.Refreshed.Count);                         // one targeted update per Group
        Assert.Equal(10, w.Refreshed.Distinct().Count());
        Assert.Equal(3 + 2 + AmigaLeaves + bigLeaves + 6 * 2, DatLineStore.ConstructionCount);   // every Group leaf exactly once
        AssertConverged(v);
        Assert.All(v.Platforms, p => Assert.False(p.CoveragePending));
        Assert.Equal(bigLeaves, Card(v, "amiga-big").LeafCount);
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Card(v, "amiga-big").Lifecycle);
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Sys(v, "snes").Lifecycle);   // a System whose only material is in a Group
        foreach (var g in v.GroupCards.Select(c => c.GroupId))
            Assert.Equal(LegacyGroupFacts(g), _queue.TryGet(g, out var f) ? f : default);
    }

    // ── 5. Wanted / Unwanted ──────────────────────────────────────────────────

    [Fact]
    public void SingleDat_MarkUnwanted_ThenRestore_RefreshesOnlyItsSystem()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        var others = v.Platforms.Where(p => p.Id != "psx").ToList();
        var cards  = v.GroupCards.ToList();

        MarkUnwanted(PsxCd, 9);                                    // a missing release → unwanted
        DatLineStore.ConstructionCount = 0;
        Assert.True(ConvergeAfterLeafMutation(v, PsxCd));

        Assert.Equal(2, DatLineStore.ConstructionCount);           // psx's two Single DATs — no other System's database
        AssertConverged(v);
        Assert.Equal(3, Sys(v, "psx").Unwanted);
        foreach (var p in others) Assert.Same(p, Sys(v, p.Id));    // every other System keeps its row
        foreach (var c in cards)  Assert.Same(c, Card(v, c.GroupId));
        Assert.All(AllGroups, g => Assert.True(_queue.IsCached(g)));   // no Group recalculated for a Single-DAT mutation

        RestoreWanted(PsxCd, 9);
        Assert.True(ConvergeAfterLeafMutation(v, PsxCd));
        AssertConverged(v);
        Assert.Equal(2, Sys(v, "psx").Unwanted);
    }

    [Fact]
    public void GroupLeaf_MarkUnwanted_GoesPending_ThenConverges_WithoutTouchingOtherGroupsOrSystems()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        var amigaRow  = Sys(v, "amiga");
        var miscCard  = Card(v, GroupMisc);
        int wantedBefore = Card(v, GroupTosec).WantedSum;

        MarkUnwanted(LeafB, 5);
        DatLineStore.ConstructionCount = 0;
        Assert.True(ConvergeAfterLeafMutation(v, LeafB));

        Assert.Equal(1, DatLineStore.ConstructionCount);            // c64's one Single DAT; no Group leaf on the UI path
        AssertConverged(v);
        Assert.True(Card(v, GroupTosec).CoveragePending);           // never the stale figure
        Assert.True(Sys(v, "c64").CoveragePending);
        Assert.Same(amigaRow, Sys(v, "amiga"));
        Assert.True(_queue.IsCached(GroupMisc) && _queue.IsCached(GroupAmiga));   // other Groups keep their aggregate
        Assert.Equal(miscCard, Card(v, GroupMisc));                 // c64's other card: recomposed with the System, same figures
        miscCard = Card(v, GroupMisc);

        // The worker recomputes only the invalidated Group; its completion is a targeted update.
        var w = new Worker(this, v);
        _queue.Enqueue(GroupTosec, priority: true, v.GroupLeafDbInfo.ContainsKey);
        DatLineStore.ConstructionCount = 0;
        w.Drainer.Drain();
        w.Pump();

        Assert.Equal(new[] { GroupTosec }, w.Computed);
        Assert.Equal(new[] { GroupTosec }, w.Refreshed);
        Assert.Equal(3, DatLineStore.ConstructionCount);            // the Group's three leaves, nothing else
        AssertConverged(v);
        Assert.Equal(wantedBefore - 1, Card(v, GroupTosec).WantedSum);
        Assert.Same(miscCard, Card(v, GroupMisc));
        Assert.Same(amigaRow, Sys(v, "amiga"));
    }

    [Fact]
    public void RapidSuccessiveMutations_NeverShowAStaleAggregate_AndConvergeOnTheLastState()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();

        // The worker's compute reads the leaf databases when it STARTS and delivers later — as the real off-thread one does.
        var inFlight = new List<TaskCompletionSource<GroupStorageFacts>>();
        var readAtStart = new List<GroupStorageFacts>();
        var w = new Worker(this, v, leaves =>
        {
            readAtStart.Add(GroupCoverageLoader.Compute(leaves, _dataDir, _catalog));
            var tcs = new TaskCompletionSource<GroupStorageFacts>();
            inFlight.Add(tcs);
            return tcs.Task;
        });

        MarkUnwanted(LeafA, 4);                                     // mutation 1
        Assert.True(ConvergeAfterLeafMutation(v, LeafA));
        _queue.Enqueue(GroupTosec, priority: true, v.GroupLeafDbInfo.ContainsKey);
        w.Drainer.Drain();                                          // compute #1 starts (has read state 1)
        AssertConverged(v);

        MarkUnwanted(LeafA, 5);                                     // mutation 2 while #1 is in flight
        Assert.True(ConvergeAfterLeafMutation(v, LeafA));
        _queue.Enqueue(GroupTosec, priority: true, v.GroupLeafDbInfo.ContainsKey);   // refused: still in flight
        AssertConverged(v);
        Assert.True(Card(v, GroupTosec).CoveragePending);

        MarkUnwanted(LeafC, 6);                                     // mutation 3, another leaf of the same Group
        Assert.True(ConvergeAfterLeafMutation(v, LeafC));
        AssertConverged(v);

        inFlight[0].SetResult(readAtStart[0]);                      // the obsolete result arrives
        w.PumpOnce();
        Assert.False(_queue.IsCached(GroupTosec));                  // rejected by the generation token
        Assert.True(Card(v, GroupTosec).CoveragePending);           // still pending — never the stale figure
        AssertConverged(v);
        Assert.Equal(2, inFlight.Count);                            // …and a fresh compute was started automatically

        inFlight[1].SetResult(readAtStart[1]);
        w.Pump();
        AssertConverged(v);
        Assert.False(Card(v, GroupTosec).CoveragePending);
        Assert.Equal(1 + 2 + 4, LegacyGroupFacts(GroupTosec).Unwanted);   // A: 1+2, C: 3+1 — all three mutations counted
        Assert.Equal(new[] { GroupTosec, GroupTosec }, w.Refreshed);
    }

    [Fact]
    public void ManySequentialMutations_AcrossSinglesAndGroups_StayEqualToTheCompleteRefresh()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        var w = new Worker(this, v);

        var steps = new (string Leaf, int Release, bool Unwanted)[]
        {
            (PsxCd, 10, true), (LeafD, 2, true), (C64Single, 6, true), (AmigaLeaf(3), 1, true), (PsxCd, 10, false),
            (NesSingle, 4, true), (LeafD, 2, false), (AmigaLeaf(3), 2, true), (LeafE, 0, true), (C64Single, 6, false),
        };
        foreach (var (leaf, release, unwanted) in steps)
        {
            if (unwanted) MarkUnwanted(leaf, release); else RestoreWanted(leaf, release);
            Assert.True(ConvergeAfterLeafMutation(v, leaf));
            AssertConverged(v);                                      // pending where a Group was invalidated

            foreach (var c in v.GroupCards.Where(c => c.CoveragePending))
                _queue.Enqueue(c.GroupId, priority: true, v.GroupLeafDbInfo.ContainsKey);
            w.Drainer.Drain();
            w.Pump();
            AssertConverged(v);                                      // and again once the worker has caught up
            Assert.All(v.GroupCards, c => Assert.False(c.CoveragePending));
        }
    }

    [Fact]
    public void TargetedSystemRefresh_DoesNotReadOtherSystems()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        var psxBefore = Sys(v, "psx");

        MarkUnwanted(PsxCd, 9);                                     // psx changes on disk…
        Assert.True(v.TryRefreshPlatform("nes", _catalog, _dataDir, _dir, _queue));   // …but only nes is refreshed

        Assert.Same(psxBefore, Sys(v, "psx"));                      // psx was not re-read
        Assert.Equal(2, Sys(v, "psx").Unwanted);

        Assert.True(v.TryRefreshPlatform("psx", _catalog, _dataDir, _dir, _queue));
        AssertConverged(v);
    }

    // ── 6. Assignment changes: Append, Build, Reabsorb, Fillback, Verify, Move, Purge ──

    [Fact]
    public void SingleDat_VolumeWorkflowEffects_NeverLeaveAStaleLifecycle()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        void Check(ArchiveLifecycle expected)
        {
            AssertConverged(Load());                                           // the complete refresh these workflows run
            Assert.True(v.TryRefreshPlatform("nes", _catalog, _dataDir, _dir, _queue));
            AssertConverged(v);                                                // and the targeted one agrees
            Assert.Equal(expected, Sys(v, "nes").Lifecycle);
        }

        Check(ArchiveLifecycle.NeedsVolume);                                   // two present artifacts, no volume

        AddVolume("v-nes", "nes", NesSingle, null, "workspace");               // Build Volume (partial)
        Assign("v-nes", NesSingle, _artifacts[NesSingle][0]);
        Check(ArchiveLifecycle.NeedsVolume);

        Assign("v-nes", NesSingle, _artifacts[NesSingle][1]);                  // Append the rest
        Check(ArchiveLifecycle.ReadyToMove);

        MoveVolume("v-nes", "disk");                                           // Move to disk
        Check(ArchiveLifecycle.Archived);

        AddVolume("v-nes-2", "nes", NesSingle, null, "workspace");             // Fillback: one artifact moves to a LOCAL volume
        var row = _catalog.GetVolumeArtifacts("v-nes").First();
        _catalog.MoveVolumeArtifactToVolume(row.Id, "v-nes", "v-nes-2", 4096);
        Check(ArchiveLifecycle.ReadyToMove);

        _catalog.RemoveVolumeArtifacts("v-nes-2", new List<string> { row.DerivedArtifactId });   // Reabsorb / Verify drops the assignment
        Check(ArchiveLifecycle.NeedsVolume);

        // Purge: the release becomes unwanted, its artifact and remaining assignment disappear.
        var purged = _artifacts[NesSingle].Single(id => id != row.DerivedArtifactId);
        _catalog.RemoveVolumeArtifacts("v-nes", new List<string> { purged });
        Store(NesSingle).DeleteDerivedArtifactRow(purged);
        Store(NesSingle).DeleteDerivedArtifactRow(row.DerivedArtifactId);
        MarkUnwanted(NesSingle, 0);
        MarkUnwanted(NesSingle, 1);
        Check(ArchiveLifecycle.None);
        Assert.Equal(2, Sys(v, "nes").Unwanted);
        Assert.Equal(0, Sys(v, "nes").Present);
    }

    [Fact]
    public void GroupOwned_VolumeWorkflowEffects_ConvergeThroughInvalidationAndTheWorker()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        var w = new Worker(this, v);

        // MainWindow.ConvergeSystemsAfterVolumeMutation(assignmentChanged: true): invalidate, then the complete refresh.
        SystemsViewData AfterGroupVolumeMutation(string groupId, bool assignmentChanged)
        {
            if (assignmentChanged) _queue.Invalidate(groupId);
            v = Load();
            AssertConverged(v);
            w = new Worker(this, v);
            foreach (var c in v.GroupCards.Where(c => c.CoveragePending))
                _queue.Enqueue(c.GroupId, priority: true, v.GroupLeafDbInfo.ContainsKey);
            w.Drainer.Drain();
            w.Pump();
            AssertConverged(v);
            Assert.Equal(LegacyGroupFacts(groupId), _queue.TryGet(groupId, out var f) ? f : default);
            return v;
        }

        Assert.Equal(ArchiveLifecycle.NeedsVolume, Card(v, GroupMisc).Lifecycle);

        AddVolume("v-misc", "c64", null, GroupMisc, "workspace");                      // Build (Group)
        Assign("v-misc", LeafD, _artifacts[LeafD][0]);
        v = AfterGroupVolumeMutation(GroupMisc, assignmentChanged: true);
        Assert.Equal(ArchiveLifecycle.ReadyToMove, Card(v, GroupMisc).Lifecycle);

        MoveVolume("v-misc", "disk");                                                   // Move: location only, no invalidation
        v = AfterGroupVolumeMutation(GroupMisc, assignmentChanged: false);
        Assert.Equal(ArchiveLifecycle.Archived, Card(v, GroupMisc).Lifecycle);
        Assert.Equal(new string[0], w.Computed);                                        // nothing recomputed for a Move

        _catalog.RemoveVolumeArtifacts("v-misc", new List<string> { _artifacts[LeafD][0] });   // Reabsorb / Verify
        v = AfterGroupVolumeMutation(GroupMisc, assignmentChanged: true);
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Card(v, GroupMisc).Lifecycle);
        Assert.Equal(new[] { GroupMisc }, w.Computed);                                  // only the mutated Group
        Assert.Equal(ArchiveLifecycle.NeedsVolume, Sys(v, "c64").Lifecycle);
    }

    // ── 7. Staging, structure, fallbacks ──────────────────────────────────────

    [Fact]
    public void StagingAppearing_IsSeenByTheTargetedSystemRefresh()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        Assert.False(Sys(v, "c64").HasStaging);

        AddStaging(LeafB);
        Assert.True(v.TryRefreshPlatform("c64", _catalog, _dataDir, _dir, _queue));

        AssertConverged(v);
        Assert.True(Sys(v, "c64").HasStaging);
        Assert.True(Card(v, GroupTosec).HasStaging);
        Assert.False(Card(v, GroupMisc).HasStaging);
        Assert.False(Sys(v, "psx").HasStaging);
    }

    [Fact]
    public void StructuralChange_IsRefused_SoTheCallerPerformsTheCompleteRefresh()
    {
        SeedState();
        ComputeAllGroups();
        var v = Load();
        var before = Snapshot(v);

        Assert.False(v.TryRefreshPlatform("no-such-system", _catalog, _dataDir, _dir, _queue));

        // A new DAT line in psx: the targeted update does not cover structure.
        BuildLeafDb("psx-new-dat", 2);
        _catalog.SaveDatLines(new List<DatLineRecord> { Line("psx-new-dat", 2, "redump", "cd") });
        Assert.False(v.TryRefreshPlatform("psx", _catalog, _dataDir, _dir, _queue));
        Assert.Equal(before, Snapshot(v));                                              // refused → nothing changed
        Assert.True(v.TryRefreshPlatform("nes", _catalog, _dataDir, _dir, _queue));    // an untouched System is still fine

        // A new Group in nes.
        CreateGroup(_catalog, "nes-tosec", "NES TOSEC", new[] { ("nes-tosec-one", 2) });
        Assert.False(v.TryRefreshPlatform("nes", _catalog, _dataDir, _dir, _queue));

        // The complete refresh the caller falls back to is, as always, equal to the reference.
        v = Load();
        AssertConverged(v);
        Assert.Equal(3, Sys(v, "psx").DatLines);
        Assert.Contains(v.GroupCards, c => c.GroupId == "nes-tosec" && c.CoveragePending);
    }

    // ── 8. Switching views ────────────────────────────────────────────────────

    [Fact]
    public void OpeningSystems_AfterChangesMadeElsewhere_ShowsThem()
    {
        SeedState();
        ComputeAllGroups();
        var shown = Load();

        // Changes made by workflows that do not converge Systems themselves (Library bulk operations, collision review…).
        foreach (var i in new[] { 8, 9, 10 }) MarkUnwanted(PsxCd, i);
        MarkUnwanted(C64Single, 7);
        AddStaging(NesSingle);
        Assign("v-psx-cd", PsxCd, _artifacts[PsxCd][3]);

        // SetActive(NavSystems) → RefreshSystemsKeepSelection → the complete refresh.
        DatLineStore.ConstructionCount = 0;
        var reopened = Load();
        Assert.Equal(4, DatLineStore.ConstructionCount);            // the Single DATs only — no Group leaf, no complete map
        AssertConverged(reopened);
        Assert.NotEqual(Snapshot(shown), Snapshot(reopened));
        Assert.Equal(5, Sys(reopened, "psx").Unwanted);
        Assert.True(Sys(reopened, "nes").HasStaging);
        Assert.All(AllGroups, g => Assert.True(_queue.IsCached(g)));   // navigation recalculates no Group
        Assert.All(reopened.GroupCards, c => Assert.False(c.CoveragePending));
    }

    // ── 9. MainWindow wiring ──────────────────────────────────────────────────

    [Fact]
    public void SystemsPaths_NeverLoadTheCompleteAssignmentMap()
    {
        Assert.DoesNotContain("GetAllAssignedDerivedIdsByDatLine", RepoFile("MainWindow.axaml.cs"));
        Assert.DoesNotContain("GetAllAssignedDerivedIdsByDatLine()", RepoFile("Systems/SystemsViewData.cs"));
        Assert.DoesNotContain("GetAllAssignedDerivedIdsByDatLine", RepoFile("Systems/GroupCoverageDrainer.cs"));
    }

    [Fact]
    public void Worker_IsWiredToTheTargetedGroupUpdate_AndTheTargetedAssignments()
    {
        var main   = RepoFile("MainWindow.axaml.cs");
        var wiring = Region(main, "private Systems.GroupCoverageDrainer GroupCoverageDrainerInstance", "Activities.ActivityMonitor.Shared);");
        Assert.Contains("Systems.GroupCoverageLoader.Compute(leaves, _dataDir, _catalog)),", wiring);
        Assert.Contains("RefreshSystemsAfterGroupCoverage,", wiring);
        Assert.DoesNotContain("RefreshSystemsKeepSelection", wiring);

        // The completion handler: targeted update first; the complete refresh only as the unknown-Group fallback; no
        // catalog or leaf-database access of its own, and it never changes the selection.
        var handler = Region(main, "private void RefreshSystemsAfterGroupCoverage(string groupId)", "private void RefreshSystemPlatformKeepSelection(");
        Assert.Contains("_systemsView?.TryApplyGroupCoverage(groupId, _groupCoverage)", handler);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(handler, @"RefreshSystemsKeepSelection\("));
        foreach (var forbidden in new[] { "_catalog.", "new DatLineStore", "ReloadSystemsData", "SelectPlatform(", "SelectDatLine(",
                                          "_selectedGroupId = ", "_selectedDatLine = ", "_selectedPlatformId = ", "BuildDatLineInfos(" })
            Assert.DoesNotContain(forbidden, handler);
    }

    [Fact]
    public void ReleaseMutation_RefreshesOnlyItsSystem_WithTheCompleteRefreshAsFallback()
    {
        var main = RepoFile("MainWindow.axaml.cs");
        var converge = Region(main, "private void ConvergeSystemsAfterLeafReleaseMutation(string datLineId, string platformId)", "private void SelectDatLine(");
        Assert.Contains("InvalidateGroupCoverage(groupId);", converge);
        Assert.Contains("RefreshSystemPlatformKeepSelection(platformId, groupId);", converge);
        Assert.DoesNotContain("RefreshSystemsKeepSelection(", converge);

        var scoped = Region(main, "private void RefreshSystemPlatformKeepSelection(string platformId, string? invalidatedGroupId)", "private bool ReplaceSystemsTreeNode(");
        Assert.Contains("view.TryApplyGroupCoverage(invalidatedGroupId, _groupCoverage) is not null", scoped);
        Assert.Contains("view.TryRefreshPlatform(platformId, _catalog, _dataDir, AppContext.BaseDirectory, _groupCoverage)", scoped);
        Assert.Contains("if (!targeted) ReloadSystemsData();", scoped);
        Assert.Contains("RestoreSystemsSelection(platformId, keepDatId);", scoped);
    }

    [Fact]
    public void OtherWorkflows_AndNavigation_StillPerformTheCompleteRefresh()
    {
        var main = RepoFile("MainWindow.axaml.cs");

        var volume = Region(main, "private void ConvergeSystemsAfterVolumeMutation(VolumeEntry entry, bool assignmentChanged)", "/// <summary>");
        Assert.Contains("InvalidateGroupCoverage(entry.GroupId!);", volume);
        Assert.Contains("RefreshSystemsKeepSelection(entry.PlatformId);", volume);

        var nav = Region(main, "if (btn == NavSystems)", "if (btn == NavLibrary)");
        Assert.Contains("RefreshSystemsKeepSelection(_selectedPlatformId);", nav);

        var full = Region(main, "private void RefreshSystemsKeepSelection(string? platformId)", "private void RestoreSystemsSelection(");
        Assert.Contains("ReloadSystemsData();", full);

        var reload = Region(main, "private void ReloadSystemsData()", "private void RefreshSystemsAfterGroupCoverage(");
        Assert.Contains("Systems.SystemsViewData.Load(_catalog, _dataDir, AppContext.BaseDirectory, _groupCoverage)", reload);
    }
}

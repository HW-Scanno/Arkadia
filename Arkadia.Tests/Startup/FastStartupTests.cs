using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Pending;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Startup;

/// <summary>
/// Runs after the parallel phase, on its own: these tests create many databases and really scan them on other threads,
/// and they must not change how the other test classes are scheduled against each other.
/// </summary>
[CollectionDefinition("FastStartup", DisableParallelization = true)]
public sealed class FastStartupCollection { }

/// <summary>
/// PERFORMANCE-03 — fast startup. The Volumes view takes its artifact counts from one aggregate query instead of
/// materializing every volume_artifacts record (<see cref="VolumeEntriesBuilder"/>), and the Pending scan of every leaf
/// database no longer runs at startup: it runs on demand, off the UI thread, single-flight
/// (<see cref="PendingItemsLoader"/> + <see cref="PendingLoadState"/>).
///
/// <para>The correctness references are <see cref="LegacyRefreshVolumes"/> and <see cref="LegacyBuildPendingItems"/>:
/// the data halves of MainWindow.RefreshVolumes / BuildPendingItems exactly as they were before this milestone. Never
/// "fix" them to follow the new code. Real <see cref="CatalogService"/> and real leaf databases over a temp directory —
/// never the runtime data.</para>
/// </summary>
[Collection("FastStartup")]
public sealed class FastStartupTests : IDisposable
{
    private const string C64Single  = "c64-nointro-cart";
    private const string PsxCd      = "psx-redump-cd";
    private const string PsxNoStore = "psx-nostore";
    private const string PsxMissing = "psx-missingdb";
    private const string Group      = "c64-tosec";
    private const string LeafA      = "c64-tosec-games-d64";
    private const string LeafB      = "c64-tosec-demos-d64";
    private const string LeafC      = "c64-tosec-apps-d64";

    private static readonly string[] ExistingLeaves = [C64Single, PsxCd, LeafA, LeafB, LeafC];

    private readonly string         _dir;
    private readonly string         _dataDir;
    private readonly CatalogService _catalog;
    private readonly Dictionary<string, List<string>> _artifacts = new(StringComparer.Ordinal);

    public FastStartupTests()
    {
        _dir     = Path.Combine(Path.GetTempPath(), "ArkPerf03_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataDir);
        _catalog = Seed();
        SeedVolumes();
        SeedPending();
    }

    public void Dispose()
    {
        // Release ONLY this fixture's pooled connections, so its directory can really be deleted. Never ClearAllPools():
        // other test classes run in parallel and must keep their own pooled connections.
        try
        {
            foreach (var dl in _catalog.LoadDatLines())
                if (dl.DataStorePath.Length > 0)
                    ReleasePool(Path.Combine(_dataDir, dl.DataStorePath));
            ReleasePool(Path.Combine(_dataDir, "catalog.db"));
            ReleasePool(Path.Combine(_dir, "empty", "data", "catalog.db"));
        }
        catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void ReleasePool(string dbPath)
        => Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"));

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static string PlatformOf(string datLineId) => datLineId[..datLineId.IndexOf('-')];
    private static string RelPath(string datLineId)    => $"systems/{PlatformOf(datLineId)}/{datLineId}.db";
    private string        DbPath(string datLineId)     => Path.Combine(_dataDir, RelPath(datLineId));
    private DatLineStore  Store(string datLineId)      => new(DbPath(datLineId));

    private static string Sha1Of(params object[] parts)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes(string.Join('/', parts)))).ToLowerInvariant();

    private static List<DatParser.ParsedGame> Games(string prefix, int n) =>
        Enumerable.Range(0, n).Select(i => new DatParser.ParsedGame
        {
            Name = $"{prefix} Game {i:D2}",
            Roms = new List<DatParser.ParsedRom>
            {
                new() { Name = $"{prefix}-{i:D2}.bin", Size = "1", Sha1 = Sha1Of(prefix, i) },
                new() { Name = $"{prefix}-{i:D2}.cue", Size = "2", Sha1 = Sha1Of(prefix, i, "cue") },
            },
        }).ToList();

    private static DatLineRecord Line(string id, int releases, string authority, string media, bool withStore = true) => new()
    {
        Id = id, HardwareFamilyId = PlatformOf(id), Name = media, Authority = authority, MediaTypeId = media,
        Version = "1", DataStorePath = withStore ? RelPath(id) : "", ReleaseCount = releases,
        ImportedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    private void BuildLeafDb(string id, int releases)
        => LeafDatDatabaseBuilder.Build(DbPath(id), id, Games(id, releases), null, CancellationToken.None);

    /// <summary>
    /// Two Systems. c64: one Single DAT + one Group with three leaves. psx: one Single DAT, one DAT line without a
    /// store and one whose database file is missing. Named authorities, so labels differ from raw ids.
    /// </summary>
    private CatalogService Seed()
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" },
            new() { Id = "psx", Name = "PlayStation",  Manufacturer = "Sony",      HardwareTypeId = "" },
        });
        c.SaveAuthority(new AuthorityRecord { Id = "nointro", Name = "No-Intro" });
        c.SaveAuthority(new AuthorityRecord { Id = "tosec",   Name = "TOSEC" });

        foreach (var (id, n) in new[] { (C64Single, 6), (PsxCd, 8) }) BuildLeafDb(id, n);
        c.SaveDatLines(new List<DatLineRecord>
        {
            Line(C64Single, 6, "nointro", "cartridge"),
            Line(PsxCd, 8, "redump", "cd"),                           // authority without a name row
            Line(PsxNoStore, 4, "nointro", "rom", withStore: false),
            Line(PsxMissing, 3, "nointro", "other"),                   // store path set, database file absent
        });

        var leaves = new List<GroupDatCatalogLeafCreate>();
        int n2 = 0;
        foreach (var id in new[] { LeafA, LeafB, LeafC })
        {
            n2++;
            BuildLeafDb(id, 5);
            leaves.Add(new GroupDatCatalogLeafCreate
            {
                DatLine = Line(id, 5, "tosec", "floppy"),
                RelativeDatPath = $"Set{n2:D3}/g.dat", SourceDatName = "g.dat",
                SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
            });
        }
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = Group, DisplayName = "Commodore 64 TOSEC", HardwareFamilyId = "c64", Authority = "tosec", Leaves = leaves,
        });
        return c;
    }

    private List<string> PresentArtifacts(string leafId, int count)
    {
        var store = Store(leafId);
        var rel   = store.LoadReleases();
        var ids   = _artifacts[leafId] = [];
        for (int i = 0; i < count; i++)
        {
            store.UpdateReleaseStatus(rel[i].Id, "present");
            ids.Add(store.IngestDerivedArtifact(
                "sha1:" + Sha1Of(leafId, i, "cik"), "", "chd", $"{leafId}-{i}.chd", $"archive/x/{leafId}/{leafId}-{i}.chd",
                4096, Sha1Of(leafId, i, "derived")));
        }
        return ids;
    }

    private void AddVolume(string id, string platformId, string? datLineId, string? groupId,
        string status = "present", string health = "ok", long actual = 0)
        => _catalog.SaveVolume(new VolumeRecord
        {
            Id = id, Label = id.ToUpperInvariant() + " label", PlatformId = platformId, DatLineId = datLineId, GroupId = groupId,
            Status = status, Health = health, PlannedSizeBytes = 1_000_000, ActualSizeBytes = actual, CreatedAt = DateTime.UtcNow,
        });

    private void Locate(string volumeId, string locationType, string? diskId = null)
        => _catalog.SetCurrentLocation(new VolumeLocationRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = volumeId, LocationType = locationType, DiskId = diskId,
            IsCurrent = true, CreatedAt = DateTime.UtcNow,
        });

    /// <summary>One volume_artifacts row with an explicit status (the count is of ALL rows, whatever their status).</summary>
    private void Assign(string volumeId, string leafId, string derivedId, string status = "present_in_final")
        => _catalog.SaveVolumeArtifact(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = volumeId, DatLineId = leafId, DerivedArtifactId = derivedId,
            ContentIdentityKey = "", Status = status, AddedAtUtc = DateTime.UtcNow,
        });

    /// <summary>
    /// Volumes in every shape the Volumes view shows: Single on a known disk (assignments in several statuses), Single
    /// in the workspace, Single with no assignment and no location, Group-owned across two leaves, lost + critical on
    /// a disk location without a disk id, owned by a DAT line without a store, a "source" location, an id differing only by case, and
    /// assignment rows that belong to no volume at all.
    /// </summary>
    private void SeedVolumes()
    {
        var now = DateTime.UtcNow;
        _catalog.SaveDisk(new DiskRecord
        {
            Id = "disk-1", Label = "ARK-DISK-01", Status = "assigned", Family = "core",
            DeclaredCapacityBytes = 4_000_000_000_000, CreatedAt = now, UpdatedAt = now,
        });

        var c64 = PresentArtifacts(C64Single, 4);
        var psx = PresentArtifacts(PsxCd, 3);
        var a   = PresentArtifacts(LeafA, 2);
        var b   = PresentArtifacts(LeafB, 3);

        AddVolume("v-single-disk", "c64", C64Single, null, actual: 12_288);
        Locate("v-single-disk", "disk", "disk-1");
        Assign("v-single-disk", C64Single, c64[0]);
        Assign("v-single-disk", C64Single, c64[1], status: "planned");
        Assign("v-single-disk", C64Single, c64[2], status: "missing");

        AddVolume("v-single-local", "psx", PsxCd, null, actual: 8_192);
        Locate("v-single-local", "workspace");
        Assign("v-single-local", PsxCd, psx[0]);
        Assign("v-single-local", PsxCd, psx[1]);

        AddVolume("v-empty", "psx", PsxCd, null, status: "init");                    // no assignment, no location

        AddVolume("v-group", "c64", null, Group, actual: 20_480);
        Locate("v-group", "workspace");
        foreach (var da in a) Assign("v-group", LeafA, da);
        foreach (var da in b) Assign("v-group", LeafB, da);

        AddVolume("v-lost", "c64", C64Single, null, status: "lost", health: "crit");
        Locate("v-lost", "disk");                                                     // a disk location without a disk id
        Assign("v-lost", C64Single, c64[3]);

        AddVolume("v-nostore", "psx", PsxNoStore, null);                              // owner has no leaf database
        Locate("v-nostore", "source");

        AddVolume("V-SINGLE-DISK", "psx", PsxCd, null);                               // differs from v-single-disk only by case
        Locate("V-SINGLE-DISK", "workspace");
        Assign("V-SINGLE-DISK", PsxCd, psx[2]);

        Assign("v-ghost", PsxCd, psx[0]);                                             // rows of a volume that does not exist
        Assign("v-ghost", PsxCd, psx[1]);
    }

    private PendingReconciliationRecord Recon(string id, string newReleaseId, string outdatedReleaseId, int minute,
        string status = "pending", bool withLocator = false) => new()
    {
        Id = id, NewReleaseId = newReleaseId, OutdatedReleaseId = outdatedReleaseId,
        ArtifactId         = withLocator ? "da-" + id : null,
        VolumeId           = withLocator ? "v-single-disk" : null,
        DiskId             = withLocator ? "disk-1" : null,
        StoredRelativePath = withLocator ? "old/path" : null,
        StoredName         = withLocator ? "old-name.chd" : null,
        TargetName         = "target-" + id + ".chd",
        TargetRelativePath = withLocator ? "new/path" : null,
        Reason = "content_hash_match", Status = status,
        CreatedAtUtc = new DateTime(2026, 2, 1, 10, minute, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// Pending rows in a Single DAT and in two Group leaves: with and without a physical locator, with and without an
    /// existing outdated release, one orphan (its new release does not exist — never listed), and rows that are not
    /// pending any more (applied / cancelled — never listed). LeafC, psx-cd and the store-less lines have none.
    /// </summary>
    private void SeedPending()
    {
        var c64 = Store(C64Single); var rc = c64.LoadReleases();
        c64.SavePendingReconciliation(Recon("c64-late",    rc[2].Id, rc[3].Id, minute: 30, withLocator: true));
        c64.SavePendingReconciliation(Recon("c64-early",   rc[0].Id, rc[1].Id, minute: 5));
        c64.SavePendingReconciliation(Recon("c64-noold",   rc[4].Id, "release-that-is-gone", minute: 10));
        c64.SavePendingReconciliation(Recon("c64-orphan",  "release-that-never-existed", rc[1].Id, minute: 15));
        c64.SavePendingReconciliation(Recon("c64-applied", rc[5].Id, rc[1].Id, minute: 20, status: "applied"));

        var la = Store(LeafA); var ra = la.LoadReleases();
        la.SavePendingReconciliation(Recon("leafa-one", ra[0].Id, ra[1].Id, minute: 7, withLocator: true));

        var lb = Store(LeafB); var rb = lb.LoadReleases();
        lb.SavePendingReconciliation(Recon("leafb-cancelled", rb[0].Id, rb[1].Id, minute: 8, status: "cancelled"));
        lb.SavePendingReconciliation(Recon("leafb-one",       rb[2].Id, rb[3].Id, minute: 9));
    }

    // ── References: the pre-milestone data halves, verbatim ──────────────────

    private static string LocTypeDisplay(string locType) => locType switch
    {
        "workspace" => "Local Archive",
        _           => locType,
    };

    /// <summary>MainWindow.RefreshVolumes' data half before PERFORMANCE-03 — one GetVolumeArtifacts(...).Count per volume.</summary>
    private List<VolumeEntry> LegacyRefreshVolumes()
    {
        var disks    = _catalog.GetDisks().ToDictionary(d => d.Id, d => d.Label, StringComparer.Ordinal);
        var datLines = _catalog.LoadDatLines().ToDictionary(dl => dl.Id, dl => dl, StringComparer.Ordinal);
        var groupNames = _catalog.LoadDatGroups().ToDictionary(g => g.Id.Value, g => g.DisplayName, StringComparer.OrdinalIgnoreCase);
        var authorityNames = _catalog.LoadAuthorities().ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        var mediaTypeNames = _catalog.GetMediaTypes().ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);

        return _catalog.GetVolumes().Select(v =>
        {
            var loc = _catalog.GetCurrentLocation(v.Id);
            string locLabel = loc is null ? "—"
                : loc.LocationType == "disk" && loc.DiskId is not null && disks.TryGetValue(loc.DiskId, out var dl)
                    ? $"disk: {dl}"
                : LocTypeDisplay(loc.LocationType);

            DatLineRecord? dlRecord = null;
            if (v.DatLineId is not null) datLines.TryGetValue(v.DatLineId, out dlRecord);
            var dbPath = dlRecord?.DataStorePath.Length > 0
                ? Path.Combine(_dataDir, dlRecord.DataStorePath)
                : "";
            var collectionLabel = v.GroupId is not null
                ? VolumeCollectionLabel.ForGroup(
                    groupNames.TryGetValue(v.GroupId, out var gn) ? gn : null, v.GroupId)
                : VolumeCollectionLabel.ForSingle(
                    dlRecord is not null && authorityNames.TryGetValue(dlRecord.Authority, out var an) ? an : null,
                    dlRecord is not null && mediaTypeNames.TryGetValue(dlRecord.MediaTypeId, out var mn) ? mn : null,
                    dlRecord?.Name, v.DatLineId ?? "",
                    dlRecord?.DisplayName);

            return new VolumeEntry
            {
                Id               = v.Id,
                Label            = v.Label,
                PlatformId       = v.PlatformId,
                CollectionLabel  = collectionLabel,
                RawDatLineId     = v.DatLineId,
                GroupId          = v.GroupId,
                DbPath           = dbPath,
                Status           = v.Status,
                Health           = v.Health,
                PlannedSizeBytes = v.PlannedSizeBytes,
                ActualSizeBytes  = v.ActualSizeBytes,
                CurrentLocation  = locLabel,
                DiskId           = loc?.DiskId,
                DiskLabel        = loc?.DiskId is not null && disks.TryGetValue(loc.DiskId, out var diskName)
                    ? diskName : null,
                ArtifactCount    = _catalog.GetVolumeArtifacts(v.Id).Count,
            };
        }).ToList();
    }

    /// <summary>MainWindow.BuildPendingItems before PERFORMANCE-03 (it ran synchronously at startup).</summary>
    private List<PendingItem> LegacyBuildPendingItems(Func<DatLineRecord, string> datLineLabel)
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

            var platformName  = allPlatforms.FirstOrDefault(p => p.Id == dl.HardwareFamilyId)?.Name ?? dl.HardwareFamilyId;
            var relById       = store.LoadReleases().ToDictionary(r => r.Id);
            var filesByRelId  = store.LoadAllReleaseFiles();

            foreach (var row in rows)
            {
                relById.TryGetValue(row.NewReleaseId,      out var newRelease);
                relById.TryGetValue(row.OutdatedReleaseId, out var outdatedRelease);
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
                    DatLineName        = datLineLabel(dl),
                    PlatformId         = dl.HardwareFamilyId,
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

    /// <summary>The label the window resolves: Group leaves "Authority · MEDIA", Single DATs their display name.</summary>
    private Func<DatLineRecord, string> Label()
    {
        var groupLeafIds   = new HashSet<string>(_catalog.GetLeavesForGroup(Group).Select(l => l.DatLine.Id), StringComparer.OrdinalIgnoreCase);
        var authorityNames = _catalog.LoadAuthorities().ToDictionary(a => a.Id, a => a.Name);
        string Authority(string id) => id.Length == 0 ? id : authorityNames.TryGetValue(id, out var n) ? n : id;
        return dl => groupLeafIds.Contains(dl.Id)
            ? $"{Authority(dl.Authority)} · {dl.MediaTypeId.ToUpperInvariant()}"
            : DatLineDisplayName.Resolve(dl.DisplayName, Authority(dl.Authority), dl.MediaTypeId);
    }

    // ── Comparison ────────────────────────────────────────────────────────────

    private static string Describe(VolumeEntry v) => string.Join(" | ",
        v.Id, v.Label, v.PlatformId, v.CollectionLabel, v.RawDatLineId ?? "<null>", v.GroupId ?? "<null>", v.DbPath,
        v.Status, v.Health, v.PlannedSizeBytes, v.ActualSizeBytes, v.CurrentLocation, v.DiskId ?? "<null>",
        v.DiskLabel ?? "<null>", "artifacts=" + v.ArtifactCount,
        // everything the Volumes view derives from the entry
        v.StatusLabel, v.IsGroupOwned, v.CanMoveVolume, v.CanAppendVolume, v.CanReabsorbVolume,
        v.CanVerifyVolume(true), v.CanVerifyVolume(false), v.CanFillbackVolume(true), v.CanFillbackVolume(false),
        v.PlannedLabel, v.ActualLabel, v.FillRatio.ToString("R"));

    private static string Describe(ReleaseRecord? r)
        => r is null ? "<null>" : string.Join("/", r.Id, r.Name, r.Status, r.Region, r.Format, r.Size);

    private static string Describe(IReadOnlyList<ReleaseFileRecord> files)
        => string.Join(",", files.Select(f => f.Id + ":" + f.RomName + ":" + f.Sha1));

    private static string Describe(PendingItem p) => string.Join(" | ",
        p.ReconId, p.Reason, p.CreatedAtUtc.ToString("o"), p.ReconStatus, p.ArtifactId, p.VolumeId, p.DiskId,
        p.StoredRelativePath, p.StoredName, p.TargetName, p.TargetRelativePath, p.DatLineId, p.DatLineName,
        p.PlatformId, p.PlatformName, Describe(p.NewRelease), Describe(p.NewRomFiles), Describe(p.OutdatedRelease),
        Describe(p.OutdatedRomFiles), p.ReleaseName, p.CreatedLabel, p.ReasonDisplay);

    private void AssertVolumesEqualLegacy()
        => Assert.Equal(
            LegacyRefreshVolumes().Select(Describe).ToArray(),
            VolumeEntriesBuilder.Build(_catalog, _dataDir, LocTypeDisplay).Select(Describe).ToArray());

    private void AssertPendingEqualsLegacy(List<PendingItem> actual)
        => Assert.Equal(
            LegacyBuildPendingItems(Label()).Select(Describe).ToArray(),
            actual.Select(Describe).ToArray());

    // ── Volumes ───────────────────────────────────────────────────────────────

    [Fact]
    public void Fixture_CoversEveryVolumeShape()
    {
        var byId = LegacyRefreshVolumes().ToDictionary(v => v.Id, StringComparer.Ordinal);
        Assert.Equal(7, byId.Count);
        Assert.Equal(3, byId["v-single-disk"].ArtifactCount);                  // every status counts
        Assert.Equal("disk: ARK-DISK-01", byId["v-single-disk"].CurrentLocation);
        Assert.Equal("ON DISK", byId["v-single-disk"].StatusLabel);
        Assert.Equal(2, byId["v-single-local"].ArtifactCount);
        Assert.Equal("LOCAL", byId["v-single-local"].StatusLabel);
        Assert.Equal(0, byId["v-empty"].ArtifactCount);
        Assert.Equal("—", byId["v-empty"].CurrentLocation);
        Assert.Equal("INIT", byId["v-empty"].StatusLabel);
        Assert.Equal(5, byId["v-group"].ArtifactCount);
        Assert.True(byId["v-group"].IsGroupOwned);
        Assert.Equal("", byId["v-group"].DbPath);
        Assert.Equal(1, byId["v-lost"].ArtifactCount);
        Assert.Equal("LOST", byId["v-lost"].StatusLabel);
        Assert.Equal("disk", byId["v-lost"].CurrentLocation);                    // no disk id → raw location type
        Assert.Null(byId["v-lost"].DiskLabel);
        Assert.Equal("", byId["v-nostore"].DbPath);
        Assert.Equal("source", byId["v-nostore"].CurrentLocation);
        Assert.Equal(1, byId["V-SINGLE-DISK"].ArtifactCount);                  // not merged with v-single-disk
    }

    [Fact]
    public void Volumes_AreIdenticalToThePreMilestoneRefresh()
        => AssertVolumesEqualLegacy();

    [Fact]
    public void AggregateCount_EqualsTheMaterializedCount_ForEveryVolume()
    {
        var counts = _catalog.GetArtifactCountsByVolume();
        foreach (var v in _catalog.GetVolumes())
            Assert.Equal(_catalog.GetVolumeArtifacts(v.Id).Count, counts.TryGetValue(v.Id, out var n) ? n : 0);

        Assert.False(counts.ContainsKey("v-empty"));        // a volume without assignments has no aggregate row → 0
        Assert.Equal(2, counts["v-ghost"]);                 // rows of a non-existent volume: aggregated, never shown
        Assert.DoesNotContain(VolumeEntriesBuilder.Build(_catalog, _dataDir, LocTypeDisplay), v => v.Id == "v-ghost");
        Assert.Equal(StringComparer.Ordinal, counts.Comparer);   // ids match exactly, like `WHERE volume_id = $vid`
    }

    [Fact]
    public void Volumes_StayIdentical_AfterEveryKindOfAssignmentChange()
    {
        var c64 = _artifacts[C64Single];
        var psx = _artifacts[PsxCd];

        // Append / Build: verified copy → assignment row + size increment.
        _catalog.AddVolumeArtifactAndIncrementSize(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = "v-empty", DatLineId = PsxCd, DerivedArtifactId = psx[2],
            ContentIdentityKey = "", Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        }, 4096);
        AssertVolumesEqualLegacy();
        Assert.Equal(1, VolumeEntriesBuilder.Build(_catalog, _dataDir, LocTypeDisplay).Single(v => v.Id == "v-empty").ArtifactCount);

        // Verify Volume: a confirmed-missing assignment is removed.
        var stale = _catalog.GetVolumeArtifacts("v-single-disk").First(x => x.Status == "missing");
        _catalog.DeleteVolumeArtifactRow(stale.Id, "v-single-disk", 4096);
        AssertVolumesEqualLegacy();
        Assert.Equal(2, VolumeEntriesBuilder.Build(_catalog, _dataDir, LocTypeDisplay).Single(v => v.Id == "v-single-disk").ArtifactCount);

        // Reabsorb / Fillback source: every assignment leaves the volume → it shows 0 again.
        _catalog.RemoveVolumeArtifacts("v-single-local", new List<string> { psx[0], psx[1] });
        AssertVolumesEqualLegacy();
        Assert.Equal(0, VolumeEntriesBuilder.Build(_catalog, _dataDir, LocTypeDisplay).Single(v => v.Id == "v-single-local").ArtifactCount);

        // Move Volume: the location changes, the count does not.
        Locate("v-group", "disk", "disk-1");
        AssertVolumesEqualLegacy();
        _ = c64;
    }

    [Fact]
    public void Volumes_NoVolume_IsAnEmptyList()
    {
        var dir = Path.Combine(_dir, "empty", "data");
        Directory.CreateDirectory(dir);
        Assert.Empty(VolumeEntriesBuilder.Build(new CatalogService(dir), dir, LocTypeDisplay));
    }

    [Fact]
    public void Volumes_OpenNoLeafDatabase()
    {
        DatLineStore.ConstructionCount = 0;
        _ = VolumeEntriesBuilder.Build(_catalog, _dataDir, LocTypeDisplay);
        Assert.Equal(0, DatLineStore.ConstructionCount);
    }

    // ── Pending: data equivalence ─────────────────────────────────────────────

    [Fact]
    public void Fixture_CoversEveryPendingRowShape()
    {
        var items = LegacyBuildPendingItems(Label());
        // catalog DAT-line order, then creation order; orphan / applied / cancelled rows are never listed
        Assert.Equal(new[] { "c64-early", "c64-noold", "c64-late", "leafa-one", "leafb-one" }, items.Select(i => i.ReconId).ToArray());
        Assert.Equal("No-Intro · CARTRIDGE", items[0].DatLineName);
        Assert.Equal("TOSEC · FLOPPY", items[3].DatLineName);
        Assert.Equal("Commodore 64", items[0].PlatformName);
        Assert.Null(items[1].OutdatedRelease);
        Assert.Empty(items[1].OutdatedRomFiles);
        Assert.Equal(2, items[0].NewRomFiles.Count);
        Assert.Equal("v-single-disk", items[2].VolumeId);
        Assert.Equal("", items[0].VolumeId);
    }

    [Fact]
    public void Pending_IsIdenticalToThePreMilestoneScan()
        => AssertPendingEqualsLegacy(PendingItemsLoader.Load(_catalog, _dataDir, Label()));

    [Fact]
    public async Task Pending_ScannedOffTheUiThread_IsIdentical()
    {
        // As the window does: catalog rows and labels are read before the scan starts; the scan only opens leaf databases.
        var platforms = _catalog.LoadPlatforms();
        var datLines  = _catalog.LoadDatLines();
        var labels    = datLines.ToDictionary(dl => dl.Id, Label(), StringComparer.Ordinal);
        var items = await Task.Run(() => PendingItemsLoader.Load(platforms, datLines, _dataDir, dl => labels[dl.Id]));
        AssertPendingEqualsLegacy(items);
    }

    [Fact]
    public void Pending_NoPendingRow_IsAnEmptyList()
    {
        foreach (var leaf in ExistingLeaves)
        {
            var store = Store(leaf);
            foreach (var row in store.LoadPendingReconciliations("pending"))
                store.UpdatePendingReconciliationStatus(row.Id, "applied");
        }
        Assert.Empty(PendingItemsLoader.Load(_catalog, _dataDir, Label()));
        Assert.Empty(LegacyBuildPendingItems(Label()));
    }

    [Fact]
    public void Pending_Scan_OpensEveryExistingLeafDatabase_WhichIsWhyItLeftTheStartupPath()
    {
        DatLineStore.ConstructionCount = 0;
        _ = PendingItemsLoader.Load(_catalog, _dataDir, Label());
        Assert.Equal(ExistingLeaves.Length, DatLineStore.ConstructionCount);   // one per leaf; store-less / missing lines skipped
    }

    // ── Pending: the load gate ────────────────────────────────────────────────

    [Fact]
    public void Gate_StartsNotReady_AndRunsOneScanAtATime()
    {
        var gate = new PendingLoadState();
        Assert.False(gate.IsReady);
        Assert.False(gate.IsLoading);

        Assert.True(gate.TryBegin(out var token));
        Assert.True(gate.IsLoading);
        Assert.False(gate.TryBegin(out _));            // single flight
        Assert.False(gate.IsReady);                    // not ready while the scan runs

        Assert.Equal(PendingLoadOutcome.Ready, gate.Complete(token, succeeded: true));
        Assert.True(gate.IsReady);
        Assert.False(gate.IsLoading);
        Assert.False(gate.TryBegin(out _));            // current data: no duplicate scan
    }

    [Fact]
    public void Gate_Invalidation_MakesShownDataNotReady_AndAllowsOneNewScan()
    {
        var gate = new PendingLoadState();
        Assert.True(gate.TryBegin(out var t1));
        Assert.Equal(PendingLoadOutcome.Ready, gate.Complete(t1, true));

        gate.Invalidate();
        Assert.False(gate.IsReady);
        Assert.True(gate.TryBegin(out var t2));
        Assert.NotEqual(t1, t2);
        Assert.False(gate.TryBegin(out _));
        Assert.Equal(PendingLoadOutcome.Ready, gate.Complete(t2, true));
        Assert.True(gate.IsReady);
    }

    [Fact]
    public void Gate_ScanOvertakenByAChange_IsStale_NeverReady()
    {
        var gate = new PendingLoadState();
        Assert.True(gate.TryBegin(out var t1));
        gate.Invalidate();                              // an operation changed the data while the scan was reading
        gate.Invalidate();                              // …twice
        Assert.False(gate.TryBegin(out _));             // still one scan at a time

        Assert.Equal(PendingLoadOutcome.Stale, gate.Complete(t1, succeeded: true));
        Assert.False(gate.IsReady);
        Assert.False(gate.IsLoading);

        Assert.True(gate.TryBegin(out var t2));         // the rescan
        Assert.Equal(PendingLoadOutcome.Ready, gate.Complete(t2, true));
    }

    [Fact]
    public void Gate_Failure_IsNotReady_AndTheNextRequestScansAgain()
    {
        var gate = new PendingLoadState();
        Assert.True(gate.TryBegin(out var t1));
        Assert.Equal(PendingLoadOutcome.Failed, gate.Complete(t1, succeeded: false));
        Assert.False(gate.IsReady);
        Assert.True(gate.TryBegin(out var t2));
        Assert.Equal(PendingLoadOutcome.Ready, gate.Complete(t2, true));
    }

    [Fact]
    public void Gate_FailedScanOvertakenByAChange_IsStale()
    {
        var gate = new PendingLoadState();
        Assert.True(gate.TryBegin(out var t1));
        gate.Invalidate();
        Assert.Equal(PendingLoadOutcome.Stale, gate.Complete(t1, succeeded: false));
        Assert.False(gate.IsReady);
    }

    // ── Pending: the window's flow, driven without a window ──────────────────

    /// <summary>
    /// MainWindow's EnsurePendingLoaded / RefreshPending flow over the real gate and the real loader. The scan really
    /// runs on another thread; "the UI thread" is this test, which applies completions when it pumps them.
    /// </summary>
    private sealed class Flow
    {
        private readonly FastStartupTests _t;
        private readonly PendingLoadState _gate = new();
        private readonly Queue<(int Token, Task<List<PendingItem>> Scan)> _running = new();
        public bool ViewVisible;
        public List<PendingItem>? Shown;            // null = the view shows its loading state, never rows
        public int Scans;
        public Action? BeforeScanReads;             // runs on the scan thread, before it reads anything
        public Action? AfterScanReads;              // runs on the scan thread, once it has read everything
        public readonly List<List<PendingItem>> Dropped = [];   // results overtaken by a change: never shown

        public Flow(FastStartupTests t) => _t = t;

        public void Open()  { ViewVisible = true; EnsureLoaded(); }
        public void Leave() => ViewVisible = false;
        public void DataChanged() { _gate.Invalidate(); if (ViewVisible) EnsureLoaded(); }

        private void EnsureLoaded()
        {
            if (!_gate.TryBegin(out var token)) return;
            Shown = null;
            Scans++;
            var platforms = _t._catalog.LoadPlatforms();
            var datLines  = _t._catalog.LoadDatLines();
            var labels    = datLines.ToDictionary(dl => dl.Id, _t.Label(), StringComparer.Ordinal);
            var dataDir   = _t._dataDir;
            var before = BeforeScanReads;
            var after  = AfterScanReads;
            _running.Enqueue((token, Task.Run(() =>
            {
                before?.Invoke();
                var items = PendingItemsLoader.Load(platforms, datLines, dataDir, dl => labels[dl.Id]);
                after?.Invoke();
                return items;
            })));
        }

        /// <summary>Delivers the oldest finished scan to "the UI thread". Returns false when no scan is running.</summary>
        public async Task<bool> PumpAsync()
        {
            if (_running.Count == 0) return false;
            var (token, scan) = _running.Dequeue();
            try { await scan; } catch { }
            switch (_gate.Complete(token, scan.IsCompletedSuccessfully))
            {
                case PendingLoadOutcome.Ready: Shown = scan.Result; break;
                case PendingLoadOutcome.Stale:
                    if (scan.IsCompletedSuccessfully) Dropped.Add(scan.Result);
                    if (ViewVisible) EnsureLoaded();
                    break;
                case PendingLoadOutcome.Failed: break;
            }
            return true;
        }

        public async Task SettleAsync() { while (await PumpAsync()) { } }
    }

    [Fact]
    public async Task Flow_NothingIsScanned_UntilThePendingViewIsOpened()
    {
        var flow = new Flow(this);
        flow.DataChanged();                              // a change while the view was never opened: still no scan
        Assert.Equal(0, flow.Scans);
        Assert.Null(flow.Shown);

        flow.Open();
        Assert.Null(flow.Shown);                         // loading state until the scan is complete
        await flow.SettleAsync();

        Assert.Equal(1, flow.Scans);
        AssertPendingEqualsLegacy(flow.Shown!);
    }

    [Fact]
    public async Task Flow_ReopeningAnUpToDateView_DoesNotScanAgain()
    {
        var flow = new Flow(this);
        flow.Open();
        await flow.SettleAsync();
        var shown = flow.Shown;

        flow.Leave(); flow.Open(); flow.Leave(); flow.Open();
        await flow.SettleAsync();

        Assert.Equal(1, flow.Scans);
        Assert.Same(shown, flow.Shown);
    }

    [Fact]
    public async Task Flow_OpeningTwiceWhileScanning_RunsOneScan()
    {
        var flow = new Flow(this);
        flow.Open(); flow.Leave(); flow.Open();          // second open while the first scan is still running
        await flow.SettleAsync();
        Assert.Equal(1, flow.Scans);
        AssertPendingEqualsLegacy(flow.Shown!);
    }

    [Fact]
    public async Task Flow_ChangeWhileHidden_IsPickedUpByTheNextOpen()
    {
        var flow = new Flow(this);
        flow.Open();
        await flow.SettleAsync();
        flow.Leave();

        // What a DAT update commits while another view is on screen: a new pending row.
        var store = Store(PsxCd); var rel = store.LoadReleases();
        store.SavePendingReconciliation(Recon("psx-new", rel[0].Id, rel[1].Id, minute: 40));
        flow.DataChanged();
        Assert.Equal(1, flow.Scans);                     // hidden: nothing scanned yet

        flow.Open();
        Assert.Null(flow.Shown);                         // the rows from before the change are never shown as current
        await flow.SettleAsync();

        Assert.Equal(2, flow.Scans);
        Assert.Contains(flow.Shown!, i => i.ReconId == "psx-new");
        AssertPendingEqualsLegacy(flow.Shown!);
    }

    [Fact]
    public async Task Flow_ChangeWhileVisible_RescansAtOnce()
    {
        var flow = new Flow(this);
        flow.Open();
        await flow.SettleAsync();

        var store = Store(LeafC); var rel = store.LoadReleases();
        store.SavePendingReconciliation(Recon("leafc-new", rel[0].Id, rel[1].Id, minute: 41));
        flow.DataChanged();
        Assert.Null(flow.Shown);
        await flow.SettleAsync();

        Assert.Equal(2, flow.Scans);
        AssertPendingEqualsLegacy(flow.Shown!);
    }

    [Fact]
    public async Task Flow_ScanThatReadBeforeAChange_IsDropped_AndTheRescanIsShown()
    {
        var flow = new Flow(this);
        using var scanHasRead = new ManualResetEventSlim(false);
        flow.AfterScanReads = scanHasRead.Set;

        flow.Open();                                     // scan #1 reads the databases as they are now
        Assert.True(scanHasRead.Wait(TimeSpan.FromSeconds(60)));
        flow.AfterScanReads = null;

        // The data changes after scan #1 read it and before its result reaches the UI thread.
        var store = Store(PsxCd); var rel = store.LoadReleases();
        store.SavePendingReconciliation(Recon("psx-raced", rel[2].Id, rel[3].Id, minute: 42));
        flow.DataChanged();                              // overtakes scan #1; no second concurrent scan
        Assert.Equal(1, flow.Scans);

        Assert.True(await flow.PumpAsync());             // scan #1 is delivered → stale → dropped → the rescan starts
        Assert.Null(flow.Shown);                         // the overtaken result was never shown
        Assert.DoesNotContain(Assert.Single(flow.Dropped), i => i.ReconId == "psx-raced");   // …and it really was out of date
        Assert.Equal(2, flow.Scans);

        await flow.SettleAsync();
        Assert.Contains(flow.Shown!, i => i.ReconId == "psx-raced");
        AssertPendingEqualsLegacy(flow.Shown!);
    }

    [Fact]
    public async Task Flow_RapidChangesWhileScanning_ConvergeToTheFinalState_OneScanAtATime()
    {
        var flow = new Flow(this);
        flow.Open();
        for (int i = 0; i < 5; i++)
        {
            var store = Store(LeafB); var rel = store.LoadReleases();
            store.SavePendingReconciliation(Recon("burst-" + i, rel[4].Id, rel[1].Id, minute: 45 + i));
            flow.DataChanged();
        }
        await flow.SettleAsync();

        Assert.True(flow.Scans <= 2, $"scans: {flow.Scans}");   // the overtaken one + one rescan, never one per change
        Assert.Equal(5, flow.Shown!.Count(i => i.ReconId.StartsWith("burst-", StringComparison.Ordinal)));
        AssertPendingEqualsLegacy(flow.Shown!);
    }

    [Fact]
    public async Task Flow_FailedScan_ShowsNoRows_AndTheNextOpenRetries()
    {
        var flow = new Flow(this);
        flow.BeforeScanReads = () => throw new InvalidOperationException("leaf database unreadable");
        flow.Open();
        await flow.SettleAsync();
        Assert.Null(flow.Shown);
        Assert.Equal(1, flow.Scans);

        flow.BeforeScanReads = null;
        flow.Leave(); flow.Open();
        await flow.SettleAsync();
        Assert.Equal(2, flow.Scans);
        AssertPendingEqualsLegacy(flow.Shown!);
    }

    // ── The real startup call path (MainWindow cannot be constructed headless: its wiring is pinned by text) ──

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
        return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
    }

    private static readonly string Main = RepoFile("MainWindow.axaml.cs");

    [Fact]
    public void Startup_StillInitializesPendingAndVolumes_InTheConstructor()
    {
        var ctor = Region(Main, "    public MainWindow()\n", "    // ── Systems ──");
        Assert.Contains("InitPending();", ctor);
        Assert.Contains("InitVolumes();", ctor);
    }

    [Fact]
    public void Startup_InitPending_ScansNothing()
    {
        var init = Region(Main, "    private void InitPending()", "    private void RefreshPending()");
        Assert.DoesNotContain("PendingItemsLoader", init);
        Assert.DoesNotContain("EnsurePendingLoaded", init);
        Assert.DoesNotContain("RefreshPending", init.Replace("private void RefreshPending()", ""));
        Assert.DoesNotContain("DatLineStore", init);
    }

    [Fact]
    public void Pending_IsScannedOnlyThroughTheGate_OffTheUiThread_AndShownOnlyWhenReady()
    {
        // one scan call site in the whole window, inside EnsurePendingLoaded
        Assert.Single(Regex.Matches(Main, Regex.Escape("PendingItemsLoader.Load(")));
        var ensure = Region(Main, "    private void EnsurePendingLoaded()", "    private void ShowPendingLoading()");

        int gate    = ensure.IndexOf("if (!_pendingLoad.TryBegin(out var token)) return;", StringComparison.Ordinal);
        int loading = ensure.IndexOf("ShowPendingLoading();", StringComparison.Ordinal);
        int run     = ensure.IndexOf("System.Threading.Tasks.Task.Run(() =>", StringComparison.Ordinal);
        int scan    = ensure.IndexOf("PendingItemsLoader.Load(", StringComparison.Ordinal);
        int post    = ensure.IndexOf("Avalonia.Threading.Dispatcher.UIThread.Post(() =>", StringComparison.Ordinal);
        int done    = ensure.IndexOf("_pendingLoad.Complete(token, t.IsCompletedSuccessfully)", StringComparison.Ordinal);
        Assert.True(gate >= 0 && gate < loading && loading < run && run < scan && scan < post && post < done,
            "gate → loading state → off-thread scan → completion applied on the UI thread");

        // LOADING while it runs, like the Dashboard roll-up
        Assert.Contains("Activities.ActivityMonitor.Shared.Begin(Activities.ActivityKind.Loading, \"pending reconciliations\")", ensure);
        // rows are shown in exactly one place: the Ready outcome
        Assert.Single(Regex.Matches(Main, Regex.Escape("ShowPendingItems(t.Result);")));
        var ready = Region(ensure, "case PendingLoadOutcome.Ready:", "case PendingLoadOutcome.Stale:");
        Assert.Contains("ShowPendingItems(t.Result);", ready);
        Assert.Single(Regex.Matches(Main, @"(?<!void )ShowPendingItems\("));
        // the scan reads no window field and no catalog: rows and labels are read on the UI thread before it starts
        var prepare = Region(ensure, "ShowPendingLoading();", "System.Threading.Tasks.Task.Run(() =>");
        Assert.Contains("platforms = _catalog.LoadPlatforms();", prepare);
        Assert.Contains("datLines  = _catalog.LoadDatLines();", prepare);
        Assert.Contains("labels    = datLines.ToDictionary(dl => dl.Id, DatLineLabel, StringComparer.Ordinal);", prepare);
        // a failure before the scan starts must not leave the gate "loading" forever
        Assert.Contains("_pendingLoad.Complete(token, succeeded: false);", prepare);
        var body = Region(ensure, "System.Threading.Tasks.Task.Run(() =>", "_ = scan.ContinueWith");
        Assert.Contains("PendingItemsLoader.Load(platforms, datLines, dataDir, dl => labels[dl.Id])", body);
        Assert.DoesNotMatch(@"\b_[a-z]\w*", body);
    }

    [Fact]
    public void Pending_RefreshOnlyInvalidates_AndOpeningTheViewBringsItUpToDate()
    {
        var refresh = Region(Main, "    private void RefreshPending()", "    /// <summary>\n    /// Brings the Pending view up to date");
        Assert.Contains("_pendingLoad.Invalidate();", refresh);
        Assert.Contains("if (_activeButton == NavPending)", refresh);
        Assert.DoesNotContain("PendingItemsLoader", refresh);
        Assert.DoesNotContain("DatLineStore", refresh);

        var setActive = Region(Main, "    private void SetActive(Button btn)", "    // ── Analytics ──");
        Assert.Matches(@"if \(btn == NavPending\)\s+EnsurePendingLoaded\(\);", setActive);

        // the loading state never leaves stale rows behind
        var loading = Region(Main, "    private void ShowPendingLoading()", "    private void ShowPendingLoadFailed()");
        Assert.Contains("PendingList.ItemsSource    = null;", loading);
        Assert.Contains("PendingList.IsVisible      = false;", loading);
    }

    [Fact]
    public void Volumes_Refresh_UsesTheBuilder_AndNeverMaterializesAssignmentsToCountThem()
    {
        var refresh = Region(Main, "    private void RefreshVolumes()", "    private void ApplyVolumesFilter()");
        Assert.Contains("VolumeEntriesBuilder.Build(_catalog, _dataDir, LocTypeDisplay)", refresh);
        Assert.DoesNotContain("GetVolumeArtifacts", refresh);
        Assert.Contains("private void InitVolumes() => RefreshVolumes();", Main);

        var builder = RepoFile("Volumes/VolumeEntriesBuilder.cs");
        Assert.Single(Regex.Matches(builder, Regex.Escape("catalog.GetArtifactCountsByVolume()")));
        Assert.DoesNotContain("GetVolumeArtifacts(", builder);
    }
}

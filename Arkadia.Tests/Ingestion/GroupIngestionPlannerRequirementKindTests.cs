using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Requirement classification inside the read-only <see cref="GroupIngestionPlanner"/>: every not-yet-secured file
/// stays an authoritative target with its <see cref="DatFileRequirementKind"/>; only physical targets are indexed and
/// count toward scope; synthesis-only work is routed through resumable finalization; unmatchable and contradictory
/// requirements are surfaced as non-blocking notices instead of being silently presented as ingestible.
/// </summary>
public sealed class GroupIngestionPlannerRequirementKindTests : IDisposable
{
    private readonly string _root, _dataDir, _appRoot;
    private readonly CatalogService _catalog;

    public GroupIngestionPlannerRequirementKindTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "ArkGrpPlanKind_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_root, "data");
        _appRoot = _root;
        Directory.CreateDirectory(_dataDir);
        _catalog = new CatalogService(_dataDir);
        _catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "trs80", Name = "TRS-80", Manufacturer = "Tandy", HardwareTypeId = "" } });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private const string Leaf    = "trs80-tosec-cmd";
    private const string Release = "Store Operating System Initialization v3.5 (1982)(Tandy) [CMD]";

    private static string Sha1Of(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private sealed record Rom(string Name, string Size, string Sha1 = "", string Md5 = "", string Crc = "");

    private static readonly Rom Strint = new("STRINT.CMD", "4864", Sha1Of("strint"));
    private static readonly Rom Store  = new("STORE.DAT", "0");

    private string LeafPath(string id) => Path.Combine(_dataDir, "systems", "trs80", id + ".db");

    private void BuildLeaf(params Rom[] roms)
    {
        var games = new List<DatParser.ParsedGame>
        {
            new()
            {
                Name = Release,
                Roms = roms.Select(x => new DatParser.ParsedRom { Name = x.Name, Size = x.Size, Sha1 = x.Sha1, Md5 = x.Md5, Crc = x.Crc }).ToList(),
            },
        };
        LeafDatDatabaseBuilder.Build(LeafPath(Leaf), Leaf, games, null, CancellationToken.None);

        _catalog.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "trs80-tosec", DisplayName = "TRS-80 TOSEC", HardwareFamilyId = "trs80", Authority = "tosec",
            Leaves = new[]
            {
                new GroupDatCatalogLeafCreate
                {
                    DatLine = new DatLineRecord
                    {
                        Id = Leaf, HardwareFamilyId = "trs80", Name = "other", Authority = "tosec", MediaTypeId = "other",
                        Version = "1", DataStorePath = $"systems/trs80/{Leaf}.db", ReleaseCount = 1, ImportedAtUtc = DateTime.UtcNow,
                    },
                    RelativeDatPath = $"{Leaf}/g.dat", SourceDatName = "g.dat", SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
                },
            },
        });
    }

    private void SetStatus(string status)
    {
        var store = new DatLineStore(LeafPath(Leaf));
        store.UpdateReleaseStatus(store.LoadReleases().Single().Id, status);
    }

    private void WriteStaging(string rom)
    {
        var dir = Path.Combine(_appRoot, "staging", "trs80", Leaf, IngestionPaths.SafeFolderName(Release));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, rom), "x");
    }

    private GroupIngestionPlan Plan()
        => GroupIngestionPlanner.Plan(_catalog, _dataDir, _appRoot, "trs80-tosec", "TRS-80 TOSEC", "trs80",
            _catalog.GetLeavesForGroup("trs80-tosec"), Path.Combine(_root, "incoming"), null);

    private static int IndexedTargetCount(GroupIngestionPlan p)
        => p.AggregateSha1Index.Values.Sum(l => l.Count) + p.AggregateMd5Index.Values.Sum(l => l.Count);

    // ── (14) hashless non-empty requirement: unmatchable, surfaced, never silently "Ready" ─

    [Fact]
    public void HashlessNonEmpty_OnlyWork_IsUnmatchable_NotInScope_Warned()
    {
        BuildLeaf(new Rom("NOHASH.BIN", "12"));

        var p = Plan();

        var t = Assert.Single(p.RequiredTargets);                       // still an authoritative requirement
        Assert.Equal(DatFileRequirementKind.Unmatchable, t.Kind);
        Assert.Equal(0, IndexedTargetCount(p));                          // impossible to match → never indexed
        Assert.Equal(0, p.MissingTargetCount);
        Assert.Equal(1, p.UnsatisfiableTargetCount);
        Assert.Equal(0, p.InScopeLeafCount);                             // not presented as ingestible work
        Assert.Equal(GroupIngestionReadiness.NothingToDo, p.Readiness);
        Assert.Contains(p.Notices, n => n.DatLineId == Leaf
            && n.Severity == GroupPlanNoticeSeverity.Warning && n.Code == GroupPlanNoticeCodes.UnmatchableRequirement);
    }

    [Fact]
    public void HashlessNonEmpty_WithOtherPhysicalWork_StillWarned()
    {
        BuildLeaf(Strint, new Rom("NOHASH.BIN", "12"));

        var p = Plan();

        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);        // the hashed file is real work
        Assert.Equal(1, p.MissingTargetCount);
        Assert.Contains(p.Notices, n => n.Code == GroupPlanNoticeCodes.UnmatchableRequirement);
    }

    // ── synthesizable empty requirements ────────────────────────────────────

    [Fact]  // release made only of an authoritative empty file → resumable finalization, informational notice
    public void SynthesizableOnly_Missing_RoutedAsResumable_InfoNotice()
    {
        BuildLeaf(Store);

        var p = Plan();

        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);
        Assert.Single(p.ResumableReleases);
        var t = Assert.Single(p.RequiredTargets);
        Assert.Equal(DatFileRequirementKind.SynthesizableEmpty, t.Kind);
        Assert.Equal(0, IndexedTargetCount(p));
        Assert.Equal(0, p.MissingTargetCount);
        Assert.Equal(1, p.SynthesizableEmptyTargetCount);
        Assert.Contains(p.Notices, n => n.Severity == GroupPlanNoticeSeverity.Info && n.Code == GroupPlanNoticeCodes.SynthesizableEmpty);
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "staging")));   // planning creates nothing
    }

    [Fact]  // mixed release: hashed file indexed; empty file classified, not indexed; not resumable until hashed file staged
    public void Mixed_HashedIndexed_EmptyClassified()
    {
        BuildLeaf(Strint, Store);

        var p = Plan();

        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);
        Assert.Equal(1, p.MissingTargetCount);
        Assert.Equal(1, p.SynthesizableEmptyTargetCount);
        Assert.Equal(Strint.Name, Assert.Single(p.AggregateSha1Index[Strint.Sha1]).RomName);
        Assert.Equal(1, IndexedTargetCount(p));
        Assert.Empty(p.ResumableReleases);
    }

    [Fact]  // the stuck TRS-80 case: hashed file already staged, empty file missing → resumable, nothing to match
    public void Stuck_HashedStaged_EmptyMissing_IsResumable()
    {
        BuildLeaf(Strint, Store);
        WriteStaging("STRINT.CMD");

        var p = Plan();

        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);
        Assert.Single(p.ResumableReleases);
        var t = Assert.Single(p.RequiredTargets);                        // STRINT.CMD is secured in staging
        Assert.Equal("STORE.DAT", t.Target.RomName);
        Assert.Equal(DatFileRequirementKind.SynthesizableEmpty, t.Kind);
        Assert.Equal(0, IndexedTargetCount(p));
    }

    [Fact]  // (12) a LOST synthesis-only release is never routed to synthesis
    public void SynthesizableOnly_Lost_NotResumable_Warned()
    {
        BuildLeaf(Store);
        SetStatus("lost");

        var p = Plan();

        Assert.Empty(p.ResumableReleases);
        Assert.Equal(GroupIngestionReadiness.NothingToDo, p.Readiness);
        Assert.Contains(p.Notices, n => n.Severity == GroupPlanNoticeSeverity.Warning && n.Code == GroupPlanNoticeCodes.SynthesisNotPermitted);
        Assert.DoesNotContain(p.Notices, n => n.Code == GroupPlanNoticeCodes.SynthesizableEmpty);
    }

    // ── contradictory / hashed zero-byte ────────────────────────────────────

    [Fact]  // (5) size 0 + incompatible hash: never indexed, never synthesized, warned
    public void ContradictoryZeroByte_NotIndexed_Warned()
    {
        var contraSha1 = Sha1Of("not-empty");
        BuildLeaf(Strint, new Rom("CONTRA.DAT", "0", contraSha1));

        var p = Plan();

        Assert.False(p.AggregateSha1Index.ContainsKey(contraSha1));
        Assert.Contains(p.RequiredTargets, t => t.Target.RomName == "CONTRA.DAT" && t.Kind == DatFileRequirementKind.ContradictoryZeroByte);
        Assert.Equal(1, p.UnsatisfiableTargetCount);
        Assert.Contains(p.Notices, n => n.Severity == GroupPlanNoticeSeverity.Warning && n.Code == GroupPlanNoticeCodes.ContradictoryZeroByte);
    }

    [Fact]  // (4)(13) size 0 with the valid empty SHA1 → physical, indexed, and a real empty file matches normally
    public void HashedZeroByte_IndexedAndMatchedNormally()
    {
        BuildLeaf(new Rom("EMPTY.BIN", "0", DatFileRequirement.EmptySha1));

        var p = Plan();

        Assert.Equal(DatFileRequirementKind.Physical, Assert.Single(p.RequiredTargets).Kind);
        Assert.Equal(1, p.MissingTargetCount);
        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);

        var incoming = Path.Combine(_root, "incoming", "EMPTY.BIN");
        Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
        File.WriteAllBytes(incoming, Array.Empty<byte>());
        var m = IngestionCandidateMatcher.HashAndMatch(incoming, p.AggregateSha1Index, p.AggregateMd5Index);
        Assert.Equal("EMPTY.BIN", Assert.Single(m.Targets).RomName);
    }

    [Fact]  // PO-5: a real zero-byte incoming file is not consumed by a hashless size-0 requirement
    public void ZeroByteIncoming_NotConsumedBySynthesizableRequirement()
    {
        BuildLeaf(Strint, Store);
        var p = Plan();

        var incoming = Path.Combine(_root, "incoming", "STORE.DAT");
        Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
        File.WriteAllBytes(incoming, Array.Empty<byte>());
        var m = IngestionCandidateMatcher.HashAndMatch(incoming, p.AggregateSha1Index, p.AggregateMd5Index);

        Assert.True(m.HashSucceeded);
        Assert.Empty(m.Targets);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Arkadia.Archive;
using Arkadia.Data;
using Arkadia.Ingestion;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// M9C1 tests for the read-only <see cref="GroupIngestionPlanner"/>: scope (Single satisfaction rules),
/// readiness barrier, uniform File Handling guard, aggregate SHA1/MD5 index, frozen plan, and — critically —
/// runtime read-only safety (no catalog/leaf-DB writes, no directory creation, missing DB never created).
/// Real CatalogService + real leaf DBs over temp roots; the real runtime is never touched.
/// </summary>
public sealed class GroupIngestionPlannerTests : IDisposable
{
    private readonly string _root, _dataDir, _appRoot;
    private readonly CatalogService _catalog;

    public GroupIngestionPlannerTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "ArkGrpPlan_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_root, "data");
        _appRoot = _root;
        Directory.CreateDirectory(_dataDir);
        _catalog = new CatalogService(_dataDir);
        _catalog.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        { new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" } });
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static string Sha1Of(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public sealed record Rom(string Name, string Sha1, string Md5 = "", string Size = "10");
    public sealed record Rel(string Name, params Rom[] Roms);

    private string LeafPath(string id) => Path.Combine(_dataDir, "systems", "c64", id + ".db");

    // Build a leaf DB from release specs.
    private void BuildLeaf(string id, params Rel[] rels)
    {
        var games = rels.Select(r => new DatParser.ParsedGame
        {
            Name = r.Name,
            Roms = r.Roms.Select(x => new DatParser.ParsedRom { Name = x.Name, Size = x.Size, Sha1 = x.Sha1, Md5 = x.Md5 }).ToList(),
        }).ToList();
        LeafDatDatabaseBuilder.Build(LeafPath(id), id, games, null, CancellationToken.None);
    }

    // Simple: each name → one release with one "<name>.d64" rom whose SHA1 = Sha1Of(name).
    private void BuildLeafSimple(string id, params string[] names)
        => BuildLeaf(id, names.Select(n => new Rel(n, new Rom(n + ".d64", Sha1Of(n)))).ToArray());

    private void RegisterGroup(string groupId, params string[] leafIds)
    {
        var leaves = leafIds.Select(id =>
        {
            // Do NOT construct a store for an un-built leaf — that would create the ghost DB the planner
            // must never create.
            int relCount = File.Exists(LeafPath(id)) ? new DatLineStore(LeafPath(id)).LoadReleases().Count : 0;
            return new GroupDatCatalogLeafCreate
            {
                DatLine = new DatLineRecord
                {
                    Id = id, HardwareFamilyId = "c64", Name = "other", Authority = "tosec", MediaTypeId = "other",
                    Version = "1", DataStorePath = $"systems/c64/{id}.db", ReleaseCount = relCount, ImportedAtUtc = DateTime.UtcNow,
                },
                RelativeDatPath = $"{id}/g.dat", SourceDatName = "g.dat", SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
            };
        }).ToArray();
        _catalog.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        { GroupId = groupId, DisplayName = "G", HardwareFamilyId = "c64", Authority = "tosec", Leaves = leaves });
    }

    private DatLineStore Store(string id) => new(LeafPath(id));

    private void MarkSatisfied(string leafId, string releaseName)
    {
        var store = Store(leafId);
        var rel   = store.LoadReleases().First(r => r.Name == releaseName);
        var ck    = $"release:{rel.Id}";
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = ck, DatSha1 = null, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.IngestDerivedArtifact(ck, "", "no_compression", "x.zip", "archive/x.zip", 10, new string('b', 40));
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = rel.Id, ContentIdentityKey = ck, CreatedAtUtc = DateTime.UtcNow });
        store.UpdateReleaseStatus(rel.Id, "present");
    }

    private void SetStatus(string leafId, string releaseName, string status)
    {
        var store = Store(leafId);
        var rel   = store.LoadReleases().First(r => r.Name == releaseName);
        store.UpdateReleaseStatus(rel.Id, status);
    }

    private void WriteStaging(string leafId, string releaseName, params string[] roms)
    {
        var dir = Path.Combine(_appRoot, "staging", "c64", leafId, IngestionPaths.SafeFolderName(releaseName));
        Directory.CreateDirectory(dir);
        foreach (var r in roms) File.WriteAllText(Path.Combine(dir, r), "x");
    }

    // Registers leaves with explicit release counts WITHOUT opening their DBs (so an intentionally
    // incomplete/corrupt test DB is not repaired by a writable open during setup).
    private void RegisterGroupCounts(string groupId, params (string id, int relCount)[] specs)
    {
        var leaves = specs.Select(s => new GroupDatCatalogLeafCreate
        {
            DatLine = new DatLineRecord
            {
                Id = s.id, HardwareFamilyId = "c64", Name = "other", Authority = "tosec", MediaTypeId = "other",
                Version = "1", DataStorePath = $"systems/c64/{s.id}.db", ReleaseCount = s.relCount, ImportedAtUtc = DateTime.UtcNow,
            },
            RelativeDatPath = $"{s.id}/g.dat", SourceDatName = "g.dat", SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
        }).ToArray();
        _catalog.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        { GroupId = groupId, DisplayName = "G", HardwareFamilyId = "c64", Authority = "tosec", Leaves = leaves });
    }

    private static List<(string Type, string Name, string Sql)> Schema(string dbPath)
    {
        var rows = new List<(string, string, string)>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type, name, COALESCE(sql,'') FROM sqlite_master ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return rows;
    }

    private GroupIngestionPlan Plan(string groupId, string? incomingOverride = null)
        => GroupIngestionPlanner.Plan(_catalog, _dataDir, _appRoot, groupId, "G", "c64",
            _catalog.GetLeavesForGroup(groupId), incomingOverride, null);

    // ── Scope ───────────────────────────────────────────────────────────────

    [Fact]  // (33.1)(34.17) satisfied leaf excluded → NothingToDo
    public void SatisfiedLeaf_NothingToDo()
    {
        BuildLeafSimple("c64-a", "G1"); RegisterGroup("g", "c64-a"); MarkSatisfied("c64-a", "G1");
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.NothingToDo, p.Readiness);
        Assert.Equal(1, p.SatisfiedLeafCount);
        Assert.Equal(0, p.MissingTargetCount);
    }

    [Fact]  // (33.2) missing wanted target included
    public void MissingTarget_InScope_Ready()
    {
        BuildLeafSimple("c64-a", "G1"); RegisterGroup("g", "c64-a");
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);
        Assert.Equal(1, p.InScopeLeafCount);
        var t = Assert.Single(p.RequiredTargets);
        Assert.Equal("c64-a", t.Target.DatLineId);
        Assert.Equal("G1.d64", t.Target.RomName);
    }

    [Fact]  // (33.3) unwanted/outdated do not create RequiredTargets
    public void UnwantedOutdated_NotRequired()
    {
        BuildLeafSimple("c64-a", "Keep", "Veto", "Old");
        RegisterGroup("g", "c64-a");
        SetStatus("c64-a", "Veto", "unwanted");
        SetStatus("c64-a", "Old", "outdated");
        var p = Plan("g");
        Assert.Single(p.RequiredTargets);
        Assert.Equal("Keep.d64", p.RequiredTargets[0].Target.RomName);
    }

    [Fact]  // (33.4) present-without-artifact stays in scope (rebuild permitted)
    public void PresentWithoutArtifact_InScope()
    {
        BuildLeafSimple("c64-a", "G1"); RegisterGroup("g", "c64-a");
        SetStatus("c64-a", "G1", "present");   // present but NO derived artifact
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);
        Assert.Single(p.RequiredTargets);      // its rom is not secured (no staging/source) → required
    }

    [Fact]  // (33.5) partial staging: staged rom excluded, missing rom required, leaf still in scope
    public void PartialStaging_OnlyMissingRequired()
    {
        BuildLeaf("c64-a", new Rel("Multi", new Rom("a.d64", Sha1Of("a")), new Rom("b.d64", Sha1Of("b"))));
        RegisterGroup("g", "c64-a");
        WriteStaging("c64-a", "Multi", "a.d64");   // a already staged; b missing
        var p = Plan("g");
        var t = Assert.Single(p.RequiredTargets);
        Assert.Equal("b.d64", t.Target.RomName);   // only the missing rom
        Assert.Equal(1, p.InScopeLeafCount);
    }

    [Fact]  // (33.6) complete resumable staging → in scope for finalization with zero required targets
    public void ResumableComplete_InScope_NoRequired()
    {
        BuildLeafSimple("c64-a", "G1"); RegisterGroup("g", "c64-a");
        WriteStaging("c64-a", "G1", "G1.d64");   // complete staging; release still 'missing'
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);
        Assert.Empty(p.RequiredTargets);
        var r = Assert.Single(p.ResumableReleases);
        Assert.Equal("c64-a", r.DatLineId);      // leaf-qualified
    }

    // ── Readiness barrier ───────────────────────────────────────────────────

    [Fact]  // (34.8)(37.36) missing leaf DB blocks AND is never created
    public void MissingDb_Blocks_NotCreated()
    {
        BuildLeafSimple("c64-a", "G1");
        RegisterGroup("g", "c64-a", "c64-b");   // c64-b registered but its DB never built
        var ghostPath = LeafPath("c64-b");
        Assert.False(File.Exists(ghostPath));

        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);
        Assert.Contains(p.Diagnostics, d => d.DatLineId == "c64-b" && d.Reason.Contains("missing"));
        Assert.False(File.Exists(ghostPath));   // planning did NOT create it
    }

    [Fact]  // (34.9) unreadable leaf DB blocks
    public void UnreadableDb_Blocks()
    {
        BuildLeafSimple("c64-a", "G1"); RegisterGroup("g", "c64-a");
        // Corrupt the DB: drop pooled connections + sidecars, then write a non-SQLite file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var s in new[] { "-wal", "-shm" }) { var f = LeafPath("c64-a") + s; if (File.Exists(f)) File.Delete(f); }
        File.WriteAllBytes(LeafPath("c64-a"), new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);
        Assert.Contains(p.Diagnostics, d => d.DatLineId == "c64-a" && d.Reason.Contains("unreadable"));
    }

    [Fact]  // (34.10) mixed FileHandling across in-scope leaves blocks
    public void MixedFileHandling_Blocks()
    {
        BuildLeafSimple("c64-a", "A"); BuildLeafSimple("c64-b", "B");
        RegisterGroup("g", "c64-a", "c64-b");
        _catalog.SaveDatLineFileHandling("c64-b", "all_files");   // a=archives_pre_extraction (default), b=all_files
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);
        Assert.Contains(p.Diagnostics, d => d.Reason.Contains("File Handling"));
    }

    [Theory]  // (34.11)(34.12) uniform File Handling accepted
    [InlineData("archives_pre_extraction")]
    [InlineData("all_files")]
    public void UniformFileHandling_Accepted(string fh)
    {
        BuildLeafSimple("c64-a", "A"); BuildLeafSimple("c64-b", "B");
        RegisterGroup("g", "c64-a", "c64-b");
        _catalog.ApplyDatGroupConfiguration("g", new[] { "c64-a", "c64-b" }, fh, "none", null);
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Ready, p.Readiness);
        Assert.Equal(fh, p.FileHandling);
    }

    [Fact]  // (34.13) invalid transform config (release_folder with no folder transform) blocks
    public void InvalidTransformConfig_Blocks()
    {
        BuildLeafSimple("c64-a", "A"); RegisterGroup("g", "c64-a");
        // release_folder pointing at a folder transform id that does not resolve → dispatch config error
        // (matches Single: a release_folder with a bad folder transform fails dispatch).
        _catalog.SaveDatLineTransformStrategy("c64-a", "release_folder", "does-not-exist");
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);
        Assert.Contains(p.Diagnostics, d => d.DatLineId == "c64-a" && d.Reason.Contains("Transform configuration invalid"));
    }

    [Fact]  // (34.14) required tool missing (file_extension mapping → transform whose tool is absent) blocks
    public void MissingTool_Blocks()
    {
        BuildLeafSimple("c64-a", "A"); RegisterGroup("g", "c64-a");
        _catalog.SaveDatLineTransformStrategy("c64-a", "file_extension", null);
        _catalog.SaveExtensionMappings("c64-a", new List<ExtensionTransformMapping>
        { new() { DatLineId = "c64-a", FileExtension = ".d64", IsDiscard = false, TransformId = "zip_file_compression" } });
        var p = Plan("g");   // temp appRoot has no tools/7zip/7zip.exe
        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);
        Assert.Contains(p.Diagnostics, d => d.DatLineId == "c64-a" && d.Reason.Contains("tool missing"));
    }

    [Fact]  // (34.16) one bad leaf blocks the entire Group plan
    public void OneBadLeaf_BlocksGroup()
    {
        BuildLeafSimple("c64-a", "A");   // good, in scope
        RegisterGroup("g", "c64-a", "c64-missing");   // c64-missing has no DB
        var p = Plan("g");
        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);
    }

    // ── Aggregate index ─────────────────────────────────────────────────────

    [Fact]  // (35.18)(35.24) same hash across two leaves → two distinct targets; leaf identity preserved
    public void SameHash_TwoLeaves_TwoTargets()
    {
        BuildLeafSimple("c64-a", "Shared"); BuildLeafSimple("c64-b", "Shared");   // "Shared.d64" hash equal
        RegisterGroup("g", "c64-a", "c64-b");
        var p = Plan("g");
        var hash = Sha1Of("Shared");
        Assert.Equal(2, p.AggregateSha1Index[hash].Count);
        Assert.Contains(p.AggregateSha1Index[hash], t => t.DatLineId == "c64-a");
        Assert.Contains(p.AggregateSha1Index[hash], t => t.DatLineId == "c64-b");
    }

    [Fact]  // (35.19) duplicate hash within one leaf preserves multiple legitimate targets
    public void DuplicateHash_WithinLeaf_MultipleTargets()
    {
        var sha = Sha1Of("dup");
        BuildLeaf("c64-a", new Rel("R1", new Rom("r1.d64", sha)), new Rel("R2", new Rom("r2.d64", sha)));
        RegisterGroup("g", "c64-a");
        var p = Plan("g");
        Assert.Equal(2, p.AggregateSha1Index[sha].Count);   // R1/r1.d64 and R2/r2.d64
    }

    [Fact]  // (35.20)(35.21) DB-satisfied and already-staged targets are absent from the index
    public void SatisfiedAndStaged_AbsentFromIndex()
    {
        BuildLeafSimple("c64-a", "Sat", "Staged", "Need");
        RegisterGroup("g", "c64-a");
        MarkSatisfied("c64-a", "Sat");
        WriteStaging("c64-a", "Staged", "Staged.d64");
        var p = Plan("g");
        Assert.Single(p.RequiredTargets);
        Assert.Equal("Need.d64", p.RequiredTargets[0].Target.RomName);
        Assert.False(p.AggregateSha1Index.ContainsKey(Sha1Of("Sat")));
        Assert.False(p.AggregateSha1Index.ContainsKey(Sha1Of("Staged")));
    }

    [Fact]  // (35.23) deterministic target ordering
    public void DeterministicOrdering()
    {
        BuildLeafSimple("c64-b", "Z"); BuildLeafSimple("c64-a", "Y");
        RegisterGroup("g", "c64-b", "c64-a");
        var p = Plan("g");
        Assert.Equal(new[] { "c64-a", "c64-b" }, p.RequiredTargets.Select(t => t.Target.DatLineId).ToArray());
    }

    [Fact]  // (35.24) MD5 aggregate index preserves leaf identity
    public void Md5Index_PreservesLeafIdentity()
    {
        BuildLeaf("c64-a", new Rel("M", new Rom("m.d64", "", Md5: Sha1Of("m")[..32])));   // md5-only rom
        RegisterGroup("g", "c64-a");
        var p = Plan("g");
        var md5 = Sha1Of("m")[..32];
        Assert.Equal("c64-a", Assert.Single(p.AggregateMd5Index[md5]).DatLineId);
    }

    // ── Frozen plan ─────────────────────────────────────────────────────────

    [Fact]  // (36.25-28) group id / platform / expected leaves / file handling frozen
    public void FrozenPlan_Identity()
    {
        BuildLeafSimple("c64-a", "A"); BuildLeafSimple("c64-b", "B");
        RegisterGroup("g", "c64-a", "c64-b");
        var p = Plan("g");
        Assert.Equal("g", p.GroupId);
        Assert.Equal("c64", p.PlatformId);
        Assert.Equal(new[] { "c64-a", "c64-b" }, p.ExpectedLeafIds.ToArray());
        Assert.Equal("archives_pre_extraction", p.FileHandling);
    }

    // ── Runtime read-only safety ────────────────────────────────────────────

    [Fact]  // (37.31)(37.32) planning writes neither catalog nor leaf DB data
    public void Planning_NoWrites()
    {
        BuildLeafSimple("c64-a", "A", "B"); RegisterGroup("g", "c64-a");
        MarkSatisfied("c64-a", "A");

        var groupsBefore  = _catalog.LoadDatGroups().Count;
        var linesBefore   = _catalog.LoadDatLines().Count;
        var statusesBefore = Store("c64-a").LoadReleases().Select(r => (r.Id, r.Status)).OrderBy(x => x.Id).ToList();

        _ = Plan("g");

        Assert.Equal(groupsBefore, _catalog.LoadDatGroups().Count);
        Assert.Equal(linesBefore, _catalog.LoadDatLines().Count);
        var statusesAfter = Store("c64-a").LoadReleases().Select(r => (r.Id, r.Status)).OrderBy(x => x.Id).ToList();
        Assert.Equal(statusesBefore, statusesAfter);
    }

    [Fact]  // (B)(E) an incomplete SQLite DB is NOT repaired/migrated by planning → schema unchanged, Blocked
    public void IncompleteDb_NotRepaired_Blocked()
    {
        // A valid SQLite file WITHOUT the leaf schema (no 'releases' table).
        var path = LeafPath("c64-a");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE dummy(x INTEGER)";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        RegisterGroupCounts("g", ("c64-a", 1));   // register WITHOUT opening (no repair)
        var before = Schema(path);

        var p = Plan("g");

        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);   // unreadable/incomplete → blocked
        Assert.Contains(p.Diagnostics, d => d.DatLineId == "c64-a");
        Assert.Equal(before, Schema(path));                          // schema NOT repaired/migrated
        Assert.DoesNotContain(Schema(path), s => s.Name == "releases");   // still no leaf schema
    }

    [Fact]  // (D)(E)(F) planning a healthy DB adds no tables/indexes and changes no schema/data
    public void HealthyDb_NoSchemaChange()
    {
        BuildLeafSimple("c64-a", "A", "B"); RegisterGroup("g", "c64-a");
        SqliteConnection.ClearAllPools();
        var before = Schema(LeafPath("c64-a"));

        _ = Plan("g");

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, Schema(LeafPath("c64-a")));   // identical sqlite_master → no tables/indexes/columns added
    }

    [Fact]  // (planner boundary) archive-output collision → entire Group plan Blocked
    public void ArchiveCollision_Blocks()
    {
        // Two wanted releases with the SAME name collide under SingleFileFlat output (one <name>.zip each).
        BuildLeaf("c64-a", new Rel("Dup", new Rom("d1.d64", Sha1Of("d1"))), new Rel("Dup", new Rom("d2.d64", Sha1Of("d2"))));
        RegisterGroup("g", "c64-a");
        _catalog.SaveDatLineTransformStrategy("c64-a", "release_folder", "zip_compression");   // folder→file → SingleFileFlat

        // Persist the CURRENT structural fingerprint so the gate sees CollisionUnresolved (not merely Stale).
        var store   = new DatLineStore(LeafPath("c64-a"));
        var zipXform = _catalog.LoadTransforms().First(t => t.Id == "zip_compression");
        var config  = ArchiveOutputConfigFactory.BuildConfig("c64", "c64-a", "release_folder", zipXform,
            new Dictionary<string, ExtensionTransformMapping>(), _catalog.LoadTransforms());
        var result  = ArchiveOutputValidator.Validate(config,
            ArchiveOutputConfigFactory.BuildReleaseInputs(store.LoadReleases(), store.LoadAllReleaseFiles()));
        Assert.Equal(ArchiveOutputValidationState.CollisionUnresolved, result.State);   // sanity: it is a collision
        _catalog.UpdateDatLineArchiveOutputValidation(
            "c64-a", "single_file_flat", "collision_unresolved", result.StructuralFingerprint, null, "2020-01-01T00:00:00Z");

        var p = Plan("g");

        Assert.Equal(GroupIngestionReadiness.Blocked, p.Readiness);
        Assert.Contains(p.Diagnostics, d => d.DatLineId == "c64-a" && d.Reason.Contains("Archive output"));
    }

    [Fact]  // (35.22) an exact-duplicate authoritative target is represented once (deterministic collapse)
    public void ExactDuplicateTarget_RepresentedOnce()
    {
        // A malformed release with two ROM entries of the SAME name → same (DatLineId, ReleaseId, RomName).
        BuildLeaf("c64-a", new Rel("R", new Rom("same.d64", Sha1Of("x")), new Rom("same.d64", Sha1Of("x"))));
        RegisterGroup("g", "c64-a");

        var p = Plan("g");

        var dupTargets = p.RequiredTargets.Count(t => t.Target.RomName == "same.d64");
        Assert.True(dupTargets <= 1, $"exact-duplicate target must not be materialized twice (found {dupTargets})");
    }

    [Fact]  // (37.33)(37.36)(37.37) incoming untouched; staging/source not created; containers untouched
    public void Planning_FilesystemReadOnly()
    {
        BuildLeafSimple("c64-a", "A"); RegisterGroup("g", "c64-a");
        var incoming = Path.Combine(_root, "in");
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "roms.zip"), "container");
        File.WriteAllText(Path.Combine(incoming, "loose.d64"), "x");

        var p = Plan("g", incomingOverride: incoming);

        Assert.Equal(1, p.IncomingInspection.LooseFiles);
        Assert.Equal(1, p.IncomingInspection.ArchiveContainers);
        Assert.True(File.Exists(Path.Combine(incoming, "roms.zip")));       // container untouched
        Assert.True(File.Exists(Path.Combine(incoming, "loose.d64")));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "staging"))); // not created by inspecting paths
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "source")));
    }
}

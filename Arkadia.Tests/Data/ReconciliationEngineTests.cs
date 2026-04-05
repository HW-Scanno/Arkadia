using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class ReconciliationEngineTests : IDisposable
{
    // Each test gets its own temp DB file.
    private readonly string _dbPath;

    public ReconciliationEngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private DatLineStore OpenStore() => new DatLineStore(_dbPath);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DatParser.ParsedGame Game(string name, string sha1 = "")
    {
        var roms = sha1.Length == 40
            ? new List<DatParser.ParsedRom> { new DatParser.ParsedRom { Sha1 = sha1 } }
            : new List<DatParser.ParsedRom>();

        return new DatParser.ParsedGame
        {
            Name       = name,
            Roms       = roms,
            ContentKey = DatParser.ComputeContentKey(roms),
        };
    }

    private static ReleaseRecord Release(string datLineId, string name,
                                         string sha1 = "", string status = "missing")
    {
        var roms = sha1.Length == 40
            ? new List<DatParser.ParsedRom> { new DatParser.ParsedRom { Sha1 = sha1 } }
            : new List<DatParser.ParsedRom>();
        return new ReleaseRecord
        {
            Id         = Guid.NewGuid().ToString("N"),
            DatLineId  = datLineId,
            Name       = name,
            Status     = status,
            ContentKey = DatParser.ComputeContentKey(roms),
        };
    }

    // ── ContentKey computation ────────────────────────────────────────────────

    [Fact]
    public void ContentKey_SingleRom_SHA1_ReturnsCorrectFormat()
    {
        var sha1 = "aabbccdd11223344556677889900aabbccdd1122";
        var roms = new List<DatParser.ParsedRom> { new() { Sha1 = sha1 } };
        Assert.Equal($"sha1:{sha1}", DatParser.ComputeContentKey(roms));
    }

    [Fact]
    public void ContentKey_MultiRom_SHA1s_AreSortedAscending()
    {
        var sha1A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var sha1B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var roms  = new List<DatParser.ParsedRom>
        {
            new() { Sha1 = sha1B },
            new() { Sha1 = sha1A },
        };
        var key = DatParser.ComputeContentKey(roms);
        Assert.Equal($"sha1:{sha1A},sha1:{sha1B}", key);
    }

    [Fact]
    public void ContentKey_NoSHA1_FallsBackToMD5()
    {
        var md5  = "aabbccdd11223344556677889900aabb";
        var roms = new List<DatParser.ParsedRom> { new() { Md5 = md5 } };
        Assert.Equal($"md5:{md5}", DatParser.ComputeContentKey(roms));
    }

    [Fact]
    public void ContentKey_NoChecksums_ReturnsEmpty()
    {
        var roms = new List<DatParser.ParsedRom> { new() { Crc = "abcd1234" } };
        Assert.Equal("", DatParser.ComputeContentKey(roms));
    }

    [Fact]
    public void ContentKey_EmptyRomList_ReturnsEmpty()
    {
        Assert.Equal("", DatParser.ComputeContentKey([]));
    }

    // ── ReconciliationEngine — kept / outdated basics ─────────────────────────

    [Fact]
    public void KeptRelease_PreservesStatus()
    {
        const string dlId = "ps2-redump-media";
        var sha1 = "aaaa000000000000000000000000000000000001";
        var store = OpenStore();
        store.SaveReleases([Release(dlId, "Game A", sha1, status: "present")]);

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [Game("Game A", sha1)]);

        var releases = store.LoadReleases();
        var r = Assert.Single(releases);
        Assert.Equal("present", r.Status);   // not reset to missing
        Assert.Equal(1, result.Kept);
        Assert.Equal(0, result.Outdated);
        Assert.Equal(0, result.Pending);
        Assert.Equal(0, result.Missing);
    }

    [Fact]
    public void RemovedRelease_BecomesOutdated()
    {
        const string dlId = "ps2-redump-media";
        var store = OpenStore();
        store.SaveReleases([Release(dlId, "Old Game")]);

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [/* no games — everything removed */]);

        var releases = store.LoadReleases();
        var r = Assert.Single(releases);
        Assert.Equal("outdated", r.Status);
        Assert.Equal(0, result.Kept);
        Assert.Equal(1, result.Outdated);
        Assert.Equal(0, result.Pending);
        Assert.Equal(0, result.Missing);
    }

    // ── PENDING eligibility — reusability gate ────────────────────────────────

    /// <summary>Prior PRESENT + unique exact match → PENDING and reconciliation row.</summary>
    [Fact]
    public void PriorPresent_UniqueMatch_CreatesPending()
    {
        const string dlId = "ps2-redump-media";
        var sha1  = "cccc000000000000000000000000000000000003";
        var store = OpenStore();
        store.SaveReleases([Release(dlId, "Old Title", sha1, status: "present")]);

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [Game("New Title (Remaster)", sha1)]);

        var releases = store.LoadReleases();
        Assert.Equal(2, releases.Count);

        var outdated = releases.Single(r => r.Name == "Old Title");
        Assert.Equal("outdated", outdated.Status);

        var pending = releases.Single(r => r.Name == "New Title (Remaster)");
        Assert.Equal("pending", pending.Status);

        var recons = store.LoadPendingReconciliations();
        var row = Assert.Single(recons);
        Assert.Equal(pending.Id,             row.NewReleaseId);
        Assert.Equal(outdated.Id,            row.OutdatedReleaseId);
        Assert.Equal("New Title (Remaster)", row.TargetName);
        Assert.Equal("content_hash_match",   row.Reason);
        Assert.Equal("pending",              row.Status);

        Assert.Equal(0, result.Kept);
        Assert.Equal(1, result.Outdated);
        Assert.Equal(1, result.Pending);
        Assert.Equal(0, result.Missing);
    }

    /// <summary>Prior MISSING + unique exact match → new release stays MISSING, no pending row.</summary>
    [Fact]
    public void PriorMissing_UniqueMatch_StaysMissing_NoPendingRow()
    {
        const string dlId = "ps2-redump-media";
        var sha1  = "1111000000000000000000000000000000000001";
        var store = OpenStore();
        store.SaveReleases([Release(dlId, "Old Title", sha1, status: "missing")]);

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [Game("New Title", sha1)]);

        var releases = store.LoadReleases();
        Assert.Equal(2, releases.Count);

        Assert.Equal("outdated", releases.Single(r => r.Name == "Old Title").Status);
        Assert.Equal("missing",  releases.Single(r => r.Name == "New Title").Status);

        Assert.Empty(store.LoadPendingReconciliations());
        Assert.Equal(0, result.Pending);
        Assert.Equal(1, result.Missing);
    }

    /// <summary>Prior LOST + unique exact match → new release stays MISSING, no pending row.</summary>
    [Fact]
    public void PriorLost_UniqueMatch_StaysMissing_NoPendingRow()
    {
        const string dlId = "ps2-redump-media";
        var sha1  = "2222000000000000000000000000000000000002";
        var store = OpenStore();
        store.SaveReleases([Release(dlId, "Old Title", sha1, status: "lost")]);

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [Game("New Title", sha1)]);

        var releases = store.LoadReleases();
        Assert.Equal(2, releases.Count);

        Assert.Equal("outdated", releases.Single(r => r.Name == "Old Title").Status);
        Assert.Equal("missing",  releases.Single(r => r.Name == "New Title").Status);

        Assert.Empty(store.LoadPendingReconciliations());
        Assert.Equal(0, result.Pending);
        Assert.Equal(1, result.Missing);
    }

    /// <summary>Prior PRESENT but ambiguous (two releases share the key) → MISSING, no pending row.</summary>
    [Fact]
    public void PriorPresent_AmbiguousMatch_StaysMissing_NoPendingRow()
    {
        const string dlId = "ps2-redump-media";
        var sha1 = "3333000000000000000000000000000000000003";
        var store = OpenStore();
        store.SaveReleases([
            Release(dlId, "Variant A", sha1, status: "present"),
            Release(dlId, "Variant B", sha1, status: "present"),
        ]);

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [Game("Merged Edition", sha1)]);

        var releases = store.LoadReleases();
        Assert.Equal("missing", releases.Single(r => r.Name == "Merged Edition").Status);
        Assert.Empty(store.LoadPendingReconciliations());
        Assert.Equal(0, result.Pending);
        Assert.Equal(1, result.Missing);
    }

    /// <summary>Prior PRESENT but no content-key match at all → MISSING, no pending row.</summary>
    [Fact]
    public void PriorPresent_ZeroMatch_StaysMissing_NoPendingRow()
    {
        const string dlId  = "ps2-redump-media";
        var sha1Old = "4444000000000000000000000000000000000004";
        var sha1New = "5555000000000000000000000000000000000005";
        var store = OpenStore();
        store.SaveReleases([Release(dlId, "Old Game", sha1Old, status: "present")]);

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [Game("Brand New Game", sha1New)]);

        var releases = store.LoadReleases();
        Assert.Equal("outdated", releases.Single(r => r.Name == "Old Game").Status);
        Assert.Equal("missing",  releases.Single(r => r.Name == "Brand New Game").Status);

        Assert.Empty(store.LoadPendingReconciliations());
        Assert.Equal(0, result.Pending);
        Assert.Equal(1, result.Missing);
    }

    // ── Other cases ───────────────────────────────────────────────────────────

    [Fact]
    public void NewRelease_EmptyContentKey_StaysMissing()
    {
        const string dlId = "ps2-redump-media";
        var store = OpenStore();
        store.SaveReleases([Release(dlId, "Old Game", status: "present")]);  // no sha1

        var result = ReconciliationEngine.ApplyDatUpdate(
            store, dlId, [Game("New Game")]);  // no sha1

        Assert.Equal("missing", store.LoadReleases().Single(r => r.Name == "New Game").Status);
        Assert.Empty(store.LoadPendingReconciliations());
        Assert.Equal(0, result.Pending);
        Assert.Equal(1, result.Missing);
    }

    [Fact]
    public void KeptRelease_ContentKeyUpdated_WhenPreviouslyEmpty()
    {
        const string dlId = "ps2-redump-media";
        var sha1  = "eeee000000000000000000000000000000000005";
        var store = OpenStore();
        // Old import had no SHA1 data.
        store.SaveReleases([Release(dlId, "Game X", sha1: "", status: "present")]);

        // New DAT provides SHA1 for the same game.
        ReconciliationEngine.ApplyDatUpdate(store, dlId, [Game("Game X", sha1)]);

        var r = Assert.Single(store.LoadReleases());
        Assert.Equal("present", r.Status);
        Assert.Equal($"sha1:{sha1}", r.ContentKey);
    }

    [Fact]
    public void MultipleNewReleases_MixedOutcomes_AllCorrect()
    {
        const string dlId  = "switch-no-intro-eshop";
        var sha1A = "aaaa100000000000000000000000000000000001";
        var sha1B = "bbbb200000000000000000000000000000000002";
        var store = OpenStore();

        store.SaveReleases([
            Release(dlId, "Game Keep",   sha1A, "present"),  // kept
            Release(dlId, "Game Rename", sha1B, "present"),  // removed → reusable → unique match → pending
        ]);

        var result = ReconciliationEngine.ApplyDatUpdate(store, dlId, [
            Game("Game Keep",        sha1A),   // kept
            Game("Game Renamed v2",  sha1B),   // pending (unique present sha1B match)
            Game("Brand New Game",   ""),       // missing (no sha1)
        ]);

        var releases = store.LoadReleases();
        Assert.Equal(4, releases.Count);

        Assert.Equal("present",  releases.Single(r => r.Name == "Game Keep").Status);
        Assert.Equal("outdated", releases.Single(r => r.Name == "Game Rename").Status);
        Assert.Equal("pending",  releases.Single(r => r.Name == "Game Renamed v2").Status);
        Assert.Equal("missing",  releases.Single(r => r.Name == "Brand New Game").Status);

        Assert.Single(store.LoadPendingReconciliations());

        Assert.Equal(1, result.Kept);
        Assert.Equal(1, result.Outdated);
        Assert.Equal(1, result.Pending);
        Assert.Equal(1, result.Missing);
    }

    /// <summary>
    /// One prior PRESENT and one prior MISSING share the same SHA1.
    /// The PRESENT is reusable but there are two candidates total → ambiguous → MISSING.
    /// </summary>
    [Fact]
    public void MixedPresentAndMissing_SameKey_AmbiguousOnTotal_StaysMissing()
    {
        const string dlId = "ps2-redump-media";
        var sha1 = "6666000000000000000000000000000000000006";
        var store = OpenStore();
        store.SaveReleases([
            Release(dlId, "Variant Present", sha1, status: "present"),
            Release(dlId, "Variant Missing", sha1, status: "missing"),
        ]);

        ReconciliationEngine.ApplyDatUpdate(store, dlId, [Game("New Title", sha1)]);

        Assert.Equal("missing", store.LoadReleases().Single(r => r.Name == "New Title").Status);
        Assert.Empty(store.LoadPendingReconciliations());
    }
}

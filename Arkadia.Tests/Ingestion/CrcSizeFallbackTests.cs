using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// CRC32 + size fallback for CRC-only DAT requirements (e.g. No-Intro Atari ST IPF entries) and the persistence of
/// the SHA1/MD5 computed from the accepted file. Real SQLite temp DBs, temp files only.
/// </summary>
public sealed class CrcSizeFallbackTests : IDisposable
{
    private readonly string _dir;

    public CrcSizeFallbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkCrcFb_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly byte[] Content = Encoding.ASCII.GetBytes("Arkadia CRC fallback test payload");

    private static string Crc(byte[] b) => Crc32Hasher.ComputeHex(new MemoryStream(b));
    private static string Sha1(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();
    private static string Md5(byte[] b)  => Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant();

    private string WriteFile(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static ReleaseRecord Rel(string id, string name, string status = "missing")
        => new() { Id = id, DatLineId = "leaf", Name = name, Status = status };

    private static ReleaseFileRecord F(string releaseId, string rom, string size, string crc, string md5 = "", string sha1 = "")
        => new() { Id = Guid.NewGuid().ToString("N"), ReleaseId = releaseId, RomName = rom, Size = size, Crc = crc, Md5 = md5, Sha1 = sha1 };

    private DatLineStore NewStore(IEnumerable<ReleaseRecord> releases, params ReleaseFileRecord[] files)
    {
        var store = new DatLineStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
        store.SaveReleases(releases.ToList());
        foreach (var g in files.GroupBy(f => f.ReleaseId))
            store.SaveReleaseFiles(g.Key, g.ToList());
        return store;
    }

    private static CrcSizeIndex BuildIndex(DatLineStore store)
    {
        var rels  = store.LoadReleases().Where(r => r.Status != "outdated").ToDictionary(r => r.Id);
        return CrcSizeIndex.Build(rels, store.LoadAllReleaseFiles());
    }

    // ── Crc32Hasher ─────────────────────────────────────────────────────────

    [Fact]  // standard CRC-32/ISO-HDLC check value
    public void Crc32_CheckValue()
        => Assert.Equal("cbf43926", Crc32Hasher.ComputeHex(new MemoryStream(Encoding.ASCII.GetBytes("123456789"))));

    [Fact]
    public void Crc32_EmptyIsZero()
        => Assert.Equal("00000000", Crc32Hasher.ComputeHex(new MemoryStream(Array.Empty<byte>())));

    // ── CrcSizeIndex ────────────────────────────────────────────────────────

    [Fact]  // only CRC-only, size>0 requirements are indexed
    public void Index_OnlyCrcOnlyPositiveSizeRequirements()
    {
        var store = NewStore(new[] { Rel("r1", "A"), Rel("r2", "B"), Rel("r3", "C"), Rel("r4", "D"), Rel("r5", "E") },
            F("r1", "a.ipf", "100", "aabbccdd"),                                   // CRC-only → indexed
            F("r2", "b.ipf", "100", "aabbccdd", sha1: new string('a', 40)),        // has SHA1 → Physical, not indexed
            F("r3", "c.ipf", "100", "aabbccdd", md5: new string('b', 32)),         // has MD5 → Physical, not indexed
            F("r4", "d.ipf", "0",   "00000000"),                                   // size 0 → never
            F("r5", "e.ipf", "",    "aabbccdd"));                                  // unknown size → never

        var idx = BuildIndex(store);

        Assert.True(idx.TryGetTargets(100, "aabbccdd", out var t));
        Assert.Equal(new[] { ("r1", "a.ipf") }, t.ToArray());
    }

    [Fact]
    public void Index_OutdatedReleasesExcluded()
    {
        var store = NewStore(new[] { Rel("r1", "A", "outdated") }, F("r1", "a.ipf", "100", "aabbccdd"));
        Assert.True(BuildIndex(store).IsEmpty);
    }

    [Fact]  // lookup needs BOTH crc and size; crc is case/padding-insensitive
    public void Index_LookupRequiresCrcAndSize_NormalizesCrc()
    {
        var store = NewStore(new[] { Rel("r1", "A") }, F("r1", "a.ipf", "100", "ABCD"));
        var idx = BuildIndex(store);

        Assert.True(idx.TryGetTargets(100, "0000abcd", out _));
        Assert.False(idx.TryGetTargets(101, "0000abcd", out _));
        Assert.False(idx.TryGetTargets(100, "0000abce", out _));
        Assert.True(idx.HasSize(100));
        Assert.False(idx.HasSize(101));
    }

    [Fact]  // same crc+size on several requirements (identical content) → all targets
    public void Index_MultipleTargetsSameCrcSize()
    {
        var store = NewStore(new[] { Rel("r1", "A"), Rel("r2", "B") },
            F("r1", "a.ipf", "100", "aabbccdd"), F("r2", "b.ipf", "100", "aabbccdd"));
        Assert.True(BuildIndex(store).TryGetTargets(100, "aabbccdd", out var t));
        Assert.Equal(2, t.Count);
    }

    // ── DatLineStore.EnrichReleaseFileHashes ────────────────────────────────

    [Fact]
    public void Enrich_FillsBlankHashes_Once()
    {
        var store = NewStore(new[] { Rel("r1", "A") }, F("r1", "a.ipf", "100", "aabbccdd"));
        var sha1 = new string('1', 40); var md5 = new string('2', 32);

        Assert.Equal(1, store.EnrichReleaseFileHashes("r1", "a.ipf", 100, "aabbccdd", md5, sha1));

        var f = store.LoadReleaseFiles("r1").Single();
        Assert.Equal(sha1, f.Sha1);
        Assert.Equal(md5, f.Md5);
        Assert.Equal("aabbccdd", f.Crc);          // DAT-declared fields untouched
        Assert.Equal("100", f.Size);
        Assert.Equal(DatFileRequirementKind.Physical, DatFileRequirement.Classify(f));

        // Second attempt (hashes no longer blank) must not overwrite.
        Assert.Equal(0, store.EnrichReleaseFileHashes("r1", "a.ipf", 100, "aabbccdd", new string('3', 32), new string('4', 40)));
        Assert.Equal(sha1, store.LoadReleaseFiles("r1").Single().Sha1);
    }

    [Fact]  // DAT-declared hashes are never overwritten
    public void Enrich_NeverOverwritesDatHashes()
    {
        var datSha = new string('a', 40);
        var store = NewStore(new[] { Rel("r1", "A") }, F("r1", "a.ipf", "100", "aabbccdd", sha1: datSha));
        Assert.Equal(0, store.EnrichReleaseFileHashes("r1", "a.ipf", 100, "aabbccdd", new string('2', 32), new string('1', 40)));
        Assert.Equal(datSha, store.LoadReleaseFiles("r1").Single().Sha1);
    }

    [Fact]  // size / crc guard against a changed requirement
    public void Enrich_RequiresSizeAndCrcToStillMatch()
    {
        var store = NewStore(new[] { Rel("r1", "A") }, F("r1", "a.ipf", "100", "aabbccdd"));
        Assert.Equal(0, store.EnrichReleaseFileHashes("r1", "a.ipf", 101, "aabbccdd", new string('2', 32), new string('1', 40)));
        Assert.Equal(0, store.EnrichReleaseFileHashes("r1", "a.ipf", 100, "11223344", new string('2', 32), new string('1', 40)));
        Assert.Equal(0, store.EnrichReleaseFileHashes("r1", "other.ipf", 100, "aabbccdd", new string('2', 32), new string('1', 40)));
        Assert.Equal("", store.LoadReleaseFiles("r1").Single().Sha1);
    }

    // ── CrcSizeFallbackMatcher ──────────────────────────────────────────────

    private static CrcSizeFallbackMatcher Matcher(DatLineStore store, Func<string, bool>? satisfied = null)
    {
        var rels = store.LoadReleases().Where(r => r.Status != "outdated").ToDictionary(r => r.Id);
        return new CrcSizeFallbackMatcher(CrcSizeIndex.Build(rels, store.LoadAllReleaseFiles()), rels,
            satisfied ?? (_ => false), store);
    }

    [Fact]  // the core scenario: CRC+size match → target accepted, SHA1/MD5 persisted, op logged
    public void Match_AcceptsFile_AndPersistsHashes()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var path = WriteFile("whatever-name.ipf", Content);

        var res = Matcher(store).TryMatch(path, Sha1(Content), Md5(Content));

        Assert.Equal(new[] { ("r1", "game.ipf") }, res.Targets.ToArray());
        var f = store.LoadReleaseFiles("r1").Single();
        Assert.Equal(Sha1(Content), f.Sha1);
        Assert.Equal(Md5(Content), f.Md5);
        var op = Assert.Single(res.Operations);
        Assert.Equal("dat-hash-enriched", op.Action);
    }

    [Fact]  // after enrichment the ROM is an ordinary Physical SHA1 requirement (indexed by LeafIngestionNeeds)
    public void Match_ThenLeafNeedsIndexesBySha1()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var path = WriteFile("x.ipf", Content);
        Matcher(store).TryMatch(path, Sha1(Content), Md5(Content));

        var needs = LeafIngestionNeeds.Build("leaf", store.LoadReleases(), store.LoadAllReleaseFiles(), Array.Empty<string>());

        Assert.True(needs.Sha1Index.ContainsKey(Sha1(Content)));
        Assert.True(needs.CrcSizeIndex.IsEmpty);   // nothing CRC-only left
    }

    [Fact]  // same size, different content → no match, nothing written
    public void Match_SameSizeDifferentCrc_NoMatch()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var other = (byte[])Content.Clone(); other[0] ^= 0xFF;
        var path = WriteFile("o.ipf", other);

        var res = Matcher(store).TryMatch(path, Sha1(other), Md5(other));

        Assert.Empty(res.Targets);
        Assert.Equal("", store.LoadReleaseFiles("r1").Single().Sha1);
    }

    [Fact]  // size not declared by any requirement → no match
    public void Match_UnknownSize_NoMatch()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, F("r1", "game.ipf", "9999", Crc(Content)));
        var path = WriteFile("s.ipf", Content);
        Assert.Empty(Matcher(store).TryMatch(path, Sha1(Content), Md5(Content)).Targets);
    }

    [Fact]  // satisfied release (present + artifact) is NOT matched by CRC — avoids a CRC-only duplicate-delete
    public void Match_SatisfiedRelease_Declined_NothingWritten()
    {
        var store = NewStore(new[] { Rel("r1", "Game", "present") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var path = WriteFile("p.ipf", Content);

        var res = Matcher(store, satisfied: id => id == "r1").TryMatch(path, Sha1(Content), Md5(Content));

        Assert.Empty(res.Targets);
        Assert.Empty(res.Operations);
        Assert.Equal("", store.LoadReleaseFiles("r1").Single().Sha1);
    }

    [Fact]  // unwanted: matched (so staging routes it to incoming-skip) but DAT record untouched
    public void Match_UnwantedRelease_MatchedButNotEnriched()
    {
        var store = NewStore(new[] { Rel("r1", "Game", "unwanted") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var path = WriteFile("u.ipf", Content);

        var res = Matcher(store).TryMatch(path, Sha1(Content), Md5(Content));

        Assert.Equal(new[] { ("r1", "game.ipf") }, res.Targets.ToArray());
        Assert.Empty(res.Operations);
        Assert.Equal("", store.LoadReleaseFiles("r1").Single().Sha1);
        Assert.Equal("unwanted", store.LoadReleases().Single().Status);   // veto untouched
    }

    [Fact]  // byte-identical duplicate in the same run → accepted again, single enrichment
    public void Match_IdenticalDuplicate_SameRun_Accepted()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var m = Matcher(store);

        var a = m.TryMatch(WriteFile("a.ipf", Content), Sha1(Content), Md5(Content));
        var b = m.TryMatch(WriteFile("b.ipf", Content), Sha1(Content), Md5(Content));

        Assert.Single(a.Targets);
        Assert.Single(b.Targets);
        Assert.Empty(b.Operations);
    }

    [Fact]  // CRC32+size collision with different content in the same run → second is refused
    public void Match_DifferentShaSameCrcSize_SecondRefused()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var m = Matcher(store);
        var path = WriteFile("a.ipf", Content);

        m.TryMatch(path, Sha1(Content), Md5(Content));
        // Simulated collision: same CRC+size file, different SHA1/MD5.
        var res = m.TryMatch(path, new string('9', 40), new string('8', 32));

        Assert.Empty(res.Targets);
        Assert.Contains(res.Operations, o => o.Action == "dat-hash-conflict");
        Assert.Equal(Sha1(Content), store.LoadReleaseFiles("r1").Single().Sha1);   // first acceptance stands
    }

    [Fact]  // no hashes available (read failure) → never accept on CRC alone
    public void Match_MissingComputedHashes_NoMatch()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, F("r1", "game.ipf", Content.Length.ToString(), Crc(Content)));
        var path = WriteFile("m.ipf", Content);
        Assert.Empty(Matcher(store).TryMatch(path, "", "").Targets);
        Assert.Equal("", store.LoadReleaseFiles("r1").Single().Sha1);
    }

    [Fact]  // multi-disk release: each disk is its own requirement
    public void Match_MultiFileRelease_EachRomMatchesItsFile()
    {
        var d1 = Encoding.ASCII.GetBytes("disk-one-contents-xx");
        var d2 = Encoding.ASCII.GetBytes("disk-two-contents-yy");
        var store = NewStore(new[] { Rel("r1", "Multi") },
            F("r1", "m (Disk 1).ipf", d1.Length.ToString(), Crc(d1)),
            F("r1", "m (Disk 2).ipf", d2.Length.ToString(), Crc(d2)));
        var m = Matcher(store);

        var r1 = m.TryMatch(WriteFile("1.ipf", d1), Sha1(d1), Md5(d1));
        var r2 = m.TryMatch(WriteFile("2.ipf", d2), Sha1(d2), Md5(d2));

        Assert.Equal(("r1", "m (Disk 1).ipf"), r1.Targets.Single());
        Assert.Equal(("r1", "m (Disk 2).ipf"), r2.Targets.Single());
        Assert.All(store.LoadReleaseFiles("r1"), f => Assert.Equal(DatFileRequirementKind.Physical, DatFileRequirement.Classify(f)));
    }

    // ── DatHashEnrichment.PreserveEnrichedHashes (Update DAT) ───────────────

    [Fact]  // Update DAT rewrites release_files from the DAT; previously-enriched hashes must survive when the rom is unchanged
    public void Preserve_CarriesHashesForUnchangedRom()
    {
        var prev = new List<ReleaseFileRecord> { F("r1", "a.ipf", "100", "aabbccdd", md5: new string('2', 32), sha1: new string('1', 40)) };
        var inc  = new List<ReleaseFileRecord> { F("r1", "a.ipf", "100", "AABBCCDD") };

        DatHashEnrichment.PreserveEnrichedHashes(prev, inc);

        Assert.Equal(new string('1', 40), inc[0].Sha1);
        Assert.Equal(new string('2', 32), inc[0].Md5);
    }

    [Fact]  // DAT-supplied hashes win; changed size/crc → nothing carried over
    public void Preserve_DoesNotOverrideDat_NorCarryChangedRoms()
    {
        var prev = new List<ReleaseFileRecord>
        {
            F("r1", "a.ipf", "100", "aabbccdd", md5: new string('2', 32), sha1: new string('1', 40)),
            F("r1", "b.ipf", "100", "11223344", md5: new string('2', 32), sha1: new string('1', 40)),
        };
        var inc = new List<ReleaseFileRecord>
        {
            F("r1", "a.ipf", "100", "aabbccdd", sha1: new string('9', 40)),   // DAT now provides sha1
            F("r1", "b.ipf", "100", "55667788"),                               // crc changed
        };

        DatHashEnrichment.PreserveEnrichedHashes(prev, inc);

        Assert.Equal(new string('9', 40), inc[0].Sha1);
        Assert.Equal("", inc[0].Md5);          // partial DAT hashes are not mixed with local ones
        Assert.Equal("", inc[1].Sha1);
        Assert.Equal("", inc[1].Md5);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Archive;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Phase B — Deep Hashing (RVZ-SUPPORT-01). Real SQLite temp DBs, synthetic RVZ containers (valid header + payload),
/// injected DolphinTool verify. Never touches real archives/databases/tools.
/// </summary>
public sealed class RvzDeepHashingTests : IDisposable
{
    private const string Platform = "gc";
    private const string Leaf     = "nintendo-gamecube";
    private const long   IsoSize  = 1_459_978_240L;                    // standard GameCube disc

    private readonly string _app;
    private readonly string _incoming;
    private readonly string _skip;
    private readonly IngestionResult _result = new();
    private readonly List<string> _verifyCalls = new();
    private readonly Dictionary<string, DolphinVerifyResult> _verify = new(StringComparer.OrdinalIgnoreCase);
    private int _rvzSeed;

    public RvzDeepHashingTests()
    {
        _app      = Path.Combine(Path.GetTempPath(), "ArkRvzDeep_" + Guid.NewGuid().ToString("N")[..8]);
        _incoming = Path.Combine(_app, "incoming-roms", Platform);
        _skip     = Path.Combine(_app, "incoming-skip", Platform);
        Directory.CreateDirectory(_incoming);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_app, true); } catch { }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed record Logical(string Sha1, string Md5, string Crc);

    private static Logical NewLogical(int seed)
    {
        var b = BitConverter.GetBytes(seed);
        return new Logical(
            Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant(),
            Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant(),
            Crc32Hasher.ComputeHex(new MemoryStream(b)));
    }

    /// <summary>Synthetic RVZ (valid header, random payload) whose DolphinTool verify returns <paramref name="logical"/>.</summary>
    private string Rvz(string name, Logical logical, long logicalSize = IsoSize, string? dir = null)
    {
        var path    = Path.Combine(dir ?? _incoming, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new byte[2048];
        new Random(++_rvzSeed).NextBytes(payload);
        var bytes = new byte[RvzHeader.HeaderSize + payload.Length];
        RvzHeader.BuildForTests(logicalSize, bytes.Length).CopyTo(bytes, 0);
        payload.CopyTo(bytes, RvzHeader.HeaderSize);
        File.WriteAllBytes(path, bytes);
        _verify[path] = new DolphinVerifyResult(true, new RvzLogicalHashes(logical.Sha1, logical.Md5, logical.Crc), "", false, Array.Empty<string>());
        return path;
    }

    private static ReleaseRecord Rel(string id, string name, string status = "missing")
        => new() { Id = id, DatLineId = Leaf, Name = name, Status = status };

    private static ReleaseFileRecord Iso(string releaseId, string rom, Logical l, long size = IsoSize, bool withMd5Crc = true)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = releaseId, RomName = rom, Size = size.ToString(),
            Sha1 = l.Sha1, Md5 = withMd5Crc ? l.Md5 : "", Crc = withMd5Crc ? l.Crc : "",
        };

    private readonly Dictionary<DatLineStore, string> _dbPaths = new();

    private long CountSourceArtifacts(DatLineStore store)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPaths[store]}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM source_artifacts";
        return (long)cmd.ExecuteScalar()!;
    }

    private DatLineStore NewStore(IEnumerable<ReleaseRecord> releases, params ReleaseFileRecord[] files)
    {
        var dbPath = Path.Combine(_app, Guid.NewGuid().ToString("N") + ".db");
        var store  = new DatLineStore(dbPath);
        _dbPaths[store] = dbPath;
        store.SaveReleases(releases.ToList());
        foreach (var g in files.GroupBy(f => f.ReleaseId))
            store.SaveReleaseFiles(g.Key, g.ToList());
        return store;
    }

    private RvzDeepHashSettings Settings(Func<string, string, bool>? sameFs = null, Action<string, string>? copy = null) => new()
    {
        CrossFilesystemCopy = copy ?? ((src, dest) => File.Copy(src, dest, overwrite: false)),
        AppRoot          = _app,
        PlatformId       = Platform,
        DatLineId        = Leaf,
        SkipDir          = _skip,
        ArchiveForm      = ArchiveDatLineOutputForm.SingleFileFlat,
        IsRvzEligibleRom = rom => Path.GetExtension(rom).Equals(".iso", StringComparison.OrdinalIgnoreCase),
        SafeFileName     = IngestionPaths.SafeFolderName,
        VerifyLogical    = p =>
        {
            _verifyCalls.Add(p);
            return _verify.TryGetValue(p, out var r) ? r : DolphinVerifyResult.Fail("no fixture");
        },
        SameFilesystem   = sameFs ?? StagingHelpers.SameVolume,
    };

    private RvzDeepHashOutcome Run(DatLineStore store, IReadOnlyList<string> candidates,
                                   Func<string, string, bool>? sameFs = null, Action<string, string>? copy = null)
        => RvzDeepHashing.Run(Settings(sameFs, copy), candidates, store, store.LoadReleases(), store.LoadAllReleaseFiles(),
                              _result, new Progress<IngestionProgress>(_ => { }));

    private string ArchivePath(string releaseName) =>
        Path.Combine(_app, "archive", Platform, Leaf, IngestionPaths.SafeFolderName(releaseName) + ".rvz");

    private static bool Has(IngestionResult r, string action) => r.Operations.Any(o => o.Action == action);

    // ── Match found + wanted → adopted ────────────────────────────────────────

    [Fact]
    public void LogicalMatch_Wanted_IsAdopted_WithLogicalSource_PhysicalDerived_TierA()
    {
        var l     = NewLogical(1);
        var store = NewStore(new[] { Rel("r1", "Game (USA)") }, Iso("r1", "Game (USA).iso", l));
        var rvz   = Rvz("Game (USA).rvz", l);
        var physSha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(rvz))).ToLowerInvariant();
        var physLen  = new FileInfo(rvz).Length;

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.Adopted, o.Dispositions[rvz]);
        Assert.Contains(rvz, o.Consumed);
        Assert.Equal("r1", o.AdoptedReleaseByPath[rvz]);
        Assert.Single(_verifyCalls);                                         // logical verification exactly once

        // Archive: RVZ moved into the flat policy path; incoming is empty; no fabricated ISO anywhere.
        var dest = ArchivePath("Game (USA)");
        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(rvz));
        Assert.False(Directory.Exists(Path.Combine(_app, "source")));
        Assert.Empty(Directory.GetFiles(_app, "*.iso", SearchOption.AllDirectories));

        // Derived = the physical RVZ (physical SHA1 + physical size), rvz_compression, Tier A.
        var ck = $"sha1:{l.Sha1}";
        var da = store.GetDerivedByContentKey(ck)!;
        Assert.Equal("rvz_compression", da.StorageStrategyId);
        Assert.Equal(physSha1, da.HashedDerivedSha1);
        Assert.Equal(physLen, da.DerivedSizeBytes);
        Assert.Equal("A", da.ArchiveTier);
        Assert.Equal($"archive/{Platform}/{Leaf}/{IngestionPaths.SafeFolderName("Game (USA)")}.rvz", da.RelativePath.Replace('\\', '/'));

        // Source = the LOGICAL ISO (verified logical digests + logical size), linked from the derived artifact.
        var sa = store.GetSourceByContentKey(ck)!;
        Assert.Equal(1, CountSourceArtifacts(store));
        Assert.Equal(da.SourceArtifactId, sa.Id);
        Assert.Equal(l.Sha1, sa.HashedSourceSha1);
        Assert.Equal(l.Md5,  sa.HashedSourceMd5);
        Assert.Equal(l.Crc,  sa.HashedSourceCrc32);
        Assert.Equal(IsoSize, sa.SourceSizeBytes);
        Assert.NotEqual(physSha1, sa.HashedSourceSha1);

        // Coverage: release present, linked, effective tier A, DB-authoritative satisfaction.
        var rel = store.LoadReleases().Single(r => r.Id == "r1");
        Assert.Equal("present", rel.Status);
        Assert.Equal("A", rel.Tier);
        Assert.Single(store.GetDerivedArtifactsByReleaseId("r1"));

        Assert.Equal(1, _result.FilesMatched);
        Assert.Equal(1, _result.DerivedArtifactsCreated);
        Assert.Equal(1, _result.ReleasesPresent);
        Assert.True(Has(_result, "rvz-adopted-moved"));
    }

    [Fact]
    public void LogicalMd5NotComputed_IsAdoptedTierA_SourceMd5LeftNull_Sha1AndCrcStillEnforced()
    {
        var l     = NewLogical(41);
        var datFile = Iso("r1", "Game (USA).iso", l);                                              // DAT declares MD5
        var store = NewStore(new[] { Rel("r1", "Game (USA)") }, datFile);
        var rvz   = Rvz("Game (USA).rvz", l with { Md5 = "" });   // real DolphinTool 2609b: "MD5 not computed"

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.Adopted, o.Dispositions[rvz]);
        var ck = $"sha1:{l.Sha1}";
        var da = store.GetDerivedByContentKey(ck)!;
        Assert.Equal("A", da.ArchiveTier);
        var sa = store.GetSourceByContentKey(ck)!;
        Assert.Equal(l.Sha1, sa.HashedSourceSha1);
        Assert.Equal(l.Crc,  sa.HashedSourceCrc32);
        Assert.Null(sa.HashedSourceMd5);                                     // never fabricated from the DAT

        // Details "Source Verified" ✔ for exactly these persisted records (Source linked from the RVZ Derived).
        Assert.True(RvzIds.IsVerifiedLogicalSourceWithoutMd5(datFile, sa, da));

        // A CRC32 disagreement is still rejected when MD5 was not computed.
        var l2     = NewLogical(42);
        var store2 = NewStore(new[] { Rel("r2", "Other (USA)") }, Iso("r2", "Other (USA).iso", l2));
        var rvz2   = Rvz("Other (USA).rvz", l2 with { Md5 = "", Crc = "00000000" });
        var o2 = Run(store2, new[] { rvz2 });
        Assert.NotEqual(RvzDeepHashDisposition.Adopted, o2.Dispositions[rvz2]);
        Assert.Null(store2.GetDerivedByContentKey($"sha1:{l2.Sha1}"));
    }

    [Fact]
    public void SameFilesystem_IsNativeMove_NotCopy()
    {
        var l     = NewLogical(2);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l);
        var id    = FileIdentity.Of(rvz);

        Run(store, new[] { rvz });

        Assert.Equal(id, FileIdentity.Of(ArchivePath("Game")));     // same NTFS file → rename, never Copy+Delete
    }

    [Fact]
    public void CrossFilesystem_UsesCopy_SourceRemovedOnlyAfterCommit()
    {
        var l     = NewLogical(3);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l);
        var bytes = File.ReadAllBytes(rvz);

        var o = Run(store, new[] { rvz }, sameFs: (_, _) => false);

        Assert.Equal(RvzDeepHashDisposition.Adopted, o.Dispositions[rvz]);
        Assert.Equal(bytes, File.ReadAllBytes(ArchivePath("Game")));
        Assert.False(File.Exists(rvz));
        Assert.True(Has(_result, "rvz-adopted-copied"));
        var da = store.GetDerivedByContentKey($"sha1:{l.Sha1}")!;
        Assert.Equal(Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), da.HashedDerivedSha1);
        Assert.Equal("present", store.LoadReleases().Single().Status);
    }

    [Fact]
    public void CrossFilesystem_DestinationHashMismatch_FailsTransfer_IncomingPreserved_NothingRegistered()
    {
        var l     = NewLogical(31);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l);
        var bytes = File.ReadAllBytes(rvz);

        // Same length, one flipped byte: a size check alone would accept it.
        var o = Run(store, new[] { rvz }, sameFs: (_, _) => false, copy: (src, dest) =>
        {
            var b = File.ReadAllBytes(src);
            b[b.Length - 1] ^= 0xFF;
            File.WriteAllBytes(dest, b);
        });

        Assert.Equal(RvzDeepHashDisposition.CommitBlocked, o.Dispositions[rvz]);
        Assert.True(Has(_result, "rvz-transfer-failed"));
        Assert.Equal(bytes, File.ReadAllBytes(rvz));                 // incoming untouched
        Assert.False(File.Exists(ArchivePath("Game")));              // unverified copy never kept
        Assert.Empty(store.GetDerivedArtifacts());
        Assert.Equal("missing", store.LoadReleases().Single().Status);
        Assert.Contains("r1", o.FailedReleaseIds);
    }

    [Fact]
    public void MultiFileReleaseFolderForm_UsesOriginalNameInsideReleaseFolder()
    {
        var l     = NewLogical(4);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Disc.iso", l));
        var rvz   = Rvz("x.rvz", l);
        var s     = Settings() with { ArchiveForm = ArchiveDatLineOutputForm.MultiFileReleaseFolder };

        RvzDeepHashing.Run(s, new[] { rvz }, store, store.LoadReleases(), store.LoadAllReleaseFiles(), _result,
                           new Progress<IngestionProgress>(_ => { }));

        Assert.True(File.Exists(Path.Combine(_app, "archive", Platform, Leaf, "Game", "Disc.rvz")));
    }

    // ── No match / wrong hash / size ──────────────────────────────────────────

    [Fact]
    public void WrongLogicalHash_FollowsIncomingSkip_NoDbWrites()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", NewLogical(5)));
        var rvz   = Rvz("Game.rvz", NewLogical(999));                    // e.g. scrubbed / NKit-derived image

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.NoMatch, o.Dispositions[rvz]);
        Assert.False(File.Exists(rvz));
        Assert.True(File.Exists(Path.Combine(_skip, "Game.rvz")));   // moved, never deleted
        Assert.False(Has(_result, "duplicate-deleted"));
        Assert.Empty(store.GetDerivedArtifacts());
        Assert.Equal("missing", store.LoadReleases().Single().Status);
        Assert.Equal(1, _result.FilesSkipped);
    }

    [Fact]
    public void LogicalSizeMatchingNoEligibleRequirement_SkipsWithoutRunningDolphinTool()
    {
        var l     = NewLogical(6);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l, logicalSize: IsoSize - 2048);

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.NoMatch, o.Dispositions[rvz]);
        Assert.Empty(_verifyCalls);
        Assert.Empty(store.GetDerivedArtifacts());
    }

    [Fact]
    public void Sha1MatchButDatSizeDiffers_IsNotAccepted()
    {
        var l     = NewLogical(7);
        // r2 makes the size pre-filter pass; r1 carries the SHA1 but a different declared size.
        var store = NewStore(new[] { Rel("r1", "A"), Rel("r2", "B") },
            Iso("r1", "A.iso", l, size: IsoSize + 1), Iso("r2", "B.iso", NewLogical(70)));
        var rvz   = Rvz("A.rvz", l);

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.NoMatch, o.Dispositions[rvz]);
        Assert.True(Has(_result, "rvz-logical-conflict"));
        Assert.Empty(store.GetDerivedArtifacts());
    }

    [Fact]
    public void Sha1MatchButDatMd5OrCrcDiffers_IsNotAccepted()
    {
        var l   = NewLogical(8);
        var bad = l with { Crc = "00000000" };
        var store = NewStore(new[] { Rel("r1", "A") }, Iso("r1", "A.iso", bad));
        var rvz   = Rvz("A.rvz", l);

        Assert.Equal(RvzDeepHashDisposition.NoMatch, Run(store, new[] { rvz }).Dispositions[rvz]);
        Assert.Empty(store.GetDerivedArtifacts());
    }

    [Fact]
    public void Sha1OnlyDatEntry_IsAccepted_ByLogicalSha1AndSize()
    {
        var l     = NewLogical(9);
        var store = NewStore(new[] { Rel("r1", "A") }, Iso("r1", "A.iso", l, withMd5Crc: false));
        var rvz   = Rvz("A.rvz", l);
        Assert.Equal(RvzDeepHashDisposition.Adopted, Run(store, new[] { rvz }).Dispositions[rvz]);
    }

    // ── Ambiguous / present / unwanted / unsupported ──────────────────────────

    [Fact]
    public void AmbiguousLogicalMatch_IsNeverAssigned()
    {
        var l     = NewLogical(10);
        var store = NewStore(new[] { Rel("r1", "Game (USA)"), Rel("r2", "Game (USA) (Rev 1)") },
            Iso("r1", "Game (USA).iso", l), Iso("r2", "Game (USA) (Rev 1).iso", l));
        var rvz   = Rvz("Game.rvz", l);

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.Ambiguous, o.Dispositions[rvz]);
        Assert.True(Has(_result, "rvz-ambiguous"));
        Assert.True(File.Exists(Path.Combine(_skip, "Game.rvz")));   // moved, never deleted
        Assert.False(Has(_result, "duplicate-deleted"));
        Assert.Empty(store.GetDerivedArtifacts());
        Assert.All(store.LoadReleases(), r => Assert.Equal("missing", r.Status));
    }

    [Fact]
    public void AlreadyPresentRelease_RedundantIncomingRvzIsDeleted_ArchivedArtifactUntouched()
    {
        var l     = NewLogical(11);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        Run(store, new[] { Rvz("Game.rvz", l) });
        var archived      = ArchivePath("Game");
        var archivedBytes = File.ReadAllBytes(archived);
        var archivedId    = FileIdentity.Of(archived);
        var daBefore      = store.GetDerivedByContentKey($"sha1:{l.Sha1}")!;
        int verifyBefore  = _verifyCalls.Count;

        // A different RVZ of the same logical ISO (other compression settings) arrives later.
        var second = Rvz("Game (copy).rvz", l);
        var o = Run(store, new[] { second });

        Assert.Equal(RvzDeepHashDisposition.AlreadySatisfied, o.Dispositions[second]);
        Assert.False(File.Exists(second));                                   // redundant candidate deleted
        Assert.False(File.Exists(Path.Combine(_skip, "Game (copy).rvz")));   // not moved to incoming-skip
        Assert.True(Has(_result, "duplicate-deleted"));
        Assert.Contains(second, o.Consumed);
        Assert.Equal("r1", o.DuplicateReleaseByPath[second]);
        Assert.Equal(verifyBefore + 1, _verifyCalls.Count);                  // no extra verification for deletion

        Assert.Equal(archivedBytes, File.ReadAllBytes(archived));            // archived artifact untouched
        Assert.Equal(archivedId, FileIdentity.Of(archived));
        var daAfter = Assert.Single(store.GetDerivedArtifacts());
        Assert.Equal(daBefore.Id, daAfter.Id);
        Assert.Equal(daBefore.HashedDerivedSha1, daAfter.HashedDerivedSha1);
        Assert.Equal(daBefore.RelativePath, daAfter.RelativePath);
    }

    [Fact]
    public void RepeatedIngestion_IsIdempotent()
    {
        var l     = NewLogical(12);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l);
        Run(store, new[] { rvz });

        // Same physical RVZ dropped again.
        var again = Path.Combine(_incoming, "Game.rvz");
        File.Copy(ArchivePath("Game"), again);
        _verify[again] = _verify[rvz];
        var o = Run(store, new[] { again });

        Assert.Equal(RvzDeepHashDisposition.AlreadySatisfied, o.Dispositions[again]);
        Assert.False(File.Exists(again));
        Assert.Single(store.GetDerivedArtifacts());
        Assert.Equal(1, CountSourceArtifacts(store));
        Assert.True(File.Exists(ArchivePath("Game")));
    }

    [Fact]
    public void PresentStatusWithoutArtifact_IsNotAlreadyPresent_RvzIsAdoptedNotDeleted()
    {
        var l     = NewLogical(32);
        var store = NewStore(new[] { Rel("r1", "Game", "present") }, Iso("r1", "Game.iso", l));   // inconsistent: no artifact
        var rvz   = Rvz("Game.rvz", l);

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.Adopted, o.Dispositions[rvz]);
        Assert.False(Has(_result, "duplicate-deleted"));
        Assert.True(File.Exists(ArchivePath("Game")));
    }

    [Fact]
    public void DatIdentityWithArtifactRow_ButReleaseNotPresent_IsNeverDeleted()
    {
        var l     = NewLogical(33);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        // A derived row for the same content exists, but the release is 'missing' and not linked -> not PRESENT.
        var rel = $"archive/{Platform}/{Leaf}/Game.rvz";
        store.IngestDerivedArtifact($"sha1:{l.Sha1}", "", "rvz_compression", "Game.rvz", rel, 10, new string('e', 40));
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath("Game"))!);
        File.WriteAllText(ArchivePath("Game"), "existing");
        var rvz   = Rvz("Game.rvz", l);

        var o = Run(store, new[] { rvz });

        Assert.NotEqual(RvzDeepHashDisposition.AlreadySatisfied, o.Dispositions[rvz]);
        Assert.True(File.Exists(rvz));                                // preserved, not deleted
        Assert.False(Has(_result, "duplicate-deleted"));
        Assert.Equal("existing", File.ReadAllText(ArchivePath("Game")));
    }

    [Fact]
    public void UnwantedRelease_IsRoutedToIncomingSkip_StatusUntouched()
    {
        var l     = NewLogical(13);
        var store = NewStore(new[] { Rel("r1", "Game", "unwanted") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l);

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.Unwanted, o.Dispositions[rvz]);
        Assert.True(Has(_result, "unwanted-moved"));
        Assert.Equal(1, _result.UnwantedSkipped);
        Assert.Equal("unwanted", store.LoadReleases().Single().Status);
        Assert.Empty(store.GetDerivedArtifacts());
    }

    [Fact]
    public void MultiFileRelease_IsUnsupported_NotAdopted()
    {
        var l     = NewLogical(14);
        var store = NewStore(new[] { Rel("r1", "Game") },
            Iso("r1", "Game.iso", l),
            new ReleaseFileRecord { Id = "x", ReleaseId = "r1", RomName = "extra.bin", Size = "10", Sha1 = new string('c', 40) });
        var rvz   = Rvz("Game.rvz", l);

        Assert.Equal(RvzDeepHashDisposition.Unsupported, Run(store, new[] { rvz }).Dispositions[rvz]);
        Assert.Empty(store.GetDerivedArtifacts());
    }

    // ── Tool failure / not applicable ─────────────────────────────────────────

    [Fact]
    public void ToolFailure_PreservesFileInPlace_IsNotAMismatch()
    {
        var l     = NewLogical(15);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l);
        _verify[rvz] = DolphinVerifyResult.Fail("DolphinTool verify exited with code 1");

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.ToolFailed, o.Dispositions[rvz]);
        Assert.Contains(rvz, o.Consumed);                  // not handed to the ordinary matcher → not moved to skip
        Assert.True(File.Exists(rvz));
        Assert.False(Directory.Exists(_skip));
        Assert.Empty(store.GetDerivedArtifacts());
        Assert.True(Has(_result, "deep-hash-failed"));
        Assert.Equal(0, _result.FilesSkipped);
        Assert.Contains(rvz, o.ToolFailedPaths);
    }

    [Fact]
    public void ToolFailure_IsAnErrorInTheFinalResult_NeverUnconditionalSuccess()
    {
        var a = NewLogical(34); var b = NewLogical(35);
        var store = NewStore(new[] { Rel("r1", "A"), Rel("r2", "B") }, Iso("r1", "A.iso", a), Iso("r2", "B.iso", b));
        var ok  = Rvz("A.rvz", a);
        var bad = Rvz("B.rvz", b);
        _verify[bad] = DolphinVerifyResult.Fail("malformed DolphinTool output (missing CRC32/MD5/SHA1)");

        var o = Run(store, new[] { bad, ok });                       // the failure does not stop other candidates

        Assert.Equal(RvzDeepHashDisposition.Adopted, o.Dispositions[ok]);
        Assert.Equal(RvzDeepHashDisposition.ToolFailed, o.Dispositions[bad]);
        Assert.True(File.Exists(bad));

        // Exactly what RunIngestionWork assigns at finalization.
        _result.TransformsFailed = o.CombinedFailureCount(new HashSet<string>(), new HashSet<string> { "r1" });
        Assert.Equal(1, _result.TransformsFailed);
        Assert.Equal(IngestionStatus.PartialSuccess, _result.Status);
        Assert.False(_result.Success);
        var log = IngestionLogFormatter.Build(Leaf, _result, DateTime.UtcNow);
        int f0 = log.IndexOf("FAILURES", StringComparison.Ordinal);
        int f1 = log.IndexOf("OPERATIONS", StringComparison.Ordinal);
        Assert.True(f0 >= 0 && f1 > f0);
        Assert.Contains("deep-hash-failed", log.Substring(f0, f1 - f0));
    }

    [Fact]
    public void ToolFailureOnly_RunIsFailed()
    {
        var l     = NewLogical(36);
        var store = NewStore(new[] { Rel("r1", "A") }, Iso("r1", "A.iso", l));
        var rvz   = Rvz("A.rvz", l);
        _verify[rvz] = DolphinVerifyResult.Fail("DolphinTool could not be started: access denied");

        var o = Run(store, new[] { rvz });
        _result.TransformsFailed = o.CombinedFailureCount(new HashSet<string>(), new HashSet<string>());

        Assert.Equal(IngestionStatus.Failed, _result.Status);
    }

    [Fact]
    public void NonRvzContent_WithRvzExtension_IsHandedBack_Untouched()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", NewLogical(16)));
        var fake  = Path.Combine(_incoming, "fake.rvz");
        File.WriteAllBytes(fake, new byte[500]);

        var o = Run(store, new[] { fake });

        Assert.Equal(RvzDeepHashDisposition.HandedBack, o.Dispositions[fake]);
        Assert.DoesNotContain(fake, o.Consumed);
        Assert.True(File.Exists(fake));
        Assert.Empty(_verifyCalls);
    }

    [Fact]
    public void PhysicalDatMatch_KeepsOrdinaryMatchingSemantics()
    {
        var store0 = NewStore(Array.Empty<ReleaseRecord>());
        var rvz    = Rvz("Game.rvz", NewLogical(17));
        var phys   = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(rvz))).ToLowerInvariant();
        var store  = NewStore(new[] { Rel("r1", "Game") },
            new ReleaseFileRecord { Id = "p", ReleaseId = "r1", RomName = "Game.rvz", Size = new FileInfo(rvz).Length.ToString(), Sha1 = phys });

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.HandedBack, o.Dispositions[rvz]);
        Assert.Empty(_verifyCalls);
        Assert.True(File.Exists(rvz));
        _ = store0;
    }

    // ── Archive safeguards ────────────────────────────────────────────────────

    [Fact]
    public void ArchiveCollision_UnclaimedTargetExists_IsNeverOverwritten()
    {
        var l     = NewLogical(18);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l);
        var dest  = ArchivePath("Game");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "someone else's file");

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.CommitBlocked, o.Dispositions[rvz]);
        Assert.Contains("r1", o.FailedReleaseIds);
        Assert.Equal("someone else's file", File.ReadAllText(dest));
        Assert.True(File.Exists(rvz));                                       // preserved in incoming
        Assert.True(Has(_result, "archive-collision"));
        Assert.Empty(store.GetDerivedArtifacts());
        Assert.Equal("missing", store.LoadReleases().Single().Status);
    }

    [Fact]
    public void ExistingArtifactOfAnotherStrategy_IsNotRepointed()
    {
        var l     = NewLogical(19);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        store.IngestDerivedArtifact($"sha1:{l.Sha1}", "", "no_compression", "Game.iso", $"archive/{Platform}/{Leaf}/Game.iso", IsoSize, l.Sha1);
        var rvz   = Rvz("Game.rvz", l);

        var o = Run(store, new[] { rvz });

        Assert.Equal(RvzDeepHashDisposition.CommitBlocked, o.Dispositions[rvz]);
        Assert.True(Has(_result, "rvz-existing-artifact-conflict"));
        Assert.True(File.Exists(rvz));
        Assert.Equal("no_compression", Assert.Single(store.GetDerivedArtifacts()).StorageStrategyId);
    }

    // ── Candidate selection / no extra I/O ────────────────────────────────────

    [Theory]
    [InlineData("Game.rvz", true)]
    [InlineData("Game.RVZ", true)]
    [InlineData("Game.iso", false)]
    [InlineData("Game.chd", false)]
    [InlineData("Game.zip", false)]
    [InlineData("Game.wia", false)]
    [InlineData("Game.rvz.part", false)]
    public void OnlyRvzExtensionIsACandidate(string name, bool expected)
        => Assert.Equal(expected, RvzDeepHashing.IsCandidate(name));

    [Fact]
    public void NoCandidates_DoesNothing()
    {
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", NewLogical(20)));
        var o = Run(store, Array.Empty<string>());
        Assert.Empty(o.Consumed);
        Assert.Empty(_result.Operations);
        Assert.Empty(_verifyCalls);
    }

    [Fact]
    public void MixedBatch_EachRvzVerifiedAtMostOnce()
    {
        var a = NewLogical(21); var b = NewLogical(22);
        var store = NewStore(new[] { Rel("r1", "A"), Rel("r2", "B") }, Iso("r1", "A.iso", a), Iso("r2", "B.iso", b));
        var ra = Rvz("A.rvz", a); var rb = Rvz("B.rvz", b); var rc = Rvz("C.rvz", NewLogical(23));

        var o = Run(store, new[] { ra, rb, rc });

        Assert.Equal(3, _verifyCalls.Count);
        Assert.Equal(3, _verifyCalls.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, o.AdoptedReleaseByPath.Count);
        Assert.Equal(RvzDeepHashDisposition.NoMatch, o.Dispositions[rc]);
    }

    [Fact]
    public void ExtractedMember_AlreadyPresent_OnlyThatMemberIsDeleted_ContainerLeftToPhase9()
    {
        var l     = NewLogical(37);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        Run(store, new[] { Rvz("Game.rvz", l) });                          // release now PRESENT

        // incoming/Game.7z was extracted by pre-ingest into incoming/Game/ (sibling folder).
        var container = Path.Combine(_incoming, "Game.7z");
        File.WriteAllBytes(container, new byte[] { 1, 2, 3 });
        var extractRoot = Path.Combine(_incoming, "Game");
        var member      = Rvz("Game.rvz", l, dir: extractRoot);
        var sibling     = Path.Combine(extractRoot, "readme.txt");
        File.WriteAllText(sibling, "unrelated member");
        var info = new ExtractedArchiveInfo(container, extractRoot, new[] { member, sibling });

        var o = Run(store, new[] { member });

        Assert.Equal(RvzDeepHashDisposition.AlreadySatisfied, o.Dispositions[member]);
        Assert.False(File.Exists(member));                                 // redundant member deleted
        Assert.True(File.Exists(sibling));                                 // unrelated member untouched
        Assert.True(File.Exists(container));                               // container NOT deleted here
        Assert.Equal("r1", o.DuplicateReleaseByPath[member]);              // associated for Phase 9
        Assert.Contains(member, o.Consumed);

        // Phase 9 keeps owning the container lifecycle (existing rule: extracted => container cleaned up there).
        var decision = Assert.Single(ArchiveCleanupPlanner.Plan(new[] { info },
            new Dictionary<string, HashSet<string>> { [container] = new() { "r1" } },
            new HashSet<string> { "r1" }, new HashSet<string>(), new HashSet<string>(), new HashSet<string> { sibling }));
        Assert.True(decision.ShouldDelete);
    }

    [Fact]
    public void RvzFromExtractedContainer_IsReportedForPhase9Association()
    {
        var l     = NewLogical(24);
        var store = NewStore(new[] { Rel("r1", "Game") }, Iso("r1", "Game.iso", l));
        var rvz   = Rvz("Game.rvz", l, dir: Path.Combine(_incoming, "Game"));   // sibling extraction folder

        var o = Run(store, new[] { rvz });

        Assert.Equal("r1", o.AdoptedReleaseByPath[rvz]);
        Assert.Contains(rvz, o.Consumed);
    }
}

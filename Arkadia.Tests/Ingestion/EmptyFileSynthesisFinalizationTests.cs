using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Authoritative zero-byte synthesis inside <see cref="LeafFinalizationEngine"/> (generate → verify → completeness
/// over the COMPLETE requirement set → commit), plus its interaction with <see cref="ResumableStagingDetector"/>.
/// Models the real TOSEC TRS-80 case: <c>STRINT.CMD</c> (hashed) + <c>STORE.DAT</c> (size 0, no hashes). Real temp
/// filesystem + real leaf DB built through the production builder; the transform is a controllable delegate.
/// </summary>
public sealed class EmptyFileSynthesisFinalizationTests : IDisposable
{
    private const string Release = "Store Operating System Initialization v3.5 (1982)(Tandy) [CMD]";

    private readonly string _dir;
    private readonly string _stagingRoot;
    private readonly string _sourceRoot;
    private readonly string _dbPath;

    public EmptyFileSynthesisFinalizationTests()
    {
        _dir         = Path.Combine(Path.GetTempPath(), "ArkSynth_" + Guid.NewGuid().ToString("N")[..8]);
        _stagingRoot = Path.Combine(_dir, "staging");
        _sourceRoot  = Path.Combine(_dir, "source");
        _dbPath      = Path.Combine(_dir, "leaf.db");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed record Rom(string Name, string Size, string Sha1 = "", string Md5 = "", string Crc = "");

    private static readonly Rom Strint = new("STRINT.CMD", "4864", new string('a', 40));
    private static readonly Rom Store  = new("STORE.DAT", "0");

    private DatLineStore Seed(params (string Name, Rom[] Roms)[] releases)
    {
        var games = releases.Select(r => new DatParser.ParsedGame
        {
            Name = r.Name,
            Roms = r.Roms.Select(x => new DatParser.ParsedRom { Name = x.Name, Size = x.Size, Sha1 = x.Sha1, Md5 = x.Md5, Crc = x.Crc }).ToList(),
        }).ToList();
        LeafDatDatabaseBuilder.Build(_dbPath, "leaf", games, null, CancellationToken.None);
        return new DatLineStore(_dbPath);
    }

    private DatLineStore SeedOne(params Rom[] roms) => Seed((Release, roms));

    private static Dictionary<string, ReleaseRecord> Releases(DatLineStore store, string? statusOverride = null)
        => store.LoadReleases().ToDictionary(r => r.Id, r =>
        {
            if (statusOverride is not null) r.Status = statusOverride;
            return r;
        }, StringComparer.Ordinal);

    private static string Folder(string releaseName) => IngestionPaths.SafeFolderName(releaseName);
    private string StagingPath(string rom, string release = Release) => Path.Combine(_stagingRoot, Folder(release), rom);
    private string SourcePath(string rom, string release = Release)  => Path.Combine(_sourceRoot,  Folder(release), rom);

    private void Stage(string rom, byte[] bytes, string release = Release)
    {
        var p = StagingPath(rom, release);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    // Production-equivalent resume inputs (same projection the planner / Single run use).
    private IReadOnlyList<string> DetectResumable(DatLineStore store, string? statusOverride = null)
    {
        var files = store.LoadAllReleaseFiles();
        var inputs = store.LoadReleases().Select(r =>
        {
            var rf = files.TryGetValue(r.Id, out var l) ? l : new List<ReleaseFileRecord>();
            return new ResumeReleaseInput(r.Id, r.Name, statusOverride ?? r.Status,
                rf.Select(f => f.RomName).ToList(),
                rf.Where(DatFileRequirement.IsSynthesizableEmpty).Select(f => f.RomName).ToList());
        }).ToList();
        return ResumableStagingDetector.Detect(_stagingRoot, inputs).ResumableReleaseIds;
    }

    private sealed class Outcome
    {
        public required LeafFinalizationResult Result { get; init; }
        public required IngestionResult        Log    { get; init; }
        public LeafReleaseFinalizeArgs?        Args   { get; set; }
        public bool TransformCalled => Args is not null;
        public bool HasOp(string action) => Log.Operations.Any(o => o.Action == action);
    }

    private Outcome Run(
        DatLineStore store, Dictionary<string, ReleaseRecord> releases,
        IEnumerable<string> affected, IEnumerable<string>? resumable = null,
        Action<LeafReleaseFinalizeArgs>? transform = null)
    {
        var log = new IngestionResult();
        LeafReleaseFinalizeArgs? seen = null;
        var r = LeafFinalizationEngine.FinalizeLeaf(
            new HashSet<string>(affected, StringComparer.Ordinal),
            (resumable ?? Array.Empty<string>()).ToList(),
            releases, store, _stagingRoot, _sourceRoot, "trs80", "leaf", IngestionPaths.SafeFolderName,
            log, new Progress<IngestionProgress>(_ => { }),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            a =>
            {
                // Observe the promoted source exactly as a real processor would see it.
                seen = a;
                transform?.Invoke(a);
            });
        return new Outcome { Result = r, Log = log, Args = seen };
    }

    private static string StatusOf(DatLineStore store, string rid) => store.LoadReleases().Single(r => r.Id == rid).Status;

    // ── (6) mixed release: hashed file matched + synthesized empty file ─────

    [Fact]
    public void Mixed_HashedStaged_EmptySynthesized_ReleaseBecomesPresent()
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1, 2, 3, 4 });

        var o = Run(store, releases, new[] { rid });

        Assert.True(o.HasOp("empty-synthesized"));
        Assert.True(o.TransformCalled);
        Assert.Contains(rid, o.Result.SuccessfulReleaseIds);
        Assert.Equal("present", StatusOf(store, rid));
        Assert.True(File.Exists(SourcePath("STORE.DAT")));
        Assert.Equal(0, new FileInfo(SourcePath("STORE.DAT")).Length);
        Assert.Equal(4, new FileInfo(SourcePath("STRINT.CMD")).Length);
    }

    // ── (7) release made only of synthesizable empty files ─────────────────

    [Fact]
    public void OnlySynthesizable_ExplicitFinalization_BecomesPresent_WithoutIncoming()
    {
        var store = SeedOne(Store, new Rom("SECOND.DAT", "0"));
        var releases = Releases(store);
        var rid = releases.Keys.Single();

        var resumable = DetectResumable(store);
        Assert.Contains(rid, resumable);                               // routed to finalization with no staging folder
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, Folder(Release))));   // detection itself creates nothing

        var o = Run(store, releases, Array.Empty<string>(), resumable);

        Assert.Equal(2, o.Log.Operations.Count(op => op.Action == "empty-synthesized"));
        Assert.Contains(rid, o.Result.SuccessfulReleaseIds);
        Assert.Equal("present", StatusOf(store, rid));
        Assert.Equal(0, new FileInfo(SourcePath("STORE.DAT")).Length);
        Assert.Equal(0, new FileInfo(SourcePath("SECOND.DAT")).Length);
    }

    // ── (8) stuck release: hashed file already staged by an earlier run ─────

    [Fact]
    public void Stuck_HashedAlreadyStaged_ResumesAndSynthesizesRemainingEmpty()
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 9, 9, 9, 9 });   // earlier run staged it; its incoming original is gone

        var resumable = DetectResumable(store);
        Assert.Contains(rid, resumable);

        // No new incoming at all: affected is empty, resumable drives finalization.
        var o = Run(store, releases, Array.Empty<string>(), resumable);

        Assert.True(o.HasOp("staging-resumed"));
        Assert.True(o.HasOp("empty-synthesized"));
        Assert.Equal("present", StatusOf(store, rid));
        Assert.Equal(4, new FileInfo(SourcePath("STRINT.CMD")).Length);
        Assert.Equal(0, new FileInfo(SourcePath("STORE.DAT")).Length);
    }

    // ── (10) the storage processor receives the complete release folder incl. the 0-byte member ─

    [Fact]
    public void FolderStrategy_ReceivesSynthesizedZeroByteMemberInPromotedSource()
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });

        string[]? filesSeenByProcessor = null;
        long storeLengthSeen = -1;
        var o = Run(store, releases, new[] { rid }, transform: a =>
        {
            // A folder processor packages the whole source folder — it must contain both members.
            filesSeenByProcessor = Directory.GetFiles(a.SourceDir).Select(Path.GetFileName).OrderBy(x => x).ToArray()!;
            storeLengthSeen      = new FileInfo(Path.Combine(a.SourceDir, "STORE.DAT")).Length;
        });

        Assert.Equal(new[] { "STORE.DAT", "STRINT.CMD" }, filesSeenByProcessor);
        Assert.Equal(0, storeLengthSeen);
        Assert.Equal(new[] { "STORE.DAT", "STRINT.CMD" }, o.Args!.ExpectedFiles.Select(f => f.RomName).OrderBy(x => x));
    }

    // ── (11) never overwrite an unexpected existing staging file ────────────

    [Fact]
    public void UnexpectedNonEmptyStagedFile_IsNotOverwritten_ReleaseStaysIncomplete()
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });
        Stage("STORE.DAT", new byte[] { 7, 7, 7 });   // unexpected content where an empty file is required

        var o = Run(store, releases, new[] { rid });

        Assert.True(o.HasOp("empty-synthesis-failed"));
        Assert.False(o.TransformCalled);
        Assert.Contains(rid, o.Result.IncompleteReleaseIds);
        Assert.Equal("missing", StatusOf(store, rid));
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(StagingPath("STORE.DAT")));   // untouched
        Assert.True(File.Exists(StagingPath("STRINT.CMD")));                                 // staging preserved
    }

    [Fact]  // an existing verified-empty staged file (earlier interrupted synthesis) is reused, not recreated
    public void ExistingEmptyStagedFile_IsAccepted()
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });
        Stage("STORE.DAT", Array.Empty<byte>());

        var o = Run(store, releases, new[] { rid });

        Assert.False(o.HasOp("empty-synthesized"));
        Assert.Equal("present", StatusOf(store, rid));
    }

    // ── (12) LOST / UNWANTED releases are never synthesized ─────────────────

    [Theory]
    [InlineData("lost")]
    [InlineData("unwanted")]
    [InlineData("pending")]
    public void NonMissingRelease_IsNotSynthesized(string status)
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store, statusOverride: status);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });

        var o = Run(store, releases, new[] { rid });

        Assert.False(o.HasOp("empty-synthesized"));
        Assert.True(o.HasOp("empty-synthesis-failed"));
        Assert.False(o.TransformCalled);
        Assert.Contains(rid, o.Result.IncompleteReleaseIds);
        Assert.False(File.Exists(StagingPath("STORE.DAT")));   // nothing materialized
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("unwanted")]
    public void NonMissingSynthesisOnlyRelease_IsNotResumable(string status)
    {
        var store = SeedOne(Store);
        Assert.Empty(DetectResumable(store, statusOverride: status));
    }

    // ── (15) completeness evaluates the COMPLETE authoritative set ─────────

    [Fact]
    public void PhysicalMissing_NothingSynthesized_ReleaseIncomplete()
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        // STRINT.CMD never matched/staged → the release is not ready; STORE.DAT must not be synthesized yet.

        var o = Run(store, releases, new[] { rid });

        Assert.True(o.HasOp("incomplete-skipped"));
        Assert.False(o.HasOp("empty-synthesized"));
        Assert.False(o.TransformCalled);
        Assert.False(File.Exists(StagingPath("STORE.DAT")));
        Assert.Equal("missing", StatusOf(store, rid));
    }

    [Fact]  // (2)(9) a hashless NON-empty requirement is unsatisfiable: even if a file sits in staging, never present
    public void HashlessNonEmptyRequirement_StagedAnyway_NeverPresent()
    {
        var store = SeedOne(Strint, new Rom("NOHASH.BIN", "12"));
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });
        Stage("NOHASH.BIN", new byte[12]);

        var o = Run(store, releases, new[] { rid });

        Assert.True(o.HasOp("requirement-unsatisfiable"));
        Assert.False(o.TransformCalled);
        Assert.Contains(rid, o.Result.IncompleteReleaseIds);
        Assert.Equal("missing", StatusOf(store, rid));
    }

    [Fact]  // (5) contradictory size-0 requirement: never synthesized, release incomplete
    public void ContradictoryZeroByteRequirement_NotSynthesized_ReleaseIncomplete()
    {
        var store = SeedOne(Strint, new Rom("CONTRA.DAT", "0", new string('b', 40)));
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });

        var o = Run(store, releases, new[] { rid });

        Assert.True(o.HasOp("requirement-unsatisfiable"));
        Assert.False(File.Exists(StagingPath("CONTRA.DAT")));
        Assert.False(o.TransformCalled);
        Assert.Equal("missing", StatusOf(store, rid));
    }

    [Fact]  // (13) existing hashed zero-byte requirement: satisfied by a matched/staged file, never "synthesized"
    public void HashedZeroByteRequirement_UnchangedBehavior()
    {
        var store = SeedOne(Strint, new Rom("EMPTY.BIN", "0", DatFileRequirement.EmptySha1));
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });

        var notStaged = Run(store, releases, new[] { rid });
        Assert.True(notStaged.HasOp("incomplete-skipped"));            // physical requirement → must be matched
        Assert.False(notStaged.HasOp("empty-synthesized"));
        Assert.False(File.Exists(StagingPath("EMPTY.BIN")));

        Stage("EMPTY.BIN", Array.Empty<byte>());                       // as if staged from a matched incoming file
        var staged = Run(store, Releases(store), new[] { rid });
        Assert.False(staged.HasOp("empty-synthesized"));
        Assert.Equal("present", StatusOf(store, rid));
    }

    [Fact]  // a synthesized requirement escaping the release root is rejected; nothing is written outside staging
    public void UnsafeSynthesizablePath_FailsWithoutWriting()
    {
        var store = SeedOne(Strint, new Rom(@"..\ESCAPE.DAT", "0"));
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });

        var o = Run(store, releases, new[] { rid });

        Assert.True(o.HasOp("empty-synthesis-failed"));
        Assert.False(File.Exists(Path.Combine(_stagingRoot, "ESCAPE.DAT")));
        Assert.Contains(rid, o.Result.IncompleteReleaseIds);
    }

    [Fact]  // a transform failure after synthesis keeps the release non-present and the managed source intact
    public void TransformFailureAfterSynthesis_SourcePreserved_NotPresent()
    {
        var store = SeedOne(Strint, Store);
        var releases = Releases(store);
        var rid = releases.Keys.Single();
        Stage("STRINT.CMD", new byte[] { 1 });

        var failed = new HashSet<string>(StringComparer.Ordinal);
        var log = new IngestionResult();
        LeafFinalizationEngine.FinalizeLeaf(
            new HashSet<string>(StringComparer.Ordinal) { rid }, Array.Empty<string>(),
            releases, store, _stagingRoot, _sourceRoot, "trs80", "leaf", IngestionPaths.SafeFolderName,
            log, new Progress<IngestionProgress>(_ => { }),
            failed, new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            a => failed.Add(a.Release.Id));

        Assert.Equal("missing", StatusOf(store, rid));
        Assert.True(File.Exists(SourcePath("STORE.DAT")));
        Assert.True(File.Exists(SourcePath("STRINT.CMD")));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// The shared DAT file requirement classification (<see cref="DatFileRequirement"/>), the per-file commit policy
/// (<see cref="FileCommitIdentityPolicy"/>), and how both shape matching: only physical requirements are indexed,
/// authoritative hashless zero-byte files are synthesizable, and nothing is ever inferred from missing hashes alone.
/// </summary>
public sealed class DatFileRequirementTests : IDisposable
{
    private readonly string _dir;

    public DatFileRequirementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkDatReq_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ReleaseFileRecord F(string rom, string size, string sha1 = "", string md5 = "", string crc = "")
        => new() { RomName = rom, Size = size, Sha1 = sha1, Md5 = md5, Crc = crc };

    private static readonly string NonEmptySha1 = new('a', 40);

    // ── Classification ──────────────────────────────────────────────────────

    [Theory]  // (1) authoritative size 0 + all hashes absent/blank → synthesizable
    [InlineData("0")]
    [InlineData(" 0 ")]
    [InlineData("00")]
    public void HashlessZeroSize_IsSynthesizableEmpty(string size)
    {
        Assert.Equal(DatFileRequirementKind.SynthesizableEmpty, DatFileRequirement.Classify(F("STORE.DAT", size)));
        Assert.Equal(DatFileRequirementKind.SynthesizableEmpty, DatFileRequirement.Classify(F("STORE.DAT", size, "  ", " ", "")));
    }

    [Theory]  // (2) hashless non-zero requirement → unmatchable, never synthesizable
    [InlineData("1")]
    [InlineData("4864")]
    public void HashlessNonZero_IsUnmatchable(string size)
    {
        var f = F("STRINT.CMD", size);
        Assert.Equal(DatFileRequirementKind.Unmatchable, DatFileRequirement.Classify(f));
        Assert.False(DatFileRequirement.IsSynthesizableEmpty(f));
    }

    [Theory]  // (3) missing / invalid size + no hashes → NOT synthesizable (missing hashes alone are never enough)
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-0")]
    [InlineData("0x0")]
    [InlineData("0.0")]
    [InlineData("1e0")]
    public void MissingOrInvalidSize_NoHashes_NotSynthesizable(string size)
    {
        var f = F("x.dat", size);
        Assert.Equal(DatFileRequirementKind.Unmatchable, DatFileRequirement.Classify(f));
        Assert.False(DatFileRequirement.IsSynthesizableEmpty(f));
        Assert.False(DatFileRequirement.IsDeclaredZeroSize(size));
    }

    [Fact]
    public void NullSize_NotZero() => Assert.False(DatFileRequirement.IsDeclaredZeroSize(null));

    [Theory]  // (4)(13) size 0 with hashes that ARE the empty-file hashes → a normal physical requirement
    [InlineData(DatFileRequirement.EmptySha1, "", "")]
    [InlineData("", DatFileRequirement.EmptyMd5, "")]
    [InlineData("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709", DatFileRequirement.EmptyMd5, DatFileRequirement.EmptyCrc32)]
    public void ZeroSize_WithValidEmptyHashes_IsPhysical(string sha1, string md5, string crc)
        => Assert.Equal(DatFileRequirementKind.Physical, DatFileRequirement.Classify(F("e.bin", "0", sha1, md5, crc)));

    [Theory]  // (5) size 0 + any hash incompatible with an empty file → contradictory, never synthesized or ignored
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "", "")]
    [InlineData("", "0123456789abcdef0123456789abcdef", "")]
    [InlineData("", "", "deadbeef")]
    [InlineData(DatFileRequirement.EmptySha1, "", "deadbeef")]   // one compatible hash does not excuse an incompatible one
    public void ZeroSize_WithIncompatibleHash_IsContradictory(string sha1, string md5, string crc)
    {
        var f = F("c.bin", "0", sha1, md5, crc);
        Assert.Equal(DatFileRequirementKind.ContradictoryZeroByte, DatFileRequirement.Classify(f));
        Assert.True(DatFileRequirement.IsUnsatisfiable(DatFileRequirement.Classify(f)));
    }

    [Fact]  // adjacent (not expanded into CRC matching): CRC-only non-zero requirement remains unmatchable
    public void CrcOnly_IsUnmatchable()
        => Assert.Equal(DatFileRequirementKind.Unmatchable, DatFileRequirement.Classify(F("c.bin", "10", crc: "12345678")));

    [Fact]
    public void NonZeroWithSha1_IsPhysical()
        => Assert.Equal(DatFileRequirementKind.Physical, DatFileRequirement.Classify(F("p.bin", "10", NonEmptySha1)));

    // ── Release-scoped identity + commit policy ─────────────────────────────

    [Fact]  // release-scoped identity: unique per (release, file); never the canonical empty-file hash
    public void SynthesizedIdentity_IsReleaseScoped()
    {
        var a = DatFileRequirement.SynthesizedEmptyIdentityKey("r1", "STORE.DAT");
        var b = DatFileRequirement.SynthesizedEmptyIdentityKey("r2", "STORE.DAT");
        var c = DatFileRequirement.SynthesizedEmptyIdentityKey("r1", "OTHER.DAT");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Contains("r1", a);
        Assert.Contains("STORE.DAT", a);
        Assert.DoesNotContain(DatFileRequirement.EmptySha1, a);
        Assert.False(a.StartsWith("sha1:", StringComparison.Ordinal));
    }

    [Fact]  // (9) C2: a hashless non-empty file can never be committed under a per-file strategy
    public void Policy_HashlessNonEmpty_CannotCommit()
    {
        var id = FileCommitIdentityPolicy.Resolve("r1", F("STRINT.CMD", "4864"));
        Assert.False(id.CanCommit);
        Assert.Equal("", id.ContentIdentityKey);
        Assert.NotEmpty(id.FailureReason);
    }

    [Fact]  // (5) contradictory zero-byte requirement cannot be committed
    public void Policy_Contradictory_CannotCommit()
        => Assert.False(FileCommitIdentityPolicy.Resolve("r1", F("c.bin", "0", NonEmptySha1)).CanCommit);

    [Fact]  // synthesizable empty file commits under its release-scoped identity
    public void Policy_SynthesizableEmpty_UsesReleaseScopedIdentity()
    {
        var id = FileCommitIdentityPolicy.Resolve("r1", F("STORE.DAT", "0"));
        Assert.True(id.CanCommit);
        Assert.Equal(DatFileRequirementKind.SynthesizableEmpty, id.Kind);
        Assert.Equal(DatFileRequirement.SynthesizedEmptyIdentityKey("r1", "STORE.DAT"), id.ContentIdentityKey);
    }

    [Fact]  // physical identity is byte-for-byte the historical DAT-declared key (existing artifacts stay recognized)
    public void Policy_Physical_KeepsHistoricalKey()
    {
        Assert.Equal($"sha1:{NonEmptySha1}", FileCommitIdentityPolicy.Resolve("r1", F("p.bin", "10", NonEmptySha1)).ContentIdentityKey);
        var md5 = new string('b', 32);
        Assert.Equal($"md5:{md5}", FileCommitIdentityPolicy.Resolve("r1", F("p.bin", "10", md5: md5)).ContentIdentityKey);
        Assert.Equal($"sha1:{DatFileRequirement.EmptySha1}",
            FileCommitIdentityPolicy.Resolve("r1", F("e.bin", "0", DatFileRequirement.EmptySha1)).ContentIdentityKey);
    }

    [Fact]  // verification: a size-0 requirement must be exactly 0 bytes; DAT SHA1 must match
    public void Policy_VerifySource()
    {
        Assert.Null(FileCommitIdentityPolicy.VerifySource(F("s", "0"), DatFileRequirementKind.SynthesizableEmpty, 0, DatFileRequirement.EmptySha1));
        Assert.NotNull(FileCommitIdentityPolicy.VerifySource(F("s", "0"), DatFileRequirementKind.SynthesizableEmpty, 1, NonEmptySha1));
        Assert.Null(FileCommitIdentityPolicy.VerifySource(F("p", "10", NonEmptySha1), DatFileRequirementKind.Physical, 10, NonEmptySha1.ToUpperInvariant()));
        Assert.NotNull(FileCommitIdentityPolicy.VerifySource(F("p", "10", NonEmptySha1), DatFileRequirementKind.Physical, 10, new string('c', 40)));
    }

    [Fact]  // (9) belt-and-braces: a release is fully committed only when every expected file is accounted for
    public void Policy_FullAccounting()
    {
        Assert.True(FileCommitIdentityPolicy.IsReleaseFullyAccountedFor(2, 2));
        Assert.False(FileCommitIdentityPolicy.IsReleaseFullyAccountedFor(2, 1));
        Assert.False(FileCommitIdentityPolicy.IsReleaseFullyAccountedFor(0, 0));
    }

    // ── Matching: only physical requirements consume incoming ───────────────

    private static List<ReleaseRecord> Rels(params string[] ids)
    {
        var l = new List<ReleaseRecord>();
        foreach (var id in ids) l.Add(new ReleaseRecord { Id = id, DatLineId = "leaf", Name = "Game " + id, Status = "missing" });
        return l;
    }

    [Fact]  // needs index excludes synthesizable / unmatchable / contradictory; keeps hashed zero-byte physical
    public void Needs_IndexesOnlyPhysicalRequirements()
    {
        var needs = LeafIngestionNeeds.Build("leaf", Rels("r1"),
            new Dictionary<string, List<ReleaseFileRecord>>
            {
                ["r1"] = new()
                {
                    F("STORE.DAT", "0"),                                  // synthesizable
                    F("NOHASH.BIN", "4864"),                              // unmatchable
                    F("CONTRA.BIN", "0", NonEmptySha1),                   // contradictory
                    F("EMPTY.BIN", "0", DatFileRequirement.EmptySha1),    // hashed zero-byte → physical
                    F("STRINT.CMD", "4864", new string('d', 40)),         // physical
                },
            },
            Array.Empty<string>());

        Assert.Equal(2, needs.Sha1Index.Count);
        Assert.True(needs.Sha1Index.ContainsKey(DatFileRequirement.EmptySha1));
        Assert.True(needs.Sha1Index.ContainsKey(new string('d', 40)));
        Assert.False(needs.Sha1Index.ContainsKey(NonEmptySha1));
        Assert.Empty(needs.Md5Index);
    }

    [Fact]  // (4)(13) a real zero-byte incoming file matches a requirement whose DAT declares the empty SHA1
    public void Match_ZeroByteFile_MatchesHashedEmptyRequirement()
    {
        var p = Path.Combine(_dir, "EMPTY.BIN");
        File.WriteAllBytes(p, Array.Empty<byte>());
        var sha1Index = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        { [DatFileRequirement.EmptySha1] = new() { ("r1", "EMPTY.BIN") } };
        var md5Index = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);

        var m = IngestionCandidateMatcher.HashAndMatch(p, sha1Index, md5Index);

        Assert.True(m.HashSucceeded);
        Assert.Equal(DatFileRequirement.EmptySha1, m.Sha1);
        Assert.Equal(("r1", "EMPTY.BIN"), Assert.Single(m.Targets));
    }

    [Fact]  // PO-5: a real zero-byte incoming file is NOT consumed by a hashless size-0 requirement (it is never indexed)
    public void Match_ZeroByteFile_NotConsumedByHashlessRequirement()
    {
        var needs = LeafIngestionNeeds.Build("leaf", Rels("r1"),
            new Dictionary<string, List<ReleaseFileRecord>> { ["r1"] = new() { F("STORE.DAT", "0") } },
            Array.Empty<string>());
        var p = Path.Combine(_dir, "STORE.DAT");
        File.WriteAllBytes(p, Array.Empty<byte>());

        var m = IngestionCandidateMatcher.HashAndMatch(p, needs.Sha1Index, needs.Md5Index);

        Assert.True(m.HashSucceeded);
        Assert.Empty(m.Targets);   // zero-match → normal zero-match / incoming-skip policy, never a false assimilation
    }

    [Fact]  // sanity: the constants really are the empty-file hashes
    public void EmptyHashConstants_AreCorrect()
    {
        Assert.Equal(DatFileRequirement.EmptySha1, Convert.ToHexString(SHA1.HashData(Array.Empty<byte>())).ToLowerInvariant());
        Assert.Equal(DatFileRequirement.EmptyMd5, Convert.ToHexString(MD5.HashData(Array.Empty<byte>())).ToLowerInvariant());
    }
}

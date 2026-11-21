using System;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// RVZ-SUPPORT-01 — Details "Source Verified" for the logical ISO of an RVZ whose MD5 DolphinTool did not compute
/// (<see cref="RvzIds.IsVerifiedLogicalSourceWithoutMd5"/>). The rule is narrow: only that RVZ's own linked logical
/// source with a NULL MD5, and only on SHA1 + size (+ CRC32 when declared). Everything else returns false so the
/// pre-existing "all declared DAT hashes must match" rule decides unchanged.
/// </summary>
public sealed class RvzSourceVerifiedTests
{
    private const string Sha1 = "1dd6b5f346ccdd096751a7a822e9ed2ea4650e0d";
    private const string Md5  = "75410aa57e7b8edafe3d4f6d143b13dd";
    private const string Crc  = "0cb5d407";
    private const long   Size = 8_388_608;

    private static ReleaseFileRecord Dat(string sha1 = Sha1, string md5 = Md5, string crc = Crc, string size = "8388608") =>
        new() { Id = "f1", ReleaseId = "r1", RomName = "Game (Japan).iso", Sha1 = sha1, Md5 = md5, Crc = crc, Size = size };

    private static SourceArtifactRecord Src(string? md5 = null, string sha1 = Sha1, string? crc = Crc, long size = Size) => new()
    {
        Id = "sa1", ContentIdentityKey = $"sha1:{Sha1}", SourceSizeBytes = size,
        HashedSourceSha1 = sha1, HashedSourceMd5 = md5, HashedSourceCrc32 = crc, VerifiedAtUtc = DateTime.UtcNow,
    };

    private static DerivedArtifactRecord Rvz(string strategy = RvzIds.TransformId, string sourceId = "sa1", string tier = "A") => new()
    {
        Id = "da1", StorageStrategyId = strategy, SourceArtifactId = sourceId, ContentIdentityKey = $"sha1:{Sha1}",
        FileName = "Game (Japan).rvz", HashedDerivedSha1 = "0dff6b6a36f700909ccb567cc17a99dadcfa61c6", ArchiveTier = tier,
    };

    [Fact]
    public void Md5NotComputed_Sha1SizeCrcMatch_LinkedRvz_IsVerified()
        => Assert.True(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(), Rvz()));

    [Fact]
    public void DatWithoutCrc_Sha1AndSizeMatch_IsVerified()
        => Assert.True(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(crc: ""), Src(crc: null), Rvz()));

    [Fact]
    public void DatCrcWithoutLeadingZeros_IsNormalized()
        => Assert.True(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(crc: "CB5D407"), Src(), Rvz()));

    [Fact]
    public void Sha1Mismatch_IsNotVerified()
        => Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(sha1: new string('0', 40)), Rvz()));

    [Fact]
    public void DatWithoutSha1_IsNotVerified()
        => Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(sha1: ""), Src(), Rvz()));

    [Theory]
    [InlineData("8388609")]
    [InlineData("")]                // logical size must be checked against a DAT size
    [InlineData("abc")]
    public void SizeMismatchOrUndeclared_IsNotVerified(string datSize)
        => Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(size: datSize), Src(), Rvz()));

    [Fact]
    public void DeclaredCrcMismatch_IsNotVerified()
        => Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(crc: "00000000"), Rvz()));

    [Fact]
    public void DeclaredCrc_ButSourceCrcMissing_IsNotVerified()
        => Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(crc: null), Rvz()));

    [Fact]
    public void ComputedMd5_IsNeverRescued_GenericRuleDecides()
    {
        // A computed (even wrong) MD5 is not "omitted": this rule steps aside, the generic rule compares it.
        Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(md5: Md5), Rvz()));
        Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(md5: new string('f', 32)), Rvz()));
    }

    [Theory]
    [InlineData("chd_compression")]
    [InlineData("no_compression")]
    [InlineData("zip_compression")]
    public void OtherFormats_WithNullMd5_AreUnchanged(string strategy)
        => Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(), Rvz(strategy: strategy)));

    [Fact]
    public void SourceNotLinkedFromThisRvz_IsNotVerified()
        => Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(), Rvz(sourceId: "other")));

    [Fact]
    public void MissingSourceOrDerived_IsNotVerified()
    {
        Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), null, Rvz()));
        Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(), null));
    }

    [Fact]
    public void TierAMetadataAlone_NeverVerifies()
    {
        // Tier A RVZ, linked, but the logical SHA1 does not match: tier is never evidence.
        Assert.False(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(sha1: new string('0', 40)), Rvz(tier: "A")));
        // Tier is irrelevant either way: a verified logical source stays verified whatever tier is recorded.
        Assert.True(RvzIds.IsVerifiedLogicalSourceWithoutMd5(Dat(), Src(), Rvz(tier: "B")));
    }
}
